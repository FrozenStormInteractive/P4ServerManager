#!/usr/bin/env python3
"""Builds the Synology packages (.spk) of P4 Server Manager, one per architecture.

Usage, from anywhere:
    python packaging/spk/build.py --version 0.1.0 --build-number 1
    python packaging/spk/build.py --arch x86_64 --skip-publish

The daemon and the CLI are published by packaging/publish.py, as for the zip archives. Needs the .NET SDK and npm to
publish, and Python 3.9 or later. Only the standard library is used, so that the file modes and line endings of the
package do not depend on the host.
"""

import argparse
import gzip
import hashlib
import io
import sys
import tarfile
import time
from datetime import datetime
from pathlib import Path

SPK_DIR = Path(__file__).resolve().parent
REPO_ROOT = SPK_DIR.parent.parent

# The publication of the daemon and the CLI, shared with the zip archives.
sys.path.insert(0, str(SPK_DIR.parent))
import publish  # noqa: E402

# DSM architecture -> .NET runtime identifier. Perforce publishes no 32-bit ARM p4d.
ARCHITECTURES = {
    "x86_64": "linux-x64",
    "armv8": "linux-arm64",
}

# .NET 10 needs glibc 2.27. DSM 7.0 and 7.1 ship glibc 2.26, DSM 7.2 ships 2.36: 7.2 GA is the oldest supported.
OS_MIN_VERSION = "7.2-64561"

PACKAGE_NAME = publish.PACKAGE_NAME

# Text files the package carries: their placeholders are replaced and their line endings forced to LF.
PLACEHOLDER_SUFFIXES = {"", ".sh", ".sc", ".json"}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--arch", choices=sorted(ARCHITECTURES), action="append",
                        help="DSM architecture to build; repeat it for several. All by default.")
    parser.add_argument("--version", default="0.1.0", help="Version of the package, x.y.z (default: %(default)s).")
    parser.add_argument("--build-number", type=int, default=1,
                        help="Build number, appended to the version as -nnnn (default: %(default)s).")
    parser.add_argument("--ui-port", type=int, default=8666, help="Port of the web UI (default: %(default)s).")
    parser.add_argument("--instance-ports", default="1666:1699",
                        help="Ports opened for the P4 servers in the DSM firewall, as first:last "
                             "(default: %(default)s).")
    parser.add_argument("--configuration", default="Release", help="Build configuration (default: %(default)s).")
    parser.add_argument("--output", type=Path, default=REPO_ROOT / "artifacts" / "spk",
                        help="Output folder (default: artifacts/spk).")
    parser.add_argument("--skip-publish", action="store_true",
                        help="Reuse the publish output of a previous run instead of running dotnet publish.")
    parser.add_argument("--beta", action="store_true", help="Mark the package as a beta in Package Center.")
    args = parser.parse_args()

    if not (1 <= args.ui_port <= 65535):
        parser.error("--ui-port must be between 1 and 65535.")
    first, _, last = args.instance_ports.partition(":")
    if not (first.isdigit() and last.isdigit() and 1 <= int(first) <= int(last) <= 65535):
        parser.error("--instance-ports must be first:last, for example 1666:1699.")

    version = f"{args.version}-{args.build_number:04d}"
    placeholders = {
        "@UI_PORT@": str(args.ui_port),
        "@INSTANCE_PORTS@": args.instance_ports,
    }

    for arch in args.arch or sorted(ARCHITECTURES):
        rid = ARCHITECTURES[arch]
        publish_dir = args.output / "publish" / rid
        if not args.skip_publish:
            publish.publish_apps(rid, args.configuration, publish_dir)
        publish.check_publish_output(publish_dir)
        cli_files = publish.cli_only_files(publish_dir)

        spk_path = args.output / f"{PACKAGE_NAME}-{arch}-{version}.spk"
        build_spk(arch, version, args.ui_port, args.beta, placeholders, publish_dir, cli_files, spk_path)
        print(f"Built {spk_path}")

    return 0


def build_spk(arch: str, version: str, ui_port: int, beta: bool, placeholders: dict, publish_dir: Path,
              cli_files: list, spk_path: Path) -> None:
    package_tgz, extract_size = build_package_tgz(publish_dir, cli_files, placeholders)

    # Package Center checks the MD5 of package.tgz against the checksum of INFO.
    checksum = hashlib.md5(package_tgz).hexdigest()
    info = make_info(arch, version, ui_port, beta, extract_size, checksum)

    spk_path.parent.mkdir(parents=True, exist_ok=True)
    with tarfile.open(spk_path, "w", format=tarfile.GNU_FORMAT) as spk:
        add_bytes(spk, "INFO", info.encode())
        add_bytes(spk, "package.tgz", package_tgz)
        add_bytes(spk, "LICENSE", text_bytes(REPO_ROOT / "LICENSE.md", placeholders))
        add_file(spk, SPK_DIR / "PACKAGE_ICON.png", "PACKAGE_ICON.PNG", placeholders)
        add_file(spk, SPK_DIR / "PACKAGE_ICON_256.png", "PACKAGE_ICON_256.PNG", placeholders)
        add_tree(spk, SPK_DIR / "conf", "conf", placeholders)
        add_tree(spk, SPK_DIR / "scripts", "scripts", placeholders, executable=lambda path: True)
        add_tree(spk, SPK_DIR / "WIZARD_UIFILES", "WIZARD_UIFILES", placeholders,
                 executable=lambda path: path.suffix == ".sh")


def build_package_tgz(publish_dir: Path, cli_files: list, placeholders: dict) -> tuple:
    """The files installed in /var/packages/P4ServerManager/target, and their size in bytes."""
    buffer = io.BytesIO()
    # A fixed mtime in the gzip header keeps two builds of the same files identical.
    with gzip.GzipFile(fileobj=buffer, mode="wb", compresslevel=9, mtime=0) as compressed:
        with tarfile.open(fileobj=compressed, mode="w", format=tarfile.GNU_FORMAT) as package:
            add_tree(package, publish_dir / "daemon", "app", None, executable=publish.is_elf)
            for path in cli_files:
                name = "app/" + path.relative_to(publish_dir / "cli").as_posix()
                add_file(package, path, name, None, 0o755 if publish.is_elf(path) else 0o644)
            add_tree(package, SPK_DIR / "package", ".", placeholders,
                     executable=lambda path: path.parent.name == "bin")
            extract_size = sum(member.size for member in package.getmembers())
    return buffer.getvalue(), extract_size


def make_info(arch: str, version: str, ui_port: int, beta: bool, extract_size: int, checksum: str) -> str:
    info = {
        "package": PACKAGE_NAME,
        "version": version,
        "os_min_ver": OS_MIN_VERSION,
        "arch": arch,
        "displayname": "P4 Server Manager",
        "description": "Create, run and maintain Perforce P4 servers from a web UI or the command line. Each "
                       "server runs the p4d version you choose, downloaded from Perforce. Unofficial: not "
                       "affiliated with Perforce Software.",
        "maintainer": "Frozen Storm Interactive",
        "maintainer_url": "https://github.com/FrozenStormInteractive/P4ServerManager",
        "distributor": "Frozen Storm Interactive",
        "distributor_url": "https://github.com/FrozenStormInteractive/P4ServerManager",
        "support_url": "https://github.com/FrozenStormInteractive/P4ServerManager/issues",
        "thirdparty": "yes",
        "dsmuidir": "ui",
        "dsmappname": "com.FrozenStormInteractive.P4ServerManager",
        "adminprotocol": "http",
        "adminport": str(ui_port),
        "adminurl": "/",
        "install_dep_services": "syno-share.target",
        "start_dep_services": "network-online.target syno-share.target",
        "extractsize": str(extract_size // 1024),
        "checksum": checksum,
        "create_time": datetime.now().strftime("%Y%m%d-%H:%M:%S"),
    }
    if beta:
        info["beta"] = "yes"
    return "".join(f'{key}="{value}"\n' for key, value in info.items())


def add_tree(archive: tarfile.TarFile, source: Path, arcname: str, placeholders, executable=lambda path: False):
    """Adds a folder, sorted for reproducible archives. Files are 0644, or 0755 when executable(path) is true."""
    for path in sorted(source.rglob("*")):
        name = path.relative_to(source).as_posix()
        name = name if arcname == "." else f"{arcname}/{name}"
        if path.is_dir():
            info = tarfile.TarInfo(name)
            info.type = tarfile.DIRTYPE
            info.mode = 0o755
            archive.addfile(normalize(info))
        else:
            add_file(archive, path, name, placeholders, 0o755 if executable(path) else 0o644)


def add_file(archive: tarfile.TarFile, path: Path, name: str, placeholders, mode: int = 0o644) -> None:
    if placeholders is not None and path.suffix in PLACEHOLDER_SUFFIXES:
        add_bytes(archive, name, text_bytes(path, placeholders), mode)
    else:
        info = tarfile.TarInfo(name)
        info.size = path.stat().st_size
        info.mode = mode
        with path.open("rb") as stream:
            archive.addfile(normalize(info), stream)


def add_bytes(archive: tarfile.TarFile, name: str, data: bytes, mode: int = 0o644) -> None:
    info = tarfile.TarInfo(name)
    info.size = len(data)
    info.mode = mode
    archive.addfile(normalize(info), io.BytesIO(data))


def normalize(info: tarfile.TarInfo) -> tarfile.TarInfo:
    info.uid = info.gid = 0
    info.uname = info.gname = "root"
    info.mtime = int(time.time())
    return info


def text_bytes(path: Path, placeholders: dict) -> bytes:
    """A text file with LF line endings and its placeholders replaced. DSM runs the scripts with /bin/sh, which
    fails on CRLF."""
    text = path.read_text(encoding="utf-8-sig").replace("\r\n", "\n")
    for placeholder, value in placeholders.items():
        text = text.replace(placeholder, value)
    return text.encode()


if __name__ == "__main__":
    sys.exit(main())

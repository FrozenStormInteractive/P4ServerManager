#!/usr/bin/env python3
"""Builds the Debian packages (.deb) of P4 Server Manager, one per architecture.

Usage, from anywhere:
    python packaging/deb/build.py --version 0.1.0 --build-number 1
    python packaging/deb/build.py --arch amd64 --skip-publish

The daemon and the CLI are published by packaging/publish.py, as for the zip archives. Needs the .NET SDK and npm to
publish, and Python 3.9 or later. Only the standard library is used, without dpkg-deb, so that the package builds on
any host and its file modes and line endings do not depend on it.
"""

import argparse
import hashlib
import io
import lzma
import os
import sys
import tarfile
import time
from pathlib import Path

DEB_DIR = Path(__file__).resolve().parent
REPO_ROOT = DEB_DIR.parent.parent

# The publication of the daemon and the CLI, shared with the zip archives.
sys.path.insert(0, str(DEB_DIR.parent))
import publish  # noqa: E402

# Debian architecture -> .NET runtime identifier. Perforce publishes no 32-bit ARM p4d.
ARCHITECTURES = {
    "amd64": "linux-x64",
    "arm64": "linux-arm64",
}

PACKAGE_NAME = "p4servermanager"
INSTALL_DIR = "usr/lib/p4servermanager"

# What a self-contained .NET 10 app needs, without ICU: the daemon runs with the invariant culture. .NET 10 needs
# glibc 2.27. OpenSSL serves HTTPS: the Perforce FTP, the backup destinations.
DEPENDS = [
    "adduser",
    "ca-certificates",
    "libc6 (>= 2.27)",
    "libgcc-s1 | libgcc1",
    "libstdc++6",
    "libssl3t64 | libssl3 | libssl1.1",
]

# Installed files that dpkg keeps when the administrator changed them.
CONFFILES = ["/etc/default/p4servermanager"]

# Files of files/ that are executable.
EXECUTABLES = {f"{INSTALL_DIR}/stop-servers"}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--arch", choices=sorted(ARCHITECTURES), action="append",
                        help="Debian architecture to build; repeat it for several. All by default.")
    parser.add_argument("--version", default="0.1.0", help="Version of the package, x.y.z (default: %(default)s).")
    parser.add_argument("--build-number", type=int, default=1,
                        help="Build number, the Debian revision of the package (default: %(default)s).")
    parser.add_argument("--ui-port", type=int, default=8666, help="Default port of the web UI (default: %(default)s).")
    parser.add_argument("--maintainer", default="Frozen Storm Interactive",
                        help="Maintainer field of the package, 'Name <email>' (default: %(default)s).")
    parser.add_argument("--configuration", default="Release", help="Build configuration (default: %(default)s).")
    parser.add_argument("--output", type=Path, default=REPO_ROOT / "artifacts" / "deb",
                        help="Output folder (default: artifacts/deb).")
    parser.add_argument("--skip-publish", action="store_true",
                        help="Reuse the publish output of a previous run instead of running dotnet publish.")
    args = parser.parse_args()

    if not (1 <= args.ui_port <= 65535):
        parser.error("--ui-port must be between 1 and 65535.")

    version = f"{args.version}-{args.build_number}"
    placeholders = {"@UI_PORT@": str(args.ui_port)}
    # Reproducible builds set SOURCE_DATE_EPOCH.
    mtime = int(os.environ.get("SOURCE_DATE_EPOCH", time.time()))

    for arch in args.arch or sorted(ARCHITECTURES):
        rid = ARCHITECTURES[arch]
        publish_dir = args.output / "publish" / rid
        if not args.skip_publish:
            publish.publish_apps(rid, args.configuration, publish_dir)
        publish.check_publish_output(publish_dir)
        cli_files = publish.cli_only_files(publish_dir)

        deb_path = args.output / f"{PACKAGE_NAME}_{version}_{arch}.deb"
        build_deb(arch, version, args.maintainer, placeholders, publish_dir, cli_files, mtime, deb_path)
        print(f"Built {deb_path}")

    return 0


def build_deb(arch: str, version: str, maintainer: str, placeholders: dict, publish_dir: Path, cli_files: list,
              mtime: int, deb_path: Path) -> None:
    data = DataTree(mtime)
    data.add_tree(publish_dir / "daemon", f"{INSTALL_DIR}/app", executable=publish.is_elf)
    for path in cli_files:
        name = f"{INSTALL_DIR}/app/" + path.relative_to(publish_dir / "cli").as_posix()
        data.add_file(name, path.read_bytes(), 0o755 if publish.is_elf(path) else 0o644)
    for path in sorted((DEB_DIR / "files").rglob("*")):
        if path.is_file():
            name = path.relative_to(DEB_DIR / "files").as_posix()
            data.add_file(name, text_bytes(path, placeholders), 0o755 if name in EXECUTABLES else 0o644)
    # The CLI defaults to the socket of the service. Its apphost resolves the link to find the runtime.
    data.add_symlink("usr/bin/p4sm", "../lib/p4servermanager/app/P4ServerManager.Cli")
    data.add_file(f"usr/share/doc/{PACKAGE_NAME}/copyright", text_bytes(REPO_ROOT / "LICENSE.md", {}), 0o644)

    control = make_control(arch, version, maintainer, data.installed_size_kib())
    control_tar = TarBuilder(mtime)
    control_tar.add_file("control", control.encode(), 0o644)
    control_tar.add_file("md5sums", data.md5sums().encode(), 0o644)
    control_tar.add_file("conffiles", "".join(f"{path}\n" for path in CONFFILES).encode(), 0o644)
    for script in ("postinst", "prerm", "postrm"):
        control_tar.add_file(script, text_bytes(DEB_DIR / "scripts" / script, placeholders), 0o755)

    deb_path.parent.mkdir(parents=True, exist_ok=True)
    with deb_path.open("wb") as deb:
        deb.write(b"!<arch>\n")
        write_ar_member(deb, "debian-binary", b"2.0\n", mtime)
        write_ar_member(deb, "control.tar.xz", xz(control_tar.bytes()), mtime)
        write_ar_member(deb, "data.tar.xz", xz(data.bytes()), mtime)


def make_control(arch: str, version: str, maintainer: str, installed_size: int) -> str:
    description = [
        "Create, run and maintain Perforce P4 servers",
        "P4 Server Manager is a self-hosted daemon that manages any number of p4d",
        "servers on one machine, from a web UI or the p4sm command line. Each server",
        "runs the p4d build you choose, downloaded on demand from Perforce. The",
        "package ships no p4d binary.",
        ".",
        "Unofficial: not affiliated with Perforce Software.",
    ]
    fields = {
        "Package": PACKAGE_NAME,
        "Version": version,
        "Architecture": arch,
        "Maintainer": maintainer,
        "Installed-Size": str(installed_size),
        "Depends": ", ".join(DEPENDS),
        "Section": "vcs",
        "Priority": "optional",
        "Homepage": "https://github.com/FrozenStormInteractive/P4ServerManager",
        "Description": description[0] + "".join(f"\n {line}" for line in description[1:]),
    }
    return "".join(f"{key}: {value}\n" for key, value in fields.items())


class TarBuilder:
    """A tar archive in memory, of entries owned by root with one mtime, in the order they are added."""

    def __init__(self, mtime: int):
        self.mtime = mtime
        self.buffer = io.BytesIO()
        self.archive = tarfile.open(fileobj=self.buffer, mode="w", format=tarfile.GNU_FORMAT)

    def add_file(self, name: str, data: bytes, mode: int) -> None:
        info = self._info(name, tarfile.REGTYPE, mode)
        info.size = len(data)
        self.archive.addfile(info, io.BytesIO(data))

    def add_dir(self, name: str) -> None:
        self.archive.addfile(self._info(name, tarfile.DIRTYPE, 0o755))

    def add_symlink(self, name: str, target: str) -> None:
        info = self._info(name, tarfile.SYMTYPE, 0o777)
        info.linkname = target
        self.archive.addfile(info)

    def bytes(self) -> bytes:
        self.archive.close()
        return self.buffer.getvalue()

    def _info(self, name: str, type_: bytes, mode: int) -> tarfile.TarInfo:
        info = tarfile.TarInfo(f"./{name}" if name else ".")
        info.type = type_
        info.mode = mode
        info.uid = info.gid = 0
        info.uname = info.gname = "root"
        info.mtime = self.mtime
        return info


class DataTree:
    """The installed files, sorted, each folder listed once before its content, as dpkg expects."""

    def __init__(self, mtime: int):
        self.mtime = mtime
        self.entries = {}

    def add_tree(self, source: Path, name: str, executable) -> None:
        for path in sorted(source.rglob("*")):
            if path.is_file():
                relative = path.relative_to(source).as_posix()
                self.add_file(f"{name}/{relative}", path.read_bytes(), 0o755 if executable(path) else 0o644)

    def add_file(self, name: str, data: bytes, mode: int) -> None:
        self.entries[name] = ("file", data, mode)

    def add_symlink(self, name: str, target: str) -> None:
        self.entries[name] = ("symlink", target, 0o777)

    def installed_size_kib(self) -> int:
        size = sum(len(data) for kind, data, _ in self.entries.values() if kind == "file")
        return (size + 1023) // 1024

    def md5sums(self) -> str:
        return "".join(f"{hashlib.md5(data).hexdigest()}  {name}\n"
                       for name, (kind, data, _) in sorted(self.entries.items()) if kind == "file")

    def bytes(self) -> bytes:
        tar = TarBuilder(self.mtime)
        tar.add_dir("")
        written = set()
        for name, (kind, data, mode) in sorted(self.entries.items()):
            parts = name.split("/")
            for depth in range(1, len(parts)):
                folder = "/".join(parts[:depth])
                if folder not in written:
                    tar.add_dir(folder)
                    written.add(folder)
            if kind == "file":
                tar.add_file(name, data, mode)
            else:
                tar.add_symlink(name, data)
        return tar.bytes()


def write_ar_member(stream, name: str, data: bytes, mtime: int) -> None:
    header = f"{name:<16}{mtime:<12}{0:<6}{0:<6}{'100644':<8}{len(data):<10}`\n"
    stream.write(header.encode("ascii"))
    stream.write(data)
    if len(data) % 2:
        stream.write(b"\n")


def xz(data: bytes) -> bytes:
    return lzma.compress(data, format=lzma.FORMAT_XZ, check=lzma.CHECK_CRC64, preset=6)


def text_bytes(path: Path, placeholders: dict) -> bytes:
    """A text file with LF line endings and its placeholders replaced: the scripts fail on CRLF."""
    text = path.read_text(encoding="utf-8-sig").replace("\r\n", "\n")
    for placeholder, value in placeholders.items():
        text = text.replace(placeholder, value)
    return text.encode()


if __name__ == "__main__":
    sys.exit(main())

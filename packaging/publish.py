#!/usr/bin/env python3
"""Publishes the daemon, with its web UI, and the CLI of P4 Server Manager, and packs them in a zip archive.

Usage, from anywhere:
    python packaging/publish.py --version 0.1.0 --build-number 1
    python packaging/publish.py --rid linux-x64 --skip-publish

Each archive holds one folder, where the daemon and the CLI share the files of the .NET runtime. packaging/spk/build.py
imports this file for the publication, so that the Synology packages carry the same binaries.

Needs the .NET SDK and npm to publish, and Python 3.9 or later. Only the standard library is used.
"""

import argparse
import filecmp
import shutil
import stat
import subprocess
import sys
import time
import zipfile
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
DAEMON_PROJECT = REPO_ROOT / "src" / "P4ServerManager.Daemon" / "P4ServerManager.Daemon.csproj"
CLI_PROJECT = REPO_ROOT / "src" / "P4ServerManager.Cli" / "P4ServerManager.Cli.csproj"

PACKAGE_NAME = "P4ServerManager"

# Perforce publishes no 32-bit ARM p4d.
RUNTIME_IDENTIFIERS = ["linux-arm64", "linux-x64"]


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--rid", choices=RUNTIME_IDENTIFIERS, action="append",
                        help="Runtime identifier to build; repeat it for several. All by default.")
    parser.add_argument("--version", default="0.1.0", help="Version of the archive, x.y.z (default: %(default)s).")
    parser.add_argument("--build-number", type=int, default=1,
                        help="Build number, appended to the version as -nnnn (default: %(default)s).")
    parser.add_argument("--configuration", default="Release", help="Build configuration (default: %(default)s).")
    parser.add_argument("--output", type=Path, default=REPO_ROOT / "artifacts" / "zip",
                        help="Output folder (default: artifacts/zip).")
    parser.add_argument("--skip-publish", action="store_true",
                        help="Reuse the publish output of a previous run instead of running dotnet publish.")
    args = parser.parse_args()

    version = f"{args.version}-{args.build_number:04d}"

    for rid in args.rid or RUNTIME_IDENTIFIERS:
        publish_dir = args.output / "publish" / rid
        if not args.skip_publish:
            publish_apps(rid, args.configuration, publish_dir)
        check_publish_output(publish_dir)
        cli_files = cli_only_files(publish_dir)

        name = f"{PACKAGE_NAME}-{rid}-{version}"
        zip_path = args.output / f"{name}.zip"
        build_zip(name, publish_dir, cli_files, zip_path)
        print(f"Built {zip_path}")

    return 0


def publish_apps(rid: str, configuration: str, publish_dir: Path) -> None:
    """Publishes the daemon, with the UI, and the CLI, self-contained for the runtime identifier, to the daemon and
    cli folders of publish_dir."""
    if publish_dir.exists():
        shutil.rmtree(publish_dir)

    # DSM has no ICU: the daemon runs with the invariant culture. The UI does its own localization.
    common = ["-c", configuration, "-r", rid, "--self-contained", "-p:InvariantGlobalization=true",
              "-p:DebugType=None", "-p:GenerateDocumentationFile=false"]
    run(["dotnet", "publish", str(DAEMON_PROJECT), *common, "-o", str(publish_dir / "daemon")])
    run(["dotnet", "publish", str(CLI_PROJECT), *common, "-o", str(publish_dir / "cli")])


def check_publish_output(publish_dir: Path) -> None:
    required = [
        publish_dir / "daemon" / "P4ServerManager.Daemon",
        publish_dir / "daemon" / "wwwroot" / "index.html",
        publish_dir / "cli" / "P4ServerManager.Cli",
    ]
    missing = [str(path) for path in required if not path.is_file()]
    if missing:
        sys.exit("The publish output is incomplete. Missing:\n  " + "\n  ".join(missing))


def cli_only_files(publish_dir: Path) -> list:
    """The files of the CLI that the daemon does not have.

    The daemon and the CLI are installed in one folder, so that they share the files of the .NET runtime. A self-
    contained app only loads what its own deps.json lists, so both run from there as long as the files they have in
    common are the same. They stop being the same when the two projects use different versions of a package: the
    build fails then, instead of installing a file one of the apps cannot load.
    """
    daemon_dir, cli_dir = publish_dir / "daemon", publish_dir / "cli"
    own, conflicts = [], []
    for path in sorted(cli_dir.rglob("*")):
        if path.is_dir():
            continue
        twin = daemon_dir / path.relative_to(cli_dir)
        if not twin.exists():
            own.append(path)
        elif not filecmp.cmp(path, twin, shallow=False):
            conflicts.append(path.relative_to(cli_dir).as_posix())
    if conflicts:
        sys.exit("The daemon and the CLI publish different versions of the same files, so they cannot share one "
                 "folder. Align the package versions of the two projects:\n  " + "\n  ".join(conflicts))
    return own


def build_zip(name: str, publish_dir: Path, cli_files: list, zip_path: Path) -> None:
    """The daemon, the files of the CLI the daemon does not have and the license, in one folder named name."""
    daemon_dir, cli_dir = publish_dir / "daemon", publish_dir / "cli"
    entries = {path.relative_to(daemon_dir).as_posix(): path for path in daemon_dir.rglob("*")}
    for path in cli_files:
        relative = path.relative_to(cli_dir)
        entries[relative.as_posix()] = path
        # The folders of a CLI file the daemon does not have.
        for parent in relative.parents:
            if parent != Path("."):
                entries.setdefault(parent.as_posix(), cli_dir / parent)
    entries["LICENSE.md"] = REPO_ROOT / "LICENSE.md"

    zip_path.parent.mkdir(parents=True, exist_ok=True)
    timestamp = time.localtime()[:6]
    with zipfile.ZipFile(zip_path, "w", compression=zipfile.ZIP_DEFLATED) as archive:
        archive.writestr(zip_entry(f"{name}/", stat.S_IFDIR | 0o755, timestamp), b"")
        for relative, path in sorted(entries.items()):
            if path.is_dir():
                archive.writestr(zip_entry(f"{name}/{relative}/", stat.S_IFDIR | 0o755, timestamp), b"")
            else:
                mode = 0o755 if is_elf(path) else 0o644
                info = zip_entry(f"{name}/{relative}", stat.S_IFREG | mode, timestamp)
                with path.open("rb") as source, archive.open(info, "w") as target:
                    shutil.copyfileobj(source, target, 1024 * 1024)


def zip_entry(name: str, mode: int, timestamp: tuple) -> zipfile.ZipInfo:
    """An entry made on Unix, whatever the host: unzip restores its mode, so the executables stay executable."""
    info = zipfile.ZipInfo(name, timestamp)
    info.create_system = 3
    info.external_attr = mode << 16
    if stat.S_ISDIR(mode):
        info.external_attr |= 0x10  # MS-DOS directory flag
    else:
        info.compress_type = zipfile.ZIP_DEFLATED
    return info


def is_elf(path: Path) -> bool:
    with path.open("rb") as stream:
        return stream.read(4) == b"\x7fELF"


def run(command: list) -> None:
    print("+ " + " ".join(command), flush=True)
    subprocess.run(command, cwd=REPO_ROOT, check=True)


if __name__ == "__main__":
    sys.exit(main())

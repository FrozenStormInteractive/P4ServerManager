# Synology package

Sources of the Synology package (`.spk`) of P4 Server Manager. `build.py` builds one package per architecture.

## Requirements

- DSM 7.2 or later. (.NET 10 needs glibc 2.27, DSM 7.0 and 7.1 ship glibc 2.26 and DSM 7.2 ships 2.36.)
- An `x86_64` or `armv8` (64-bit ARM) NAS. Perforce publishes no 32-bit ARM p4d.

## Build

From the repository root, with the .NET SDK, npm and Python 3.9 or later:

```bash
python packaging/spk/build.py --version 0.1.0 --build-number 1
```

The packages land in `artifacts/spk/`, named `P4ServerManager-<arch>-<version>-<build>.spk`. Useful options:

| Option | Default | Meaning |
| --- | --- | --- |
| `--arch x86_64` / `--arch armv8` | both | Architecture to build, can be repeated. |
| `--ui-port` | `8666` | Port of the web UI and the REST API. |
| `--instance-ports` | `1666:1699` | Port range the DSM firewall offers for the P4 servers. |
| `--skip-publish` | | Reuse the output of the last `dotnet publish`. |
| `--beta` | | Mark the package as a beta in Package Center. |

## Layout

| Path | Goes to | Content |
| --- | --- | --- |
| `conf/` | `conf/` of the SPK | `privilege` (runs as the `Perforce` user, see below) and `resource` (shared folder, firewall ports, CLI link). |
| `scripts/` | `scripts/` of the SPK | DSM lifecycle scripts. `common` holds the shared functions. |
| `WIZARD_UIFILES/` | `WIZARD_UIFILES/` of the SPK | Install wizard (a script, because its steps depend on the NAS) and uninstall wizard. |
| `package/` | `/var/packages/P4ServerManager/target` | UI entry of the DSM main menu, firewall service file, `bin/p4sm`. |
| `PACKAGE_ICON*.PNG`, `package/ui/images/` | | Icons of Package Center and of the DSM main menu. |

## On the NAS

- **Data**: the `P4ServerManager` shared folder is the data directory of the daemon: `p4servermanager.db`, `archive/`
  (every p4d build, compressed with zstd), `builds/` (the builds the servers use, extracted), `instances/` (logs,
  pid files and the default roots of the servers) and `keys/`. `Daemon__ArchiveDirectory` moves the archive to another
  folder.
- **Super users**: the daemon stores the super user of each server, to administer it, with its password encrypted by
  the keys in `keys/`, readable by the daemon only. A backup of the data directory must include `keys/`: without them,
  the stored passwords cannot be read, and each super user must be set again from the instance page or with
  `p4sm instance super-user set`. The daemon checks each account at startup, when its server starts, and every hour
  (`Daemon__SuperUserCheckInterval`).
- **p4d builds**: Settings > p4d builds in the web UI, or `p4sm build`. The daemon can check the `SHA256SUMS` of the
  Perforce FTP on a schedule and download the new builds of the chosen releases, and keeps the newest builds of each
  release. A build can also be imported from p4d and p4, or from `helix-core-server.tgz`. Uploads go through the UI
  port: a reverse proxy in front of it must accept requests of about 100 MB.
- **Daemon**: `start-stop-status` runs `daemon/P4ServerManager.Daemon` in the background, listening on every
  interface on the UI port, and on the socket of the CLI. Its log is `/var/packages/P4ServerManager/var/daemon.log`, the previous one
  `daemon.log.1`.
- **Stopping the package** stops the daemon, then each p4d with SIGTERM, a clean shutdown for p4d. Once DSM stops a
  package it may kill what is left of its processes, so the servers are not left to that. They start again with the
  package when auto-start is on. A daemon crash or restart outside of Package Center leaves them running.
- **CLI**: `p4sm`, linked in `/usr/local/bin`. It talks to the daemon through the Unix socket
  `/var/packages/P4ServerManager/var/daemon.sock`, open to the `Perforce` user and group only: run it with `sudo`, or
  as a member of the `Perforce` group. `P4SM_SOCKET` or `--socket` points it to another socket.
- **Uninstallation**: the wizard keeps the data by default. Deleting it removes the database, `archive/`, `builds/`,
  `instances/`, `import/` and `keys/`. The shared folder and the roots chosen outside of it are always kept.
- **HelixCoreServer package**: at each start, the package copies the p4dctl-ng profiles of the
  [Perforce-Synology](https://github.com/FrozenStormInteractive/Perforce-Synology) package to
  `import/HelixCoreServer/` in the shared folder, except the ones already there or dismissed. The install wizard
  only mentions the profiles it finds. Nothing is imported until the user chooses to: the
  Instances page of the web UI lists them (`GET /api/legacy-profiles`). Importing one keeps its root: the daemon saves
  a checkpoint to the folder of the instance, reads the Unicode mode, upgrades the database to the chosen p4d release
  and starts it. Stop the HelixCoreServer package before importing.

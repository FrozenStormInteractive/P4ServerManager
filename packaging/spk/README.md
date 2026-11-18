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

- **Data**: the package keeps its files in the folders DSM gives it (DSM Developer Guide, "File System Hierarchy"),
  out of reach of the users of the shared folders:
  - `/var/packages/P4ServerManager/var` (`@appdata`, kept on an upgrade and after an uninstallation) is the data
    directory of the daemon: `p4servermanager.db`, `keys/`, `archive/` (every p4d build, compressed with zstd),
    `builds/` (the builds the servers use, extracted), `instances/` (what the daemon keeps for each server: p4 tickets,
    backup index, health check journals, checkpoints taken before an import or a Unicode conversion), `import/` and
    the log of the daemon. `Daemon__ArchiveDirectory` moves the archive to another folder.
  - `/var/packages/P4ServerManager/tmp` (`@apptemp`, emptied on an upgrade and an uninstallation, while the package is
    stopped) is the runtime directory: the pid files of the daemon and of each p4d, the socket of the CLI, the start
    output of p4d and the temporary files of the backups.
  - The `P4ServerManager` shared folder holds the P4 servers created without a server directory, one folder per server
    named after the instance at its creation (`Main/root`, `Main/journals`, `Main/logs`, `Main/archives`). The
    creation is refused when that folder already holds files, and warns of a folder whose name differs by its case
    only, such as `main` for `Main`: both are kept apart, but they are easy to mix up from Windows.
  - Hyper Backup and Snapshot Replication work on shared folders: they do not see the `var` folder. Back it up another
    way, `keys/` included.
- **Deleting a server** runs in the background: the server shows `Deleting` until it is gone, and a NAS restart in the
  middle carries the deletion on. What the daemon keeps for the server in `var/instances/` and `tmp/instances/` always
  goes; the backup index there only spares re-reading unchanged files at the next backup, and the backups themselves
  stay at their destination. When its files are kept, the log of a server without a `logs` folder, such as an
  imported one, is moved to the `logs` folder of its root.
  - **With its files**, the deletion removes what the server uses, read from its configuration, wherever its folder
    is: in the shared folder, chosen at creation or imported. Its root and the folders of its depots go whole; its
    checkpoints, journals and logs go one by one; their folders and the server directory go once nothing else is left
    in them, so a file the daemon does not know keeps its folder. The deletion is refused, with the reason, when a
    folder is a root of the file system or right under it (`/volume1`), holds the files of the daemon or reaches the
    root of another instance: nothing is stopped nor deleted then.
- **Accounts**: the web UI and the REST API on the UI port ask to sign in. The install wizard can create the first
  account; otherwise the web UI asks for it on the first visit, and anybody who reaches the port can create it until
  then. That first account is an administrator. The wizard always shows that step: when a previous installation left
  accounts in the `var` folder, they are kept and the account of the wizard is ignored. The wizard cannot tell them
  apart, since Package Center does not give it the volume the package is installed to (`SYNOPKG_PKGDEST_VOL` is only
  set for the scripts, and for the upgrade wizard). Every account has all rights on the servers, but only administrators manage the accounts in
  Settings > Users: the other users only change their own password. Passwords are stored as salted PBKDF2 hashes. The sessions are cookies signed with the keys in `keys/`: they last 14 days without a request
  (`Authentication__SessionLifetime`) and survive a restart of the daemon. Sign-in attempts are limited to 10 per
  minute and per address (`Authentication__SignInAttemptsPerMinute`). The CLI needs no account: a lost password is
  reset with `sudo p4sm user set-password <name>`. The UI port serves plain HTTP, so the cookie and the passwords
  cross the network in clear: reach it through HTTPS, with the reverse proxy of DSM (Control Panel > Login Portal >
  Advanced > Reverse Proxy, with the WebSocket headers), from anything but a trusted network.
- **Super users**: the daemon stores the super user of each server, to administer it, with its password encrypted by
  the keys in `keys/`, readable by the daemon only. A backup of the `var` folder must include `keys/`: without them,
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
  `/var/packages/P4ServerManager/tmp/daemon.sock`, open to the `Perforce` user and group only: run it with `sudo`, or
  as a member of the `Perforce` group. Whoever can open the socket is trusted, without an account. `P4SM_SOCKET` or
  `--socket` points it to another socket.
- **Uninstallation**: the wizard keeps the data by default. Deleting it empties the `var` folder of the package. The
  P4 servers are always kept, in the shared folder and elsewhere.
- **HelixCoreServer package**: at each start, the package copies the p4dctl-ng profiles of the
  [Perforce-Synology](https://github.com/FrozenStormInteractive/Perforce-Synology) package to
  `import/HelixCoreServer/` in the `var` folder, except the ones already there or dismissed. The install wizard
  only mentions the profiles it finds. Nothing is imported until the user chooses to: the
  Instances page of the web UI lists them (`GET /api/legacy-profiles`). Importing one keeps its root: the daemon saves
  a checkpoint to the folder of the instance, reads the Unicode mode, upgrades the database to the chosen p4d release
  and starts it. Stop the HelixCoreServer package before importing.

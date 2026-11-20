# Debian package

Sources of the Debian package (`.deb`) of P4 Server Manager, for Debian, Ubuntu and their derivatives with systemd.
`build.py` builds one package per architecture, in Python only: no `dpkg-deb` needed, so it builds on Windows too.

## Requirements

- glibc 2.27 or later (.NET 10): Debian 10, Ubuntu 18.04 or later.
- `amd64` or `arm64`. Perforce publishes no 32-bit ARM p4d.
- systemd. Without it (a container, for instance), the package installs but nothing starts the daemon.

## Build

From the repository root, with the .NET SDK, npm and Python 3.9 or later:

```bash
python packaging/deb/build.py --version 0.1.0 --build-number 1
```

The packages land in `artifacts/deb/`, named `p4servermanager_<version>-<build>_<arch>.deb`. The build number is the
Debian revision. Useful options:

| Option | Default | Meaning |
| --- | --- | --- |
| `--arch amd64` / `--arch arm64` | both | Architecture to build, can be repeated. |
| `--ui-port` | `8666` | Default port of the web UI and the REST API. |
| `--maintainer` | `Frozen Storm Interactive` | `Maintainer` field, `Name <email>`. |
| `--skip-publish` | | Reuse the output of the last `dotnet publish`. |

## Publication

The CI builds both packages and `release.yml` attaches them to the draft GitHub release. Once that draft is
published, `.github/workflows/cloudsmith.yml` pushes them to Cloudsmith, as `any-distro/any-version`: one upload
serves every Debian and Ubuntu release. It needs the repository variable `CLOUDSMITH_REPOSITORY`
(`owner/repository` on Cloudsmith) and the secret `CLOUDSMITH_API_KEY`. Run it by hand, with the tag, to push a
release again.

## Layout

| Path | Goes to | Content |
| --- | --- | --- |
| `files/` | `/` | systemd unit, `/etc/default/p4servermanager` (a conffile), `stop-servers`. `@UI_PORT@` is replaced. |
| `scripts/` | control archive | `postinst`, `prerm`, `postrm`. |
| publish output | `/usr/lib/p4servermanager/app` | The daemon, its web UI and the CLI, sharing the .NET runtime. |
| | `/usr/bin/p4sm` | Link to the CLI. |

## On the machine

- **Service**: `p4servermanager.service`, enabled and started on installation, runs the daemon as the
  `p4servermanager` system user. Its logs go to the journal: `journalctl -u p4servermanager`.
- **Settings**: `/etc/default/p4servermanager` overrides the environment of the unit, for instance the port
  (`ASPNETCORE_URLS`) or the folder of the servers (`Daemon__ServersDirectory`). Restart the service after a change.
- **Data**: `/var/lib/p4servermanager` is the data directory: database, `keys/`, `archive/`, `builds/`,
  `instances/` and, by default, `servers/`, one folder per P4 server. Back it up whole, `keys/` included: without
  them, the stored super user passwords cannot be read.
- **Runtime**: `/run/p4servermanager` holds the socket of the CLI and the pid files of the p4d servers. It is kept
  when the service stops (`RuntimeDirectoryPreserve=yes`), so that the next daemon adopts the servers.
- **CLI**: the socket is open to the `p4servermanager` user and group. Run `p4sm` with `sudo`, or add yourself to the
  group: `sudo usermod -aG p4servermanager $USER`, then sign in again.
- **Servers outlive the daemon**: the unit uses `KillMode=process`, so a stop, a restart or a crash of the daemon
  never stops a p4d. systemd logs a "left-over process" line for each p4d at the next start: it is expected.
  `stop-servers --on-shutdown` (`ExecStopPost`) stops them with SIGTERM when the system shuts down, before the file
  systems are unmounted.
- **Upgrade**: `prerm` stops the daemon only, the p4d servers keep running, and `postinst` starts the new daemon,
  which adopts them.
- **Removal**: `apt remove` stops and disables the service, then stops the p4d servers. `apt purge` also deletes the
  settings, but never `/var/lib/p4servermanager` nor the user: they may hold P4 servers.
- The UI port serves plain HTTP. Outside a trusted network, put it behind an HTTPS reverse proxy, with the WebSocket
  headers.

# AGENTS.md

This file provides guidance to AI Agents when working with code in this repository.

P4 Server Manager: A self-hosted service to create, run and maintain Perforce P4 servers, from a web UI or the command line. The same daemon can also run as a systemd service.

It replaces the older package at FrozenStormInteractive/Perforce-Synology (SSH-only `p4dctl-ng`, one p4d version baked into the package). Users should be able to manage servers without SSH and pick a p4d version per instance.

This project is unofficial and not affiliated with Perforce Software. "Perforce" and "P4" are trademarks of Perforce Software.

## Guiding principles

1. **API first.** The web UI and the CLI are both clients of the same REST API.
2. **p4d is decoupled from the package.** The packages ships no p4d binary. Each instance is pinned to an exact build downloaded on demand.
3. **Servers outlive the daemon.** A daemon crash or restart must never stop a running p4d. On startup the daemon adopts running instances instead of restarting them.
4. **One process, one port.** The daemon serves the API, SignalR and the static UI files from the same origin. The CLI
   reaches the same API through a Unix domain socket (`Daemon:SocketPath`), open to the user and group of the daemon only.
5. **Safe operations over features.** Safe upgrades and consistent backups matter more than orchestration features.

## Repository layout

```
src/
  P4ServerManager.Cli/          Command-line app (p4sm), kept compatible with Native AOT
  P4ServerManager.Contracts/    Request and response types of the REST API, shared by the daemon and the CLI
  P4ServerManager.Daemon/       ASP.NET Core host: REST API, SignalR hub, serves the UI
  P4ServerManager.Daemon.UI/    SPA UI (Vue 3 + Vuetify 4 + Vite) wrapped as an .esproj
packaging/
  spk/                          INFO, scripts, conf, wizard files, icons
tests/                          Tests
  P4ServerManager.Cli.Tests/
  P4ServerManager.Daemon.Tests/
docs/
```

## Commands

Backend & CLI (repository root):

```
dotnet build P4ServerManager.slnx
dotnet test                                                   # all test projects
dotnet test --project tests/P4ServerManager.Daemon.Tests      # one project
dotnet test --project tests/P4ServerManager.Daemon.Tests --filter-method "*Test1"     # one test
dotnet test --project tests/P4ServerManager.Daemon.Tests --filter-class "*UnitTest1"  # one class
dotnet run --project src/P4ServerManager.Daemon --launch-profile https
dotnet run --project src/P4ServerManager.Cli -- instance list # CLI against that daemon, through its socket in data/
```

Frontend / UI (from `src/P4ServerManager.Daemon.UI`):

```
npm install
npm run dev              # Vite dev server
npm run build            # vue-tsc type-check + vite build, in parallel
npm run type-check
npm run lint             # oxlint then eslint, report only
npm run lint:fix
npm run test:unit        # vitest
npm run test:unit:watch        # vitest (watch mode)
npx vitest run path/to/file.spec.ts   # one test file
npx vitest run -t "test name"          # tests matching a name
```

## Tech stack

- Backend and CLI: C#, .NET, ASP.NET Core, EF Core + SQLite, SignalR, FluentValidation, Cronos for cron expressions.
- CLI: C#, .NET, System.CommandLine.
- UI: Vue 3 (`<script setup>`, TypeScript), Vuetify 4, Vue Router, Pinia, vue-i18n, `@microsoft/signalr`.
- Publication: self-contained, `linux-x64` and `linux-arm64`. Perforce provides no 32-bit ARM p4d, so no 32-bit ARM target.

## Database

- Before you change the database schema or a migration, read `src/P4ServerManager.Daemon/Database/AGENTS.md`. Migration rules live there.

## Frontend

Frontend-specific rules live in `src/P4ServerManager.Daemon.UI/AGENTS.md`. Before you edit, spec, or review a change to P4ServerManager.Daemon.UI/, i18n, UI components, or frontend tests, read that file.

## Conventions

- Nullable reference types enabled.
- Async all the way, always pass `CancellationToken`. No blocking calls on the request path.
- No new API endpoint without its OpenAPI description.
- Request validation: one FluentValidation `AbstractValidator<T>` per request type. Validators are registered from the assembly and run by `RequestValidationFilter`, so controllers never call them. Keep `[Required]` on the contracts: it shapes the OpenAPI schema, and MVC checks it before the validators run.
- Request and response types of the API live in `P4ServerManager.Contracts`, which depends on the BCL only. Their mapping from the daemon's own types stays in the daemon (`Contracts/ContractMappings.cs`).
- CLI: JSON through the source-generated `CliJsonContext`, no reflection, so that it stays compatible with Native AOT.

## Do not

- Do not embed a p4d binary in the package or the repository.
- Do not start p4d as a child that dies with the daemon.
- Do not store secrets in the database in clear text or in json.
- Do not imply Perforce or P4 official affiliation.

## Language rule

Everything written in the repository is in **English**: identifiers, comments, XML doc comments, log messages,
exception messages, UI strings, commit messages, and documentation, even when the conversation is in another language.

# AGENTS.md

Database Schema and migration rules for work under `src/P4ServerManager.Daemon/Database/`.

- Put a change to a migration that already exists on `develop` in a new migration.
- Prefer managing migrations with `dotnet ef migrations` (command in the root `CLAUDE.md`) rather than by hand; edit a generated migration only to fix its formatting, never its content.
- Migrations live in `Migrations` folder.

## Commands

Backend & CLI (repository root):

```
# every model change gets an EF Core migration; they live in Server.Data, the Server is the startup project
dotnet ef migrations add <Name> --project src/P4ServerManager.Daemon
```

# Repository Guidelines

## Project Structure & Module Organization

- `src/GoingCooperative.Core/` contains transport contracts, codecs, identity, hashing, and other game-independent primitives.
- `src/GoingCooperative.Plugin.BepInEx/` is the BepInEx plugin: Unity/Harmony integration and host-authoritative replication runtime. Keep game-reflection code here, not in Core.
- `config/` holds tracked host and client configuration templates.
- `scripts/Build.ps1` compiles the single plugin DLL; `scripts/Package-Release.ps1` stages release assets. Generated output is under `artifacts/` and is ignored.

## Build, Test, and Development Commands

Close Going Medieval before rebuilding or replacing its plugin DLL. From the repository root:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Build.ps1 -Configuration Debug -GameRoot ".."
```

This writes `artifacts/bin/Debug/GoingCooperative.dll`. Use `Release` for a production build. Create a distributable archive with:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Package-Release.ps1 -Version 0.1.0 -GameRoot ".."
```

For in-game testing, copy the built DLL to `BepInEx/plugins/GoingCooperative/GoingCooperative.dll` and create `GoingCooperative/replication.cfg` from the appropriate template.

## Coding Style & Naming Conventions

Use C# with four-space indentation, braces on their own lines, and nullable reference annotations. Use PascalCase for types, methods, properties, and enum members; camelCase for private fields and parameters. Keep one focused responsibility per file; replication feature files follow the `Replication*.cs` pattern. Preserve the existing explicit namespaces and avoid adding dependencies on game assemblies to Core.

## Testing Guidelines

There is currently no automated test project. Build after every change, then test both host and client using the same game version and compatible saves. Check `BepInEx/LogOutput.log` and `BepInEx/GoingCooperative/plugin.log`; include relevant log excerpts and reproduction steps in bug reports.

## Local Development Documentation

Some checkouts contain a private, locally excluded Obsidian vault at `local-docs/`. If available, read `00_INDEX.md` first, then consult relevant active notes at the start of work, before significant decisions, and during validation. Treat documented constraints as local project context unless they conflict with source or newer instructions; record durable local findings there. Do not assume it exists in every clone or use it as the only shared-requirements location. Never stage, commit, or move its contents into tracked files without explicit approval.

## Commit & Pull Request Guidelines

The history currently contains only `Initial Going Cooperative release`, so no detailed convention is established. Use short, imperative, scoped subjects, for example `Fix client resync acknowledgement`. Keep commits focused. Pull requests should explain the multiplayer behavior changed, list host/client test coverage, link relevant issues, and include logs or screenshots for UI-visible changes. Never commit `artifacts/`, active configs, logs, saves, or game/BepInEx binaries.

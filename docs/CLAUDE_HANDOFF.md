# Nexus Shell — handoff to Claude Code

Status captured: **2026-08-02 (Europe/Moscow)**

## Product intent

Nexus Shell is a Russian-language native file manager and organizer for
Windows 11. The product goal is to replace File Explorer in daily use as an
application while preserving `explorer.exe`, the taskbar, Start, standard file
dialogs and an emergency Explorer action.

The visual direction is a calm, readable continuation of Windows 11: dark
matte surfaces, generous whitespace and one restrained blue-violet accent.
The user works on a 34-inch ultrawide monitor, but the minimum supported window
width is 960 px.

Read before changing code:

- `PRODUCT.md`
- `DESIGN.md`
- `docs/SECURITY_RULES.md`
- `design/nexus-reference.png`
- `design/nexus-reference-compact.png`

The reference images describe different views. Do not remove content from one
view merely to imitate the other.

## Repository map

```text
NexusShell.sln
├── src/Nexus.App                  WinUI 3 UI and Windows interactions
├── src/Nexus.Core                 File/domain/catalog services and models
├── tests/Nexus.Core.Tests         Console regression harness
├── tests/Nexus.RecycleBin.SmokeTests
├── installer                      Inno Setup release pipeline
├── design                         Authoritative visual references
└── docs                           Product, safety and release documentation
```

High-change files:

- `src/Nexus.App/MainWindow.xaml`
- `src/Nexus.App/MainWindow.xaml.cs`
- `src/Nexus.App/ViewModels/MainWindowViewModel.cs`
- `src/Nexus.Core/Services/FileOperationService.cs`
- `src/Nexus.Core/Services/GameLibraryService.cs`
- `src/Nexus.Core/Services/StartApplicationCatalog.cs`
- `src/Nexus.Core/Services/AiWorkspaceService.cs`
- `src/Nexus.Core/Services/RecycleBinService.cs`

`MainWindow` remains oversized. Prefer narrow fixes and extract one cohesive
feature boundary at a time; do not start with a full rewrite.

## Verified baseline

Environment used for the last verification:

- Windows 11 x64
- .NET SDK 8.0.423
- .NET 8 / `net8.0-windows10.0.22621.0`
- Windows App SDK 1.6.240923002
- warnings treated as errors

Commands:

```powershell
dotnet restore NexusShell.sln --configfile NuGet.Config -p:NuGetAudit=false
dotnet build NexusShell.sln -c Release --no-restore -p:NuGetAudit=false
dotnet run --project tests/Nexus.Core.Tests/Nexus.Core.Tests.csproj `
  -c Release --no-build --no-restore -p:NuGetAudit=false
dotnet run --project tests/Nexus.RecycleBin.SmokeTests/Nexus.RecycleBin.SmokeTests.csproj `
  -c Release --no-build --no-restore -p:NuGetAudit=false
```

Last verified results:

| Gate | Result |
|---|---|
| Release solution build | PASS — 0 warnings, 0 errors |
| Core console regression harness | PASS |
| Recycle Bin read-only smoke/ABI probe | PASS — no mutation |
| Installer static safety validation | PASS |
| Installer 0.8.0 self-contained validation | PASS |
| Installer Authenticode | UNSIGNED |
| Installer extraction smoke | SKIPPED — `innounp` unavailable |

`Nexus.Core.Tests` is a console executable. `dotnet test` can report success
while running zero checks and is not a valid gate here.

The former cross-volume `CopiedSourceSnapshot` P0 is fixed and covered by
regressions. Production fail-closed behavior was not weakened. Do not recreate
that work as the first task.

## Implemented product areas

- real folders, known folders and drives;
- list/grid modes, tabs, address bar, sorting, search and recent items;
- favorites, create, rename, copy, move, paste and Recycle Bin deletion;
- progress, speed, pause/cancel and explicit conflict handling;
- image thumbnails and Windows Shell icons;
- Steam, Epic, Xbox and bounded local/portable game discovery;
- torrent metadata parsing independent of a specific torrent client;
- Start Menu, Store and system application cataloguing with stable identities;
- Ollama/model, Codex, Claude, Cursor, Skills, MCP and AI project discovery;
- persisted custom game and AI scan roots exposed in Settings;
- safe organization suggestions with confirmation and undo;
- self-contained per-user Inno Setup installer with guarded versioned payloads.

## Safety invariants

- Never modify `explorer.exe`, `Winlogon\\Shell`, the system shell, file
  associations or the registry without a separate explicit request and recovery
  design.
- Never test against real Desktop, Downloads, Documents, installed games or
  Recycle Bin contents when an isolated `%TEMP%` fixture is possible.
- Never recursively move/delete an unresolved broad path, workspace root,
  system directory or reparse point.
- A cross-volume move may delete its source only after verified copy/commit and
  identity-checked cleanup of exactly the copied entries.
- Manual deletion goes to the Windows Recycle Bin. Permanent deletion requires
  a separate explicit confirmation.
- Installer cleanup may remove only a versioned Nexus payload carrying the exact
  ownership marker expected by the installer.
- Do not weaken, skip or rewrite regressions to hide a failure.

## Known limitations and next work

Nexus Shell is not yet complete Explorer parity. Network credential workflows,
all archive formats, Windows property/permission pages, sharing, shell
extensions and several advanced system operations remain incomplete.

Priorities for bounded follow-up tasks:

1. Run a real GUI accessibility/adaptivity pass at 960 px, common desktop sizes
   and the user's 34-inch ultrawide resolution. Verify icon-only automation
   names, keyboard focus, scaling and empty/loading/error states.
2. Validate game/application identity and artwork against deterministic fixtures
   and a non-destructive user-specific sample. Never launch or remove real games
   during automated tests.
3. Validate AI discovery diagnostics and custom roots against known missing
   projects; keep scan limits and skipped paths visible and truthful.
4. Keep file-operation UI copy aligned with actual partial/rollback results and
   extend durable history/undo without reducing source-preservation guarantees.
5. Extract cohesive Browser, File Operations, Preview, Games, Applications,
   AI Center and Recycle Bin components from `MainWindow` gradually.
6. Add CI on a Windows runner once the repository is published.

The current installed application on the original development machine may be an
older 0.6.0 build until the user runs the new installer. Do not confuse the
installed version with the source or self-contained 0.8.0 publish output.

## Release and repository hygiene

Generated content is intentionally excluded from Git:

- `outputs/**`
- `work/**`
- all `bin/**` and `obj/**`
- executables, DLLs, PDBs, archives, logs, dumps and test results
- local IDE/agent settings and credentials

Therefore a GitHub clone contains source only. If a binary release is needed,
build it after all gates are green and attach the installer, checksum and
manifest to a GitHub Release; do not commit them to the source tree.

Build the installer:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File installer/build-installer.ps1 `
  -Version 0.8.0
```

Requirements: Windows 11 x64, .NET 8 SDK and Inno Setup 6. `innounp` is
optional. Code signing is optional for local development but a public release
should be signed before broad distribution.

## Working method for Claude Code

1. Read the product/design/safety documents.
2. Reproduce the green build and console-test baseline.
3. Select one bounded, verified problem.
4. Add or preserve deterministic tests in isolated temporary directories.
5. Make the smallest coherent production change.
6. Run focused checks, the full Core harness and Release build.
7. For XAML, COM, Shell, picker, icon or window changes, perform a real GUI
   smoke test and state clearly when that verification is unavailable.
8. Report exactly what changed, what passed and what remains unverified.

Do not claim that every Explorer function, every game source or every AI project
is supported until the corresponding paths are implemented and tested.

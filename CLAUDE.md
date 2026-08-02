# Nexus Shell — project instructions for Claude Code

## Start here

- Read `docs/CLAUDE_HANDOFF.md` before changing code. It records the verified state, known defects, and the next safe milestones.
- Read `docs/SECURITY_RULES.md`, `PRODUCT.md`, and `DESIGN.md` before changing file operations, installer behavior, navigation, or UI.
- Treat `docs/IMPLEMENTATION_PLAN.md` and old release notes as historical intent, not proof that a feature is complete.
- Never use an executable, DLL, ZIP, manifest, or screenshot in `outputs/` or `work/` as evidence of the current source state. Those artifacts are stale unless rebuilt in the current session.

## Product intent

Nexus Shell is a Russian-language, Windows 11-style file manager and organizer intended to replace File Explorer in daily use without replacing the Windows system shell. It must feel calm, spacious, readable, and native. The standard Explorer remains an emergency fallback.

The application should eventually cover normal Explorer workflows, games and applications, torrent metadata, AI workspaces, favorites, recent items, Recycle Bin management, and safe organization suggestions. Do not claim parity until the corresponding paths are implemented and tested.

## Technology and layout

- Windows 11 x64, C#, .NET 8, WinUI 3, Windows App SDK 1.6.
- `src/Nexus.App`: WinUI window, XAML resources, view model, thumbnails, interaction logic.
- `src/Nexus.Core`: file system, file operations, games, applications, torrents, AI discovery, organization, favorites, and Recycle Bin services.
- `tests/Nexus.Core.Tests`: a custom console regression harness, not xUnit/NUnit. A zero exit code is the test gate.
- `installer`: Inno Setup per-user self-contained installer and guarded release script.
- `design`: the two authoritative UI references. They represent different views and both must remain.

`MainWindow.xaml` and `MainWindow.xaml.cs` are currently very large. Make narrow fixes first. For substantial new work, extract cohesive components or services instead of adding another unrelated block to code-behind.

## Verified commands

Run from the repository root in PowerShell on Windows 11:

```powershell
dotnet restore NexusShell.sln --configfile NuGet.Config -p:NuGetAudit=false
dotnet build NexusShell.sln -c Release --no-restore -p:NuGetAudit=false
dotnet run --project tests/Nexus.Core.Tests/Nexus.Core.Tests.csproj -c Release --no-build --no-restore -p:NuGetAudit=false
```

`Nexus.Core.Tests` is a console executable. Do not use `dotnet test`: it exits
successfully without running this harness. When NuGet is reachable, separately
run restore with `-p:NuGetAudit=true -p:NuGetAuditMode=all`; the handoff machine
currently receives `NU1900` from the unavailable NuGet audit endpoint.

Run the application after a successful build:

```powershell
dotnet run --project src/Nexus.App/Nexus.App.csproj -c Release --no-build
```

Static installer safety check:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File installer/build-installer.ps1 -ValidateOnly
```

Do not build or distribute an installer while the Core test command is failing.
Increment the version for every materially different release and never overwrite
an existing published artifact with different bytes.

## Current verified state

As verified on 2026-08-02:

- Release solution build: passes with 0 warnings and 0 errors.
- Core console checks: pass, including adversarial cross-volume, cancellation,
  reparse-point and catalog regressions.
- The former `CopiedSourceSnapshot` P0 is fixed. Do not recreate it as the first
  task or weaken its regression coverage.
- Read-only Recycle Bin smoke/ABI checks pass without modifying user data.
- Installer 0.8.0 was built and independently hash-checked as self-contained.
  It is unsigned, and generated artifacts under `outputs/` are intentionally not
  part of the Git repository.
- Automated source/build checks do not replace a real GUI pass after WinUI,
  Shell, picker, thumbnail or window-lifecycle changes.

Before editing, reproduce the current green baseline. Then select one bounded
issue from `docs/CLAUDE_HANDOFF.md`, add or preserve a regression where possible,
and report exactly what was and was not verified.

## Non-negotiable safety rules

- Do not modify `explorer.exe`, `Winlogon\\Shell`, the Windows shell, system file associations, or the registry unless the user makes a separate explicit request after a recovery design is reviewed.
- Do not test against real Desktop, Downloads, Documents, game folders, installed applications, or Recycle Bin contents.
- Never recursively delete or move a broad, unresolved, user-controlled, system, reparse-point, or workspace-root path.
- Never follow junctions, symlinks, mount points, or other reparse points in recursive copy, move, rollback, or cleanup.
- A cross-volume move is complete only after copy, verification, atomic destination commit where possible, and identity-checked deletion of exactly the entries that were copied.
- If source identity or metadata safety cannot be proven, preserve the source and return an explicit partial/failure result with recovery paths.
- Manual deletion goes to the Windows Recycle Bin. Permanent deletion always needs a separate explicit confirmation.
- Installer cleanup may remove only a versioned Nexus payload carrying the exact ownership marker expected by the current installer.
- Never hide a failed test, downgrade it, or update expected results to match unsafe behavior.

## Working method

1. Inspect the relevant implementation and existing regression tests.
2. Reproduce the defect with a deterministic test in an isolated `%TEMP%` directory.
3. Make the smallest coherent production change.
4. Run the focused console checks, then the full Release build.
5. For XAML, COM, Shell, picker, icon, or window changes, also perform a real GUI smoke test; compilation alone is insufficient.
6. Report exactly what was verified, what remains unverified, and any user data or system state touched.

Preserve unrelated user changes. Do not initialize, reset, clean, commit, push, install, uninstall, or modify system state unless the user explicitly asks for that action.

## Product-specific priorities

1. Perform a real GUI pass for minimum-width and 34-inch ultrawide layouts,
   keyboard focus, scaling, empty/error/loading states and icon-only automation names.
2. Validate user-specific game/application icon matching and AI discovery with
   non-destructive fixtures before changing heuristics.
3. Keep file-operation progress, pause/cancel, conflict and rollback copy aligned
   with the Core result model; never bypass fail-closed behavior.
4. Split `MainWindow` by cohesive feature boundaries without a big-bang rewrite.
5. Add durable operation history/undo and richer Explorer parity only behind
   explicit, recoverable product flows.
6. Rebuild and re-hash a self-contained installer after every release change.

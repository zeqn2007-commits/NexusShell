---
paths:
  - "src/Nexus.Core/Services/FileOperationService.cs"
  - "src/Nexus.Core/Services/OrganizationService.cs"
  - "src/Nexus.Core/Services/RecycleBinService.cs"
  - "tests/Nexus.Core.Tests/**/*.cs"
---

# File-system safety

- Treat file operations as data-loss-sensitive code. Prefer a failed or partial operation with explicit recovery paths over deleting an unverified source.
- Never test against real Desktop, Downloads, Documents, game libraries, Recycle Bin contents, or installed applications. Use a unique directory below `%TEMP%` and verify its resolved path before cleanup.
- Never follow directory junctions, symbolic links, mount points, or other reparse points during recursive copy, move, rollback, or deletion.
- Cross-volume move is copy, verify, commit, then identity-checked source deletion. Only delete entries that were actually copied successfully.
- Cancellation and exceptions must leave the source intact or return concrete recovery locations. Do not describe a rollback as successful unless it was verified.
- Existing destination data must not be overwritten unless the user explicitly selected a conflict policy. Folder replacement remains unsupported until it has a complete merge/rollback design.
- Manual deletion goes through the Windows Recycle Bin. Permanent deletion requires a separate explicit confirmation and must never be used by tests.
- Preserve Windows metadata where promised. If directory metadata cannot be transferred safely, do not finish a destructive cross-volume move.
- Add a regression test for every race, rollback, reparse-point, conflict, and cancellation fix.

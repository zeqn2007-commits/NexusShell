# Nexus Shell agent guide

Before changing code, read `CLAUDE.md`, `docs/CLAUDE_HANDOFF.md`,
`docs/SECURITY_RULES.md`, `PRODUCT.md` and `DESIGN.md`. These documents define
the current verified baseline, product intent and non-negotiable file safety
rules for every coding agent, not only Claude Code.

Run the exact Release build and console harness commands from `CLAUDE.md` before
and after a change. `dotnet test` is not a valid replacement for the custom
`Nexus.Core.Tests` executable. Use isolated `%TEMP%` fixtures and never test a
destructive operation on real user folders, games or Recycle Bin contents.

Keep changes bounded. `MainWindow` is large, so prefer extracting one cohesive
feature boundary over adding unrelated code or attempting a full rewrite.
Report GUI-sensitive work as unverified unless it was exercised in a real
Windows session.

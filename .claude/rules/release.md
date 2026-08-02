---
paths:
  - "installer/**"
  - "src/Nexus.App/Nexus.App.csproj"
  - "docs/RELEASE_*.md"
---

# Release and installer rules

- The installer must remain per-user and must not require administrator rights.
- Publish `win-x64` self-contained with `WindowsAppSDKSelfContained=true`; a clean machine must not receive a Windows App Runtime prerequisite prompt.
- Never change `explorer.exe`, `Winlogon\\Shell`, the system shell, registry file associations, or context-menu registrations without a separate explicit request and recovery design.
- Installer cleanup may remove only a versioned Nexus payload with a valid ownership marker and matching product/version metadata. Never recursively clean a guessed install directory.
- Existing files in `outputs/` are historical and untrusted until rebuilt by the current source and validated by the current script.
- Run console checks, Release build, publish validation, and installer static safety validation before creating a release artifact.
- Do not claim a signed release unless Authenticode validation succeeds. Unsigned builds must be labelled clearly.

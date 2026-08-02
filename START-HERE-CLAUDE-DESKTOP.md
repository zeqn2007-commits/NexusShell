# Start Nexus Shell in Claude Code Desktop

1. Extract the handoff ZIP to a normal local folder. Do not select or attach the ZIP itself.
2. Open the **Claude Desktop** application and choose the **Code** tab.
3. Create a new session.
4. Set **Environment** to **Local**.
5. Click **Select folder** and choose the extracted folder that contains `NexusShell.sln` and `CLAUDE.md`.
6. For the first session, choose **Plan** permission mode so Claude audits the project before editing.
7. Open `docs/CLAUDE_START_PROMPT.md`, copy the prompt inside its code block, and send it.
8. After Claude reproduces the current green build/test baseline and proposes a
   bounded plan, switch to the normal edit/approval mode when you are ready to
   let it implement the selected change.

Claude Code Desktop automatically uses the project `CLAUDE.md` and the rules under `.claude/rules/`. Do not upload old files from `outputs/` or `work/`; they were deliberately excluded from this handoff.

## Windows requirements

- Use the current Claude Desktop version with the **Code** tab.
- Select **Local**, not Remote: this is a Windows-only WinUI 3 application and must be built/tested on Windows.
- Git for Windows must be installed for local Code sessions. The handoff itself is source-only and does not contain Git history.
- .NET 8 SDK is required. Inno Setup 6 is needed only after the code and GUI quality gates pass.

If the app shows only **Chat** or **Cowork** and no **Code** tab, update Claude Desktop and confirm that the current account/plan includes Claude Code. A ZIP attached to ordinary Chat can be analyzed, but Chat cannot directly build, run, and edit the local Windows project.

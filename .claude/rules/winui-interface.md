---
paths:
  - "src/Nexus.App/**/*.xaml"
  - "src/Nexus.App/**/*.cs"
---

# WinUI interface rules

- Follow `DESIGN.md` and the reference images in `design/`. The visual direction is calm Windows 11, dark, readable, spacious, and not cyberpunk.
- `design/nexus-reference.png` is the main Projects/AI composition. `design/nexus-reference-compact.png` is the compact Files tab. They are different views; do not remove one to imitate the other.
- The preview panel is contextual and hidden until a real file, game, application, torrent, AI project, or Recycle Bin entry is selected.
- Keep file-manager behavior discoverable: familiar command labels, keyboard shortcuts, visible selection, conflict dialogs, determinate progress, pause/resume, cancellation, and recovery feedback.
- Support a 34-inch ultrawide monitor without stretching content into sparse, unreadable rows. Use bounded content widths where appropriate and adaptive columns; also preserve usability at the documented 960 px minimum.
- Use shared resources/tokens from `App.xaml`; do not scatter one-off colors, font sizes, corner radii, or margins through code-behind.
- Keep primary text at WCAG AA contrast, interactive targets at least 40x40 px, and add AutomationProperties names to icon-only actions.
- Avoid unrelated rewrites of the very large `MainWindow.xaml` and `MainWindow.xaml.cs`. Extract cohesive services/view models/components when changing a feature substantially.
- Build the full solution after XAML changes and perform a real GUI smoke test. A successful compile does not prove XAML/COM runtime correctness.

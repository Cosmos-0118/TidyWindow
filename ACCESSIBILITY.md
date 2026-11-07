# Accessibility

TidyWindow should be usable by everyone who maintains a Windows PC, including people who use a keyboard instead of a mouse, a screen reader, high contrast themes or reduced motion. This page describes what the app supports today, where we know it falls short, and how to tell us about a barrier.

## Our commitment

- New pages and controls are built with Windows' standard WinUI 3 controls, which carry keyboard and screen reader support by default.
- Accessibility problems are treated as bugs, not feature requests.
- We would rather say plainly what has not been tested than claim support we cannot show.

## Supported environments

- **Windows 10 build 19041 (version 2004) or later and Windows 11, x64.**
- **Screen readers.** The app is built on Windows UI Automation, so screen readers that use it, such as Narrator and NVDA, can read it. See the known limitations below for what has been tested.
- **Windows accessibility settings** described below. The app follows them; it has no separate accessibility settings of its own.

## What the app supports today

| Area | Support |
| --- | --- |
| **Screen readers** | Interactive controls that have no descriptive visible text (icon buttons, toggles, values shown next to a label) carry an accessible name through `AutomationProperties.Name`, so a screen reader announces what they do. |
| **Announcements** | Notifications and the progress line shown during long operations are live regions, so a screen reader announces them without moving focus. |
| **Keyboard** | The sidebar entries, row actions, toggles and dialogs are standard WinUI buttons and controls, reachable with Tab and operated with Enter, Space and Esc. Rows that expand when clicked also have a details button. **Ctrl+K** opens Install Hub search from anywhere in the app. |
| **High contrast** | The app ships a high contrast theme that replaces its own colors with your Windows high contrast colors, and it updates when you switch contrast themes. |
| **Reduced motion** | When **Settings → Accessibility → Visual effects → Animation effects** is off, the app skips its page, sidebar, toast, loading and illustration animations. |
| **Light and dark themes** | Both themes are supported. The app follows your Windows app mode unless you pick one in Settings. |
| **Text size** | Text follows the Windows **Text size** setting through WinUI's default text scaling. |
| **Status and results** | Outcomes are written in words (for example "Restored 12 files" or "installation could not be verified") rather than shown by color alone. |

## Known limitations

- **No formal audit yet.** TidyWindow has not been tested against WCAG 2.2, and no full screen reader pass of every page has been done.
- **Few keyboard shortcuts.** Apart from Ctrl+K there are no shortcuts for jumping to a page or a common action; you move with Tab.
- **Large text sizes.** Large Windows text sizes have not been checked on every page, so some rows may clip or wrap awkwardly.
- **Native windows outside the app.** Installer dialogs, Windows administrator prompts and Chocolatey's elevated console are Windows or third-party windows. Their accessibility is outside TidyWindow's control.

## Report a barrier

If something in TidyWindow is hard or impossible to use with your setup, please tell us:

- [Open a bug report](https://github.com/Cosmos-0118/TidyWindow/issues/new?template=bug_report.yml) and choose **Accessibility** as the kind of problem. Include the page, what you were trying to do, and the assistive technology you use (for example Narrator, NVDA, high contrast, keyboard only, text size) with its version.
- If you prefer not to post publicly, email [dhanush13244@gmail.com](mailto:dhanush13244@gmail.com) with "TidyWindow accessibility" in the subject.

We aim to reply within 7 days. Barriers that block a task completely are prioritized with other serious bugs.

## For contributors

When you change the UI, check it with the keyboard alone, with Narrator, in high contrast and in both light and dark themes, and give new controls an accessible name when their visible text does not describe them. [CONTRIBUTING.md](CONTRIBUTING.md) has the rest of the checklist.

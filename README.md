# TidyWindow

A native Windows app for installing software and keeping Windows tidy. Built with C#, .NET 10 and WinUI 3.

Everything that changes your PC is reviewed first, runs from a short list of known actions, and can be put back. Where an option has no good evidence behind it, it is left out on purpose and the research notes say why.

> The previous TidyWindow (v4.x) is kept on the [`legacy`](https://github.com/Cosmos-0118/TidyWindow/tree/legacy) branch. This branch is a ground-up rebuild and shares no code with it.

## What it does

| Page | What it is for |
| --- | --- |
| **Overview** | Detects WinGet, Chocolatey and Scoop, shows their versions and offers setup help when one is missing. |
| **Install Hub** | Searches the real package sources together, ranks the matches, recognizes what is already installed and shows a review before anything starts. |
| **Installed** | Lists installed packages, with update, uninstall and hold, and checks available versions. |
| **Scout** | Finds files and folders by name or path with ranked results, type and location filters, copy path and open location. |
| **Cleanup** | Clears caches and temporary files. Each clean can be undone afterwards. |
| **Path Doctor** | Shows which program runs when you type a command, and fixes the folder list that decides it. |
| **Repair** | Fixes installs and uninstalls that fail because an earlier one was cancelled or crashed. Every repair keeps a full copy of what it removed. |
| **Startup** | Chooses what opens with Windows. Nothing is deleted; turning an item back on restores it exactly as it was. |
| **Quiet** | Turns known Windows background items (telemetry, screen-watching features such as Recall, optional services) off and back on. See [docs/quiet-research.md](docs/quiet-research.md). |
| **Tune** | A short list of registry settings that make a noticeable difference. Each shows its current value, saves the original before a change and can be put back exactly. See [docs/tune-research.md](docs/tune-research.md). |
| **Fixes** | Repairs for common problems (system files, Windows Update, the drive, network, printing, sound, Store, Start menu, icons, OneDrive). Each is a plain list of standard Windows commands shown before it runs, with its output live. See [docs/fixes-research.md](docs/fixes-research.md). |
| **Lab** | Measures what a change really does: a 15-second check saved with the settings in force and compared with the previous one, a power-plan switch, and a temporary priority change for one running app. See [docs/lab-research.md](docs/lab-research.md). |
| **Queue** | Installs, updates and uninstalls from every page, one at a time, with live output. |
| **Logs** | Outcomes and problems worth acting on. |

## How it stays safe

- **Reviewed catalogs, not free-form commands.** Quiet, Tune, Fixes, Cleanup and the Lab can only act on items in a catalog that is checked when the app starts, and a second allowlist refuses anything outside it, whatever a caller sends.
- **Shown before it runs.** You see the exact change, whether it needs administrator permission and whether a restart is needed.
- **Reversible.** Original values are saved before a change, and the result is read back to check it took effect. Cleanups, repairs and startup changes keep what they removed so it can be restored, even after closing the app.
- **Administrator permission only when needed.** The app runs without it. Changes to Windows-wide settings go through a small helper (`TidyWindow.CleanHost.exe`) that starts only after you approve the Windows prompt.
- **Honest results.** When a change cannot be verified, the app says so instead of claiming success.

## Install queue

Installs, updates and uninstalls share one queue across pages. It runs one operation at a time, rejects duplicate or conflicting requests for the same manager, package and scope, and continues after a failure. Output streams as it arrives, and each card keeps the newest 64 KiB.

Windows administrator prompts and installer dialogs stay native windows that you respond to. The queue waits for the command to exit, then checks the inventory; if the expected change cannot be verified, the card says that rather than reporting success. Chocolatey and global Scoop operations use a separate elevated window, so their output cannot be shown in TidyWindow, and their cards point you to that window. Declining elevation reports a failure and the queue moves on.

The queue belongs to the current session and is not resumed after a crash. Closing is blocked while work remains.

## Search

Package searches run against every source at once and appear as each finishes, after a 250 ms debounce. A bounded in-memory cache keeps successful searches for 60 seconds and shares identical in-flight work. Ranking is local: exact IDs and names first, then whole-word, prefix, substring and typo matches, scored with [BM25](https://lucene.apache.org/core/10_5_2/core/org/apache/lucene/search/similarities/BM25Similarity.html) and bounded Damerau–Levenshtein distance. It can only rank what a source returned.

Installed status comes from each manager's local inventory plus the Windows uninstall registry. Exact IDs win; fuzzy name matching is deliberately not used, so forks and preview editions are not marked installed by similarity.

Scout uses [Everything](https://www.voidtools.com/)'s local index through a separate worker for Home and This PC searches, and its own index for a chosen folder (no dependency). It returns up to 150 ranked results, never reads file contents and never deletes files. Details and limits are in [docs/scout-research.md](docs/scout-research.md).

## Logs

Logs record only what is worth acting on: install and setup outcomes, a package manager changing state, a failing search source and unexpected errors. A normal session leaves them empty. An identical event within six hours bumps one entry's count. Entries are JSON lines in `%LOCALAPPDATA%\TidyWindow.Next\Logs`, with your account name replaced by `%USERPROFILE%`.

## Build

Requires Windows 10 build 19041 or later (x64) and the .NET 10 SDK. Package managers are optional; install at least one to search and install software.

```powershell
dotnet restore TidyWindow.slnx --locked-mode
dotnet build app/TidyWindow.Desktop.csproj -c Release --no-restore --output artifacts/preview
dotnet test tests/TidyWindow.Tests/TidyWindow.Tests.csproj -c Release --no-restore
./artifacts/preview/TidyWindow.Next.exe
```

For a folder that includes .NET and the Windows App SDK:

```powershell
dotnet publish app/TidyWindow.Desktop.csproj -c Release --self-contained true -o artifacts/publish
```

Tests never install packages or change settings. Live, read-only checks against the machine are opt-in:

```powershell
$env:TIDYWINDOW_LIVE_TESTS = '1'
dotnet test tests/TidyWindow.Tests/TidyWindow.Tests.csproj -c Release
```

## Repository

| Path | Contents |
| --- | --- |
| `app/` | WinUI shell, pages and observable state. |
| `core/` | Package manager adapters, catalogs, the Windows-facing systems, process execution and ranking. |
| `clean-host/` | The elevated helper for Windows-wide changes. |
| `search-host/` | The Scout worker that talks to Everything. |
| `tests/` | Parsing, catalog, safety-guard, ranking and layout tests. |
| `docs/` | Research behind each page: [Scout](docs/scout-research.md), [Quiet](docs/quiet-research.md), [Tune](docs/tune-research.md), [Fixes](docs/fixes-research.md), [Lab](docs/lab-research.md). |
| `tools/Brand/` | Script that renders the app icons. |

WinUI 3 gives native controls and desktop integration without a web renderer, and .NET keeps the app and backend in one language. This is a Windows-specific app by design. See Microsoft's [desktop framework guidance](https://learn.microsoft.com/en-us/windows/apps/desktop/).

## Branches and releases

- `main`: the current app and the default branch. CI builds and tests every push and pull request.
- `legacy`: the previous TidyWindow (v4.x), unchanged and no longer developed.

Pushing a tag such as `v5.0.0` runs the release workflow, which tests, publishes a self-contained build and attaches `TidyWindow-<version>-win-x64.zip` to a GitHub release. Releases up to v4.7.x are the legacy app.

## License

[MIT](LICENSE)

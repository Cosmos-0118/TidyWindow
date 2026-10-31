# Architecture and behavior

TidyWindow is a Windows-specific desktop app built with C#, .NET 10 and WinUI 3. Native controls and desktop integration run without a web renderer; the UI and backend share one language.

## Project map

| Path | Contents |
| --- | --- |
| [`src/TidyWindow.Desktop/`](../src/TidyWindow.Desktop/) | WinUI app. `Shell/` is the main window, `Features/` has one folder per page (view and view model together), and `Controls/`, `Common/` and `Styles/` are shared. |
| [`src/TidyWindow.Core/`](../src/TidyWindow.Core/) | Everything that does the work, one folder per area: `Packages/` (managers, search, queue), `Repair/`, `Cleanup/`, `Scout/`, `Paths/`, `Startup/`, `Background/`, `Tune/`, `Debloat/`, `Fixes/`, `Lab/`, plus `Infrastructure/` (process runner, logs, output) and `Icons/`. Each area keeps its catalog next to its code. |
| [`src/TidyWindow.CleanHost/`](../src/TidyWindow.CleanHost/) | The elevated helper for Windows-wide changes. |
| [`src/TidyWindow.SearchHost/`](../src/TidyWindow.SearchHost/) | The Scout worker that talks to Everything. |
| [`tests/TidyWindow.Tests/`](../tests/TidyWindow.Tests/) | Unit tests, in folders that mirror `TidyWindow.Core` (and `Desktop/` for the UI code they link). |
| [`tests/TidyWindow.UiSmoke/`](../tests/TidyWindow.UiSmoke/) | A small WinUI program that checks shared UI pieces and the live Scout lookup. Run by hand. |
| [`docs/`](./) | Research, implementation decisions and validation notes. |
| [`installer/`](../installer/) | Inno Setup script, the script that builds it, and an install/uninstall smoke test. |
| [`tools/Brand/`](../tools/Brand/) | Script that renders the app icons. |
| [`scripts/`](../scripts/) | Checks that run against the real PC and are not part of the test suite. |

Build output goes to `artifacts/` at the repository root (set once as `ArtifactsDir` in `Directory.Build.props`). Namespaces stay `TidyWindow.Next.Core` and `TidyWindow.Next.Desktop` whatever folder a file is in.

## Package search

Searches run against every available source at once and results appear as each finishes, after a 250 ms debounce. A bounded in-memory cache keeps successful searches for 60 seconds and shares identical in-flight work.

Ranking is local: exact IDs and names first, then whole-word, prefix, substring and typo matches, scored with BM25 and bounded Damerau–Levenshtein distance. It can only rank what a source returned.

Installed status comes from each manager's local inventory plus the Windows uninstall registry. Exact IDs win; fuzzy name matching is deliberately not used, so forks and preview editions are not marked installed by similarity.

## Package operations

Installs, updates and uninstalls share one queue across pages. It runs one operation at a time, rejects duplicate or conflicting requests for the same manager, package and scope, and continues after a failure. Output streams as it arrives, and each card keeps the newest 64 KiB.

Windows administrator prompts and installer dialogs stay native windows that the person responds to. The queue waits for the command to exit, then checks the inventory; if the expected change cannot be verified, the card says so rather than reporting success.

Chocolatey and global Scoop operations use a separate elevated window, so their output cannot be shown in TidyWindow; their cards point to that window. Declining elevation reports a failure and the queue moves on.

The queue belongs to the current session and does not resume after a crash. Closing while work remains asks first: Keep open is the default, and Close anyway stops the running command and drops what is waiting. A running package operation can also be stopped from the Queue page, and the queue moves on if a stop is ignored. A note kept on disk while an operation runs lets the next launch say that the last session ended partway.

## Scout

Scout uses Everything's local index through a separate worker for Home and This PC searches, and its own index for a chosen folder, without an external dependency. It returns up to 150 ranked results, never reads file contents and never deletes files.

See [Scout research](scout-research.md) for candidate limits, backend requirements and failure handling.

## Windows changes

Cleanup, Quiet, Tune, Debloat, Fixes and Lab act on reviewed catalogs checked when the app starts. A second allowlist refuses actions outside those catalogs. Windows-wide changes use `TidyWindow.CleanHost.exe`, launched only after the person approves elevation.

Debloat is the one page that removes software. The page scans without administrator rights and sends the helper only catalog item IDs. The helper loads the catalog again, checks each request against a protected list, removes the app for every account, rescans to confirm, and records the result so **Removed** can put it back. See [Debloat research](debloat-research.md).

Reversible changes save original state before writing and read back the result where supported. Cleanup holds old installers and unfinished downloads for 14 days; ordinary cache rules delete their files. Held files, installer repair backups and startup restore data survive app restarts. Windows repair commands and package operations have their own effects and do not have a universal undo mechanism.

The per-tool research notes linked from the [README](../README.md#documentation) describe the contracts and limitations in more detail.

## Logs

Logs record events worth acting on: install and setup outcomes, a package manager changing state, a failing search source and unexpected errors. A normal session can leave them empty. An identical event within six hours bumps one entry's count.

Entries are JSON lines in `%LOCALAPPDATA%\TidyWindow.Next\Logs`, with the account name replaced by `%USERPROFILE%`. This replacement does not remove every potentially identifying detail; review logs before sharing them.

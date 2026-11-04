# Architecture and behavior

TidyWindow is a Windows-specific desktop app built with C#, .NET 10 and WinUI 3. Native controls and desktop integration run without a web renderer; the UI and backend share one language.

## Project map

| Path | Contents |
| --- | --- |
| [`src/TidyWindow.Desktop/`](../src/TidyWindow.Desktop/) | WinUI app. `Shell/` is the main window, `Features/` has one folder per page (view and view model together), and `Controls/`, `Common/` and `Styles/` are shared. |
| [`src/TidyWindow.Core/`](../src/TidyWindow.Core/) | Everything that does the work, one folder per area: `Packages/` (managers, search, queue), `Repair/`, `Cleanup/`, `Scout/`, `Paths/`, `Startup/`, `Background/`, `Tune/`, `Debloat/`, `Fixes/`, `Unstick/`, `Lab/`, `Crashes/`, plus `Infrastructure/` (process runner, logs, output) and `Icons/`. Each area keeps its catalog next to its code. |
| [`src/TidyWindow.CleanHost/`](../src/TidyWindow.CleanHost/) | The elevated helper for Windows-wide changes. |
| [`src/TidyWindow.SearchHost/`](../src/TidyWindow.SearchHost/) | The Scout worker that talks to Everything. |
| [`tests/TidyWindow.Tests/`](../tests/TidyWindow.Tests/) | Unit tests, in folders that mirror `TidyWindow.Core` (and `Desktop/` for the UI code they link). |
| [`tests/TidyWindow.UiSmoke/`](../tests/TidyWindow.UiSmoke/) | A small WinUI program that checks shared UI pieces and the live Scout lookup. Run by hand; it is part of the solution so CI keeps it compiling. |
| [`docs/`](./) | Research, implementation decisions and validation notes. |
| [`installer/`](../installer/) | Inno Setup script, the script that builds it, and an install/uninstall smoke test. |
| [`tools/Brand/`](../tools/Brand/) | Script that renders the app icons. |
| [`scripts/`](../scripts/) | Checks that run against the real PC and are not part of the test suite. |
| [`.github/`](../.github/) | CI (locked restore, build, tests, installer smoke test), the release workflow, CodeQL and Dependabot. Actions are pinned to commit SHAs. |

Build output goes to `artifacts/` at the repository root (set once as `ArtifactsDir` in `Directory.Build.props`). Release builds keep their debug information inside the assemblies (`DebugType=embedded`), so stack traces carry line numbers without separate `.pdb` files. Namespaces stay `TidyWindow.Next.Core` and `TidyWindow.Next.Desktop` whatever folder a file is in.

## Package search

Searches run against every available source at once and results appear as each finishes, after a 250 ms debounce. A bounded in-memory cache keeps successful searches for 60 seconds and shares identical in-flight work.

Ranking is local: exact IDs and names first, then whole-word, prefix, substring and typo matches, scored with BM25 and bounded Damerau–Levenshtein distance. It can only rank what a source returned.

Installed status comes from each manager's local inventory plus the Windows uninstall registry. Exact IDs win; fuzzy name matching is deliberately not used, so forks and preview editions are not marked installed by similarity.

## Package operations

Installs, updates and uninstalls share one queue across pages. It runs one operation at a time, rejects duplicate or conflicting requests for the same manager, package and scope, and continues after a failure. Output streams as it arrives, and each card keeps the newest 64 KiB.

Windows administrator prompts and installer dialogs stay native windows that the person responds to. The queue waits for the command to exit (an install, update or uninstall gets two hours before it is ended; reads keep short limits), then checks the inventory; if the expected change cannot be verified, the card says so rather than reporting success.

A command runs with administrator rights only when its program lives where accounts without those rights cannot change it (`ElevatedProgram`): nothing under a profile, the temporary folder or a folder whose permissions let ordinary accounts write, and nothing whose permissions cannot be read. WinGet's command is an alias in the profile, so the packaged program it stands for under Program Files\WindowsApps is what is checked and run. A program that fails the check is not run elevated and the card says why; because Scoop keeps its script in the profile, its all-users operations are refused. The same rule covers an app's own uninstaller when the ladder runs one for a per-machine entry, whose command is expanded with the PC's variables only, never the person's.

Chocolatey operations use a separate elevated window, so their output cannot be shown in TidyWindow; their cards point to that window. Declining elevation reports a failure and the queue moves on. Scoop keeps its script in your profile, where any program you run could change it, so TidyWindow does not run Scoop operations for all users with administrator rights; the card says to use an administrator PowerShell window for those.

The queue belongs to the current session and does not resume after a crash. Closing while work remains asks first: Keep open is the default, and Stop and close drops what is waiting and stops the running command, then closes once that has happened. A fix step that must not be ended half way (DISM, SFC) is let finish and the steps that put things back still run, so the window waits for them behind a "Finishing before closing" dialog instead of for a fixed time; its Close now button is the one way out early. A running fix can be stopped from the Queue page too. If it is still finishing a step it must not end, a Stop waiting button appears: the queue moves on, the fix is not ended and carries on in the background (its real result reaches Logs when it ends), and another fix waits for its turn until then. Closing while one is still finishing says so, because its clean-up steps will not run. A running package operation can also be stopped from the Queue page, and the queue moves on if a stop is ignored; the next operation then waits for the ignored command to end before it starts, and can itself be stopped. A note kept on disk while an operation runs lets the next launch say that the last session ended partway.

## Scout

Scout uses Everything's local index through a separate worker for Home and This PC searches, and its own index for a chosen folder, without an external dependency. It returns up to 150 ranked results, never reads file contents and never deletes files.

See [Scout research](scout-research.md) for candidate limits, backend requirements and failure handling.

## Windows changes

Cleanup, Quiet, Tune, Debloat and Fixes act on catalogs embedded in `TidyWindow.Core` and checked when the app starts; a second allowlist in each feature refuses anything outside them. Lab has no catalog: `PowerGuard` allows a fixed set of `powercfg` arguments, and the priority change applies to one process the person picked. Windows-wide changes run in `TidyWindow.CleanHost.exe`, started only after the person approves elevation.

### The helper boundary

The app runs as the ordinary account and never elevates itself. When a feature needs administrator rights, the app first creates a named pipe with a random name whose permissions admit only the current account, administrators and SYSTEM (`HelperPipe.CreateServer`), then starts the helper through the Windows permission prompt from the `CleanHost` folder next to the app (`HelperPipe.LaunchElevated`). The helper connects by name, checks that the pipe's owner is the account it was started for, and reads JSON lines with a ceiling on line length. The app sends catalog item IDs and arguments; the helper loads its own copy of the catalog and rebuilds every action from it, so a damaged or hostile app process can choose which entry runs, not what it does.

Anything the helper later writes back with administrator rights comes from `ProtectedStore` ([`src/TidyWindow.Core/Infrastructure/ProtectedStore.cs`](../src/TidyWindow.Core/Infrastructure/ProtectedStore.cs)): a folder under `%ProgramData%\TidyWindow.Next` that administrators own and no other account can change, created with those permissions in one step and checked again on every call. A folder or file there with the wrong owner or permissions is moved aside and never read. The startup items the helper removed, the registry a repair backed up and the originals of tweaks are kept there; the app keeps its own copy under `%LOCALAPPDATA%\TidyWindow.Next` for display, and the helper never trusts that copy.

Two refusals follow from the same rule. `MachinePath` will not add a folder to the Windows-wide PATH when an account without administrator rights could change it or any folder above it, because a program planted there would run as whoever next types the command. And the helper starts package managers only from locations ordinary accounts cannot change.

The app itself will not run as administrator. [`AppElevation`](../src/TidyWindow.Core/Infrastructure/AppElevation.cs) checks first thing, before anything is read or started: with a full administrator token, every history file the app reads and every package manager it starts would be working with those rights, and only the helper is written for them. So it says so and closes. With User Account Control off there is no limited token to go back to and no boundary to protect, so the check does not apply.

What this does not cover: the helper is whatever `CleanHost\TidyWindow.CleanHost.exe` is in the install folder when the prompt is approved. Under Program Files only administrators can change it; in a per-user install or a portable copy in a writable folder, a program running as the user can replace it beforehand, and the prompt then approves that program. The installer therefore defaults to an all-users install and offers per-user as a choice, and the README says why.

Debloat is the one page that removes software. The page scans without administrator rights and sends the helper only catalog item IDs. The helper loads the catalog again, checks each request against a protected list, removes the app for every account, rescans to confirm, and records the result so **Removed** can put it back. See [Debloat research](debloat-research.md).

Reversible changes save original state before writing and read back the result where supported. Cleanup holds old installers and unfinished downloads for 14 days; ordinary cache rules delete their files. Held files, installer repair backups and startup restore data survive app restarts. Windows repair commands and package operations have their own effects and do not have a universal undo mechanism.

The per-tool research notes linked from the [README](../README.md#documentation) describe the contracts and limitations in more detail.

## Crashes

Crashes reads app crashes, freezes and Windows problems (blue screens, failing services, driver resets, disk and hardware errors) from the Windows event log, using the system's own query engine so Windows does the filtering. Nothing watches the log: a scan runs when the page opens, on Refresh, and once shortly after launch so the sidebar can show whether something is new. A scan reads only what is newer than the last, on a lowest-priority thread.

Records are kept in `%LOCALAPPDATA%\TidyWindow.Next\Crashes` (up to 6,000 records and 120 days) so the page opens at once and outlives Windows' own log rotation. Duplicates are merged (a .NET crash is written twice, a blue screen up to three times), repeats of one system problem within ten minutes become one entry with a count, and search runs over strings built once per refresh. See [Crashes research](crashes-research.md).

This is separate from Logs, which records what TidyWindow itself did.

## Logs

Logs record events worth acting on: install and setup outcomes, a package manager changing state, a failing search source and unexpected errors. A normal session can leave them empty. An identical event within six hours bumps one entry's count.

Entries are JSON lines in `%LOCALAPPDATA%\TidyWindow.Next\Logs`, with the account name replaced by `%USERPROFILE%`. This replacement does not remove every potentially identifying detail; review logs before sharing them. A crash is recorded with the exception and its stack trace; because release builds carry their debug information inside the assemblies, those traces name the source file and line, with the build machine's paths replaced by the repository-relative `/_/` form on CI.

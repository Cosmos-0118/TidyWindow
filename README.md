# TidyWindow

A new native Windows app, built from scratch with C#, .NET 10 and WinUI 3. The previous TidyWindow is preserved on [`main`](https://github.com/Cosmos-0118/TidyWindow/tree/main).

The app keeps its main pages focused:

- **Overview:** detect WinGet, Chocolatey and Scoop, check their versions, and open setup help when needed.
- **Install Hub:** search real package sources, rank matches, recognize installed apps, and review an installation before starting it.
- **Installed:** find installed packages, select apps to update or uninstall, and check available versions.
- **Scout:** find files and folders by name/path, with ranked results, type/location filters, copy path and open location.
- **Quiet:** see known Windows background items (telemetry, screen-watching features such as Recall, optional services) and turn each off or back on. Only a reviewed catalog can be changed, and anything other things rely on is locked. See [docs/quiet-research.md](docs/quiet-research.md).
- **Tune:** a short, reviewed list of Windows registry settings that make a noticeable difference (big folders, shutdown, menus, Edge in the background, ads and suggestions, the full right-click menu, mouse acceleration and a few more). Popular "registry booster" values with no evidence behind them are left out on purpose. Each setting shows its current value, saves the original before a change and can be put back exactly. See [docs/tune-research.md](docs/tune-research.md).
- **Fixes:** a short list of repairs for common Windows problems (system files, Windows Update, the drive, network, printing, sound, Store, Start menu, icons, OneDrive). Each is a plain list of standard Windows commands shown before it runs, with options and an honest note on what it changes. Fixes run in the Queue, so every step's output shows live, including fixes that need administrator permission. See [docs/fixes-research.md](docs/fixes-research.md).
- **Lab:** measure what a change really does. A 15-second check of processor wake-up speed, all-core work, memory copy and random memory access is saved with the settings in force and compared with your previous one, saying "no clear difference" unless the change is bigger than the normal wobble. It also switches the Windows power plan to Ultimate Performance and back, shows whether memory integrity is on (with a link to turn it off in Tune), and can ease or boost one running app until it closes. The old Performance Lab's boot timer, service, tracing and core-pinning tweaks are left out because they do not help. See [docs/lab-research.md](docs/lab-research.md).
- **Logs:** inspect outcomes and problems when needed.

Installs, updates and uninstalls share one queue across pages. You can keep adding apps while an operation runs. Select all applies to the visible Installed list; Update selected queues only apps with an available update and skips held packages. Bulk uninstall asks for confirmation of the specific apps. Changing a filter clears the selection.

The queue runs one operation at a time in order, rejects duplicate or conflicting requests for the same manager/package/scope, and continues after failures. Queue in the sidebar opens a full page with running, waiting and recent finished operations. Click a card to expand its command output. Output streams as bytes arrive (including partial lines), handles UTF-8 across chunk boundaries, and retains the newest 64 KiB per operation. UI updates are coalesced and keep the card expanded; output follows the bottom until you scroll up. Clear finished removes the session's card history; durable outcomes remain in Logs. Remove and Clear waiting never interrupt the active installer.

Windows administrator prompts and installer dialogs remain native windows that the user must respond to. The queue waits for the command process to exit, then verifies inventory. A launcher that detaches from its installer cannot be tracked reliably; if the expected change cannot be verified, the card reports that rather than claiming success. Chocolatey and global Scoop operations use a separate elevated window, so their stdout cannot be redirected into TidyWindow with this launch method. Their cards explicitly direct you to that window. Normal captured WinGet and per-user Scoop output can stream; output from a separate GUI installer is not a console stream.

Queue output uses selectable rich text: blue steps/information, green success, amber warnings and red errors, with STEP/OK/WARN/ERROR labels. Colors adapt to light, dark and high-contrast settings. PowerShell CLIXML is decoded incrementally into typed messages; duplicate host records and internal serialization metadata are omitted. The formatted view shows at most the newest 600 lines to keep verbose commands responsive.

The Queue page's **Show installer options for new operations** setting asks newly queued WinGet/Chocolatey installs and updates to use their supported interactive mode (`--interactive` / `--not-silent`). Package support varies; Scoop uses its package scripts. This does not automatically answer security prompts or click installer controls. Normal package commands have a 20-minute timeout; explicitly interactive commands allow two hours. Declining elevation reports a failure and the queue proceeds to the next item.

The queue belongs to the current app session and is not resumed after a crash. Closing is blocked while work remains. Outcomes are saved in Logs rather than shown in a bottom results panel. When the queue drains, manager status and installed inventories refresh together. Scoop update checks use local bucket manifests.

The interface uses a quiet sidebar and simple rows, with layouts that adapt from a narrow window to a large display. It follows the restrained layout of Clop for Windows rather than adding dashboard panels.

Scout uses Everything's local index through a separate worker process for indexed Home/This PC searches. Enable instant search queues the Everything installation; it may request administrator permission for its index service. A chosen-folder search always uses TidyWindow's own reusable index and needs no dependency. Folder indexing includes hidden names, skips offline entries, never follows directory reparse points, respects current permissions and reports inaccessible locations. It keeps up to 400,000 entries in memory, watches name changes, and asks for a refresh after a watcher overflow or newly moved tree. It does not read file contents or delete files.

Scout returns up to 150 ranked results. The Everything worker retrieves bounded candidate sets (2,000 broad/smart candidates plus an exact-name pass); broad-query ranking is therefore not a guarantee of global top matches. Alternate/beta Everything instance names are not supported. A folder search starts with directory discovery, so its first result set depends on filesystem speed; subsequent searches reuse discovered metadata. Research, alternatives, primary sources and local measurement notes are in [docs/scout-research.md](docs/scout-research.md).

Scout detects installation separately from index readiness. It starts Everything's user process when only its Windows service is running, updates after setup, and retries readiness while the page is open. Setup shows Installing/Indexing while pending, hides when ready, and offers Retry for an installed engine that cannot respond. It does not offer another installation just because the index is still starting. Fuzzy retrieval targets names and is skipped for existing exact matches; literal paths remain searchable.

## Develop

Requires Windows 10 build 19041 or later, x64, and the .NET 10 SDK. Package managers are optional; install at least one to search and install software.

```powershell
dotnet restore TidyWindow.slnx --locked-mode
dotnet build app/TidyWindow.Desktop.csproj -c Release --no-restore --output artifacts/preview
dotnet test tests/TidyWindow.Tests/TidyWindow.Tests.csproj -c Release --no-restore
./artifacts/preview/TidyWindow.Next.exe
```

The preview includes the Windows App SDK runtime and requires the .NET 10 runtime. For a distributable folder including .NET, publish with `dotnet publish app/TidyWindow.Desktop.csproj -c Release --self-contained true -o artifacts/publish`.

Opt into live, read-only package manager checks separately:

```powershell
$env:TIDYWINDOW_LIVE_TESTS = '1'
dotnet test tests/TidyWindow.Tests/TidyWindow.Tests.csproj -c Release
```

Tests never install packages. Live checks use the sources and installed software on the machine running them.

## Design

- `app/`: WinUI shell, adaptive layout and observable page state.
- `core/`: package manager adapters, bounded process execution, installed inventory, relevance ranking and search cache.
- `tests/TidyWindow.Tests/`: parsing, identity matching, ranking, cancellation, caching and layout policy checks.

WinUI 3 provides native Windows controls and desktop integration without a web renderer in the UI. .NET 10 keeps the app and backend in one language. This is a Windows-specific choice; the app does not attempt a cross-platform abstraction. See Microsoft's [desktop framework guidance](https://learn.microsoft.com/en-us/windows/apps/desktop/).

Searches run concurrently and appear as each source finishes. A 250 ms debounce avoids starting work for every keystroke. A bounded in-memory cache retains successful searches for 60 seconds (empty responses for 15 seconds), shares identical in-flight work and cancels work once it has no consumers. Overview's Refresh clears the cache and reloads installed inventory. Errors are never cached.

Ranking prioritizes exact IDs and names, then whole-word, prefix, substring and typo matches. Weighted [BM25](https://lucene.apache.org/core/10_5_2/core/org/apache/lucene/search/similarities/BM25Similarity.html) scores distinguish candidates within those tiers; bounded Damerau–Levenshtein distance handles adjacent transpositions. Names and product IDs outweigh publisher tokens. Ranking runs locally on the candidates returned by the managers, without another network call or service. It cannot retrieve a package the source did not return. Each source currently contributes up to 60 search results.

Installed status comes from each manager's complete local inventory and the Windows uninstall registry, refreshed at startup, on Refresh and after an installation. Exact source IDs take precedence. Exact normalized display names recognize some apps across sources, but fuzzy name matching is deliberately excluded from installed detection so that forks, related apps and preview editions are not marked installed by similarity. Unrecognized aliases may still require a source-specific match. A failed inventory read is reported rather than treated as proof an app is absent.

Commands use explicit argument lists; Scoop arguments are encoded as literal PowerShell strings. Scoop prefers PowerShell 7 and uses a process-scoped RemoteSigned policy without changing the saved machine policy. The app runs without administrator rights; Chocolatey requests elevation only for its installation process. Installation uses the exact selected package ID and source and accepts its license agreements after the review dialog. The selected version is informational: the manager installs its latest available version.

The CLI adapters fail visibly when their output cannot be read. Initial search time still depends on source readiness, network response and CLI startup. Installation progress currently shows status and completion output; it does not stream a percentage or support rollback.

## Logs

The Logs page records only what is worth acting on: install and setup outcomes, package managers changing state (a problem appearing, or recovering), a search source failing, and unexpected errors. Routine activity such as searches, refreshes and a manager that stays healthy is not recorded, so a normal session leaves the log empty.

An identical event repeated within six hours updates one entry's count instead of adding another. Entries are stored as JSON lines in `%LOCALAPPDATA%\TidyWindow.Next\Logs` (about 2 MB at most across two files; the newest 500 are loaded), written when the event happens so they survive a crash. The Windows account name is replaced with `%USERPROFILE%`. Logging never throws: if the disk is unwritable the page says so and keeps entries in memory. Entries can be filtered by severity, area and text, and copied individually or as a transcript.

## Branches

- `main`: archived previous version; left unchanged.
- `master`: new TidyWindow and GitHub default branch.
- `Developer`: integration branch for the new app.
- `codex/rebuild-foundation`: local implementation branch.

Old code is retained in Git history and on `main`; it is not part of this app's source tree or build.

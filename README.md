# TidyWindow

A new native Windows app, built from scratch with C#, .NET 10 and WinUI 3. The previous TidyWindow is preserved on [`main`](https://github.com/Cosmos-0118/TidyWindow/tree/main).

This first version contains only two pages:

- **Overview:** detect WinGet, Chocolatey and Scoop, check their versions, and open setup help when needed.
- **Install Hub:** search real package sources, rank matches, recognize installed apps, and review an installation before starting it.

The interface uses a quiet sidebar and simple rows, with layouts that adapt from a narrow window to a large display. It follows the restrained layout of Clop for Windows rather than adding dashboard panels.

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

## Branches

- `main`: archived previous version; left unchanged.
- `master`: new TidyWindow and GitHub default branch.
- `Developer`: integration branch for the new app.
- `codex/rebuild-foundation`: local implementation branch.

Old code is retained in Git history and on `main`; it is not part of this app's source tree or build.

# Contributing to TidyWindow

Thanks for helping. Bug reports, ideas, documentation fixes and pull requests are all welcome.

By taking part you agree to follow the [Code of Conduct](CODE_OF_CONDUCT.md). To report a security problem, follow [SECURITY.md](SECURITY.md) instead of opening a public issue.

## Ways to help

- **Report a bug.** [Open a bug report](https://github.com/Cosmos-0118/TidyWindow/issues/new/choose) with the page you were using, your TidyWindow version (Settings → Current version), how you installed it, your Windows version and what you expected to happen.
- **Suggest a feature or a catalog change.** Explain the problem first. For a new Tune, Quiet, Cleanup, Debloat or Fixes entry, link the Microsoft documentation or other evidence that shows what it does.
- **Improve the docs.** The research notes in [`docs/`](docs/) are part of the project. Corrections and better sources are as useful as code.
- **Send a pull request.** For anything larger than a small fix, open an issue first so we can agree on the approach before you spend time on it.

Issues labelled [`good first issue`](https://github.com/Cosmos-0118/TidyWindow/labels/good%20first%20issue) and [`help wanted`](https://github.com/Cosmos-0118/TidyWindow/labels/help%20wanted) are good places to start.

## Sharing logs safely

Logs live in `%LOCALAPPDATA%\TidyWindow.Next\Logs`. The account name is replaced by `%USERPROFILE%`, but package names, other local paths and, for a crash, the stack trace can still appear. Read a log before you attach it and remove anything you do not want to be public.

## Set up a development environment

You need:

- **Windows 10 build 19041 or later, x64.** The app is WinUI 3 and only builds and runs on Windows.
- **The [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).** The exact version and roll-forward policy are in [`global.json`](global.json).
- Package managers (WinGet, Chocolatey, Scoop) are optional for building.

```powershell
git clone https://github.com/Cosmos-0118/TidyWindow.git
cd TidyWindow

dotnet restore TidyWindow.slnx --locked-mode
dotnet build src/TidyWindow.Desktop/TidyWindow.Desktop.csproj -c Release --no-restore --output artifacts/preview
./artifacts/preview/TidyWindow.Next.exe
```

Use `dotnet build`, not `dotnet publish`, for the desktop app: `publish` leaves out the compiled pages and the app closes at startup.

If Windows Search starts listing many `TidyWindow.*.exe` copies from `bin/`, `obj/` and `artifacts/`, run `pwsh scripts/exclude-build-output-from-search.ps1` once.

### Project layout

| Folder | What is in it |
| --- | --- |
| `src/TidyWindow.Core` | The work itself, one folder per area (`Packages`, `Cleanup`, `Tune`, `Debloat` and so on). Catalogs such as `tune-catalog.json` sit next to the code that uses them. |
| `src/TidyWindow.Desktop` | The WinUI 3 app: pages in `Features/`, shared controls, styles and the shell. |
| `src/TidyWindow.CleanHost` | The helper that runs Windows-wide changes with administrator rights after the user approves the prompt. |
| `src/TidyWindow.SearchHost` | The Scout search helper and the Everything SDK integration. |
| `tests/TidyWindow.Tests` | The test suite, one folder per area. |
| `docs/` | Architecture notes and the research behind each tool. |

[`docs/architecture.md`](docs/architecture.md) explains how the pieces fit together.

## Run the tests

Run the tests the way CI does, after the restore above:

```powershell
dotnet build TidyWindow.slnx -c Release --no-restore
dotnet test tests/TidyWindow.Tests/TidyWindow.Tests.csproj -c Release --no-build
```

The standard tests do not install packages, change Windows settings or need a network. Tests that look at the real PC are opt-in and are reported as skipped until you switch them on; `dotnet test --filter "Category!=Live"` leaves them out altogether.

| Variable | What it switches on |
| --- | --- |
| `TIDYWINDOW_LIVE_TESTS=1` | Read-only checks of this PC's packages, services, registry, installed programs and caches. |
| `TIDYWINDOW_LIVE_DOWNLOAD=1` (with the one above) | Also downloads one vendor installer and unpacks it in a temp folder. |
| `TIDYWINDOW_RECOVERY_TESTS=1` with `TIDYWINDOW_TEST_ARTIFACTS=<folder>` | Copies your Scoop into that folder and runs the recovery scripts on the copy. |
| `TIDYWINDOW_ELEVATED_TESTS=1`, in an administrator window | Runs the privileged repair path against a throwaway key under `HKCU`. |

Add or update tests in `tests/TidyWindow.Tests/<Area>/` for the behavior you change.

## Making a change

1. Fork the repository and create a branch from `main`, named for the change, for example `fix/queue-cancel` or `feat/scout-filters`.
2. Keep the change focused. One fix or feature per pull request is easier to review and to revert.
3. Follow the existing style. [`.editorconfig`](.editorconfig) sets indentation (4 spaces, 2 for YAML and JSON). [`Directory.Build.props`](Directory.Build.props) treats warnings as errors, including nullable warnings and NuGet vulnerability audits, so a change must build cleanly.
4. Dependencies are restored in locked mode. If you add or update a NuGet package, commit the updated `packages.lock.json` files too.
5. Write commit messages that say what changed, for example `fix: Keep the queue running after a cancelled install`. A `feat:` or `fix:` prefix is preferred.
6. Run the build and the tests before you open the pull request, and fill in the pull request template.

### Rules for catalog changes

Cleanup, Quiet, Tune, Debloat and Fixes only act on entries in their catalogs, and the elevated helper rebuilds each action from its own copy of them. Because these entries change Windows for real:

- Tie every entry to documented Windows behavior. Link the source in the pull request.
- Update the matching research note in `docs/` (for example [`docs/tune-research.md`](docs/tune-research.md)) to say what the entry does, why it is safe and how it is undone.
- Keep the undo path working: reversible changes must save the original state before they write.
- Settings with no evidence behind them (common "registry boosters") are left out on purpose and will not be accepted.

### UI changes

Check the page in both light and dark themes and with Windows high contrast on. Give new interactive controls an accessible name (`AutomationProperties.Name`) when their visible text does not describe them. Include screenshots in the pull request. See [ACCESSIBILITY.md](ACCESSIBILITY.md) for what the app currently supports.

## Continuous integration

CI builds and tests every pull request. It only runs the test groups affected by the files you changed (the rules are in [`.github/ci/test-areas.json`](.github/ci/test-areas.json)); a push to `main` runs everything. The required check is **CI result**. CodeQL also scans the C# code.

## License

By contributing, you agree that your contributions are licensed under the project's [MIT license](LICENSE).

# Project Oblivion Rework

Project Oblivion is a powerful uninstaller, but its current safety net is paper thin. The notes below capture why it deletes unrelated apps today and what we need to rebuild.

## Snapshot of critical faults

-   **Selection fails open:** `automation/scripts/oblivion-force-cleanup.ps1` and `automation/scripts/uninstall-app-deep.ps1` default to deleting every discovered artifact when the selection JSON is missing, empty, or malformed. Even `-WaitForSelection` only delays for a timer; once it expires, everything is selected.
-   **Heuristics outrun boundaries:** `automation/modules/TidyWindow.Automation.psm1` returns any directory whose name shares a token with the app (`Invoke-OblivionArtifactDiscovery`). Tokens include publisher names, tags, and sanitized IDs, so common words like "driver", "studio", or "helper" match unrelated products.
-   **Process sweep kills by substring:** `Find-TidyRelatedProcesses` (same module) falls back to `procKey.Contains(nameKey)`. Short names such as "Go" or "Edge" match system processes and we then stop and delete their folders.
-   **Force removal escalates aggressively:** `Invoke-OblivionForceDirectoryRemoval` and friends take ownership, robocopy `/MIR` empty folders into targets, and queue `PendingFileRenameOperations` with no guard that the path sits under the target app. One false positive wipes entire `Program Files` trees.
-   **Inventory + dedupe are opaque:** `automation/scripts/get-installed-app-footprint.ps1` produces blended records from registry, managers, AppX, Steam, shortcuts, etc. `ProjectOblivionViewModel.DeduplicateApps` then merges them with heuristic keys (package family → manager hint → install root → normalized name). When those heuristics disagree, duplicates linger and feed multiple uninstall attempts into the same run.
-   **No safety tests:** `tests/TidyWindow.Automation.Tests/ProjectOblivion/ProjectOblivionScriptTests.cs` only asserts that a dry-run emits a summary. We never verify that we _skip_ artifacts outside the selected app, that selection timeouts work, or that `Find-TidyRelatedProcesses` ignores system services.
-   **CLI path missing:** Every script tries to load `data/catalog/oblivion-inventory.json`, but that file is absent in the repo. Outside the GUI (which writes a temp snapshot), the tooling just dies or runs with stale snapshots.

## Why unrelated files get deleted

1. **Selection hand-off is brittle**  
   `ProjectOblivionPopupViewModel` always creates a temp selection file, but if the UI crashes or the operator cancels before committing, the script sees no file and falls back to selecting all artifacts (`Resolve-ArtifactSelection`). Even when the selection exists, its schema is overloaded (`selectedIds`, `removeAll`, `removeNone`, `deselectIds`) which makes automation bugs likely.
2. **Discovery trusts any match**
    - `Get-TidyCandidateDataFolders` simply appends the alphanumeric version of the app name to `%ProgramData%`, `%LOCALAPPDATA%`, and `%APPDATA%` and assumes those folders belong to the app. Apps that share a vendor prefix ("Adobe", "Microsoft" etc.) collide immediately.
    - `Invoke-OblivionArtifactDiscovery` walks entire `Program Files` and `Program Files (x86)` roots, adding the first _N_ folders whose names contain any token. Tokens come from `App.tags`, `App.publisher`, even `App.appId`, so a tag like `game` can sweep unrelated launchers into the artifact list.
    - Related process detection (`Find-TidyRelatedProcesses`) feeds back into discovery (`ProcessImageDirectory` reason). If we kill `chrome.exe` because "Chro" matches another product, we also add Chrome's install directory to the artifact list and delete it.
3. **Removal never validates context**  
   None of the `Invoke-OblivionForce*` functions check that a path is either beneath the install root(s) or inside a whitelist produced by inventory. Once a path winds up in the artifact list, it is fair game for take-ownership plus recursive deletes, even if it points to `C:\Windows\System32`.
4. **Telemetry encourages false confidence**  
   Cleanup verification relies on `Test-LocalArtifactRemoved` (force-cleanup) or `Test-OblivionArtifactRemoved` (module). Both only check for path existence. If we delete the wrong directory, verification happily reports success because the path disappeared.

## Rework blueprint

1. **Fail-safe selection handshake**

    - In scripts, treat missing/invalid selection data as a hard failure unless `-AutoSelectAll` was passed explicitly. Stop assuming "no input" means "delete everything".
    - Persist selections alongside the inventory snapshot (e.g., `AppData/Local/TidyWindow/ProjectOblivion/<app-id>/selection.json`) and sign them with a hash so background runs can resume safely.
    - Simplify the schema to `{ selectedIds: [], deselectedIds: [] }` and reject any other shape.

2. **Scoped artifact graph**

    - Require every artifact to be justified by at least one trusted anchor: install root, registry install location, manager manifest, or explicit user approval. Heuristic matches (token scans, process directories, start-menu shortcuts) should start as _candidates_ that default to unselected.
    - Cap each heuristic set by both count and depth (e.g., `Program Files` scan confined to directories under the known vendor root, never the whole drive).
    - Record provenance (`reason`, `confidence`, `sourceAnchor`) directly in the artifact payload so the UI can show why a path is proposed.

3. **Process/service correlation boundaries**

    - Replace substring matching with structured rules: compare full image paths against known install roots or package family folders. Only fall back to name matching when the name is unique _and_ longer than a threshold.
    - Never terminate processes running from `C:\Windows`, `%SystemRoot%`, or other protected locations unless the installer explicitly identified them.

4. **Removal engine safety valves**

    - Before running any force strategy, ensure the path sits under an approved root (install root, artifact hint) or is in an allow list (registry key created by the app). If not, skip and surface the conflict to the operator.
    - Remove destructive fallbacks such as robocopy-ing empty folders or mass-updating `PendingFileRenameOperations` until the path passes validation.
    - Add a dry-run summary that shows the command lines (`takeown`, `icacls`, `robocopy`, `sc.exe delete`, etc.) before execution so operators can spot mistakes.

5. **Inventory + dedupe clarity**

    - Emit per-source identifiers (`registry:<hive>:<key>`, `winget:<id>`, `steam:<id>`) and keep them separate in the UI so we can run the vendor uninstall more than once if needed without merging everything upfront.
    - Produce the `oblivion-inventory.json` snapshot the CLI expects (and commit a template under `data/catalog/`), so scripted runs have deterministic data.
    - Revisit `ProjectOblivionViewModel.DeduplicateApps`: default to showing all sources until the user confirms a merge, and keep the dedupe rules in a single, testable helper instead of the current layered heuristics.

6. **Testing + telemetry**
    - Unit-test artifact discovery with fixtures that include decoy folders (`C:\Program Files\Common Files`, `C:\ProgramData\Shared`) to ensure they are not selected by default.
    - Add integration tests around `Resolve-ArtifactSelection`, covering timeouts, corrupt JSON, empty files, and resuming existing selections.
    - Log every rejected artifact with the reason (e.g., "Skipped `C:\Windows\System32` – outside approved roots") so we can audit future runs.

## Immediate guardrails we can ship now

-   Make `Resolve-ArtifactSelection` throw if no selection file is provided when `-WaitForSelection` is set (automation/scripts/oblivion-force-cleanup.ps1, uninstall-app-deep.ps1).
-   In `Invoke-OblivionArtifactDiscovery`, ignore tokens shorter than four characters and blacklist paths under `%SystemRoot%`, `%WINDIR%`, `%ProgramFiles%\Common Files`, and `%ProgramFiles%\WindowsApps` unless the inventory explicitly called them out.
-   Change `Find-TidyRelatedProcesses` to require either an install-root prefix match or an explicit `processHints` entry. Remove the `nameKey` fallback entirely until a safer matcher exists.
-   Skip `Invoke-OblivionForceDirectoryRemoval` for any path whose drive root is not in the approved artifact list, and bubble a `requiresManualReview` flag to the UI instead of deleting blindly.

Locking down these pieces lets us keep Project Oblivion "simple and powerful" without letting it nuke unrelated software.

# Automation Layer

TidyWindow executes PowerShell 7 scripts through managed runspaces so the desktop app can stay responsive.

## Module Structure

-   `automation/modules/TidyWindow.Automation/TidyWindow.Automation.psm1` exposes shared helpers such as `Write-TidyLog` and `Assert-TidyAdmin`.
-   Scripts under `automation/scripts` should import the module with `Import-Module "$PSScriptRoot/../modules/TidyWindow.Automation/TidyWindow.Automation.psm1" -Force` to reuse helpers.

## Invocation Conventions

-   Scripts must accept named parameters and return structured objects (PSCustomObject) whenever possible.
-   Write informational output with `Write-TidyLog -Level Information -Message "..."` so logs stay consistent.
-   Throw terminating errors for unrecoverable states; the .NET invoker will capture these in the error stream.

## .NET Bridge

-   `PowerShellInvoker` reads scripts from disk and executes them asynchronously.
-   Scripts should be stored alongside source control to keep audit history and packaging simple.
-   When adding new scripts, extend the tests in `tests/TidyWindow.Core.Tests` to cover happy-path and error scenarios.

## PulseGuard Watchdog

PulseGuard is the always-on log sentinel that samples automation output, labels events, and turns them into human-friendly nudges. The name and copy intentionally lean into the "guardian" metaphor so the UI can say things like _"PulseGuard is standing watch"_ when it is active.

### Naming & UX Copy

-   **PulseGuard** — feature name shown in navigation, settings, and notifications.
-   **Standing watch / Taking a break** — friendly state copy for enabled/disabled toggles.
-   **Tap to view detailed log** — shared action label that opens the log viewer at the relevant time range.

### Event Taxonomy

| Category            | Trigger Heuristics                                                                                 | Notification Copy                                         | Surfaces                                                 |
| ------------------- | -------------------------------------------------------------------------------------------------- | --------------------------------------------------------- | -------------------------------------------------------- |
| `SuccessDigest`     | Script finishes without errors and produced actionable output.                                     | "PulseGuard: `<script>` finished. View the digest."       | System notification, Logs page digest card               |
| `InfoInsight`       | Non-blocking anomalies (skipped modules, optional calibrations).                                   | "PulseGuard spotted something worth a look."              | System notification (if user inactive), Logs page ribbon |
| `ActionRequired`    | Recoverable errors that need user input (e.g., missing PowerShell 7).                              | "Action needed: update PowerShell to keep scans running." | Blocking toast with `View log` CTA                       |
| `RestartRequired`   | Installation or configuration changes that need an app restart.                                    | "App restart required to finish installing Scoop."        | Inline prompt + toast from PulseGuard                    |
| `BlockedAutomation` | Automation aborted because prerequisites are missing (admin rights, offline, policy restrictions). | "PulseGuard blocked the task to keep things safe."        | Toast (if inactive), in-app callout                      |

### Notification Heuristics

-   Only send toasts when the window is not the active foreground app. When focused, log entries accumulate for manual review instead of interrupting the workflow.
-   Collapse duplicate events within a five-minute window into a single digest card to avoid notification spam.
-   Always attach a `View log` deep link so users can inspect the raw script output from the Logs page.
-   When an `ActionRequired` or `RestartRequired` notification fires, schedule an in-app banner so the guidance is still visible after a toast is dismissed.
-   Scripts should emit structured tags via `Write-TidyLog -Tag ActionRequired` (or matching taxonomy values) so PulseGuard can score the event without brittle string parsing.

### High-Friction Prompts

-   PulseGuard escalates when it spots legacy PowerShell runtimes or automation that needs a TidyWindow restart; prompts offer quick `View logs` and `Restart app` actions so the user can respond without hunting through menus.
-   Prompts dedupe by scenario and timestamp so the same blocker cannot spam the session.

## PathPilot Runtime Switchboard

PathPilot keeps runtime management machine-scoped. The automation lives in `automation/scripts/Get-PathPilotInventory.ps1` and is driven by `data/catalog/runtime-inventory.json`.

### Inventory Workflow

-   Run `Get-PathPilotInventory.ps1 -Export json` (default) to emit a structured snapshot containing runtime cards, PATH entries, and warnings.
-   The script expands the JSON catalog, discovers installations via glob/registry probes, records `--version` output, and indicates which executable currently wins PATH priority.
-   Markdown output is available via `-Export markdown`; PathPilot writes the file defined by `-OutputPath` or falls back to `%ProgramData%\TidyWindow\PathPilot\exports`.

### Switching & Safeguards

-   Invoke switching with either `-Switch <runtimeId> <installPath>` or explicit `-SwitchRuntimeId/-SwitchInstallPath` parameters.
-   Before touching PATH, the script validates: runtime id exists, target file resolves to the configured executable name, and the file actually exists (directories are rewritten to include the executable).
-   On every successful update PathPilot writes `%ProgramData%\TidyWindow\PathPilot\backup-<timestamp>.reg` plus a JSON operation log (`switch-<timestamp>.json`) that includes the previous PATH value for rollback.
-   Failures (missing executables, unresolved directories, backup errors) stop the operation before PATH is rewritten and return a terminating error to the .NET caller.

### App Surface

-   `PathPilotInventoryService` shells out through `PowerShellInvoker`, deserializes the JSON payload, and exposes switch/export helpers to the WPF view model.
-   The view model logs every refresh, switch, and export event via `ActivityLogService` so PulseGuard has full context.
-   UI actions always remind operators that the feature modifies HKLM + machine PATH; the dismissible warning banner on the page mirrors that safety guidance from the concept deck.


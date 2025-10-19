# Automation Layer

TidyWindow executes PowerShell 7 scripts through managed runspaces so the desktop app can stay responsive.

## Module Structure

-   `automation/modules/TidyWindow.Automation.psm1` exposes shared helpers such as `Write-TidyLog` and `Assert-TidyAdmin`.
-   Scripts under `automation/scripts` should import the module with `Import-Module "$PSScriptRoot/../modules/TidyWindow.Automation.psm1" -Force` to reuse helpers.

## Invocation Conventions

-   Scripts must accept named parameters and return structured objects (PSCustomObject) whenever possible.
-   Write informational output with `Write-TidyLog -Level Information -Message "..."` so logs stay consistent.
-   Throw terminating errors for unrecoverable states; the .NET invoker will capture these in the error stream.

## .NET Bridge

-   `PowerShellInvoker` reads scripts from disk and executes them asynchronously.
-   Scripts should be stored alongside source control to keep audit history and packaging simple.
-   When adding new scripts, extend the tests in `tests/TidyWindow.Core.Tests` to cover happy-path and error scenarios.

# Runtime Updates Module

The runtime updates surface keeps an eye on foundational runtimes that TidyWindow and common automation flows rely on. It blends static metadata (what do we care about?) with live detection (what is currently installed on this machine?).

## What we track

-   **.NET Desktop Runtime** (`Microsoft.WindowsDesktop.App`) – powers WPF/WinForms applications and is required for TidyWindow itself.
-   **PowerShell 7** – preferred automation host with the latest security fixes and cmdlet improvements.
-   **Node.js LTS** – widely used by JavaScript tooling and cross-platform CLIs leveraged during environment bootstrap.

The catalog is defined in `TidyWindow.Core/Updates/RuntimeCatalogService.cs`. Each entry includes an identifier, vendor, description, and canonical download URL.

## How detection works

1. The app invokes `automation/scripts/check-runtime-updates.ps1` through the shared `PowerShellInvoker`.
2. The script inspects the local machine:
    - `.NET Desktop Runtime` uses `dotnet --list-runtimes` and selects the highest `Microsoft.WindowsDesktop.App` version.
    - `PowerShell 7` reports the current `$PSVersionTable.PSVersion`.
    - `Node.js LTS` calls `node --version` when available.
3. Each result is annotated with a status (`UpToDate`, `UpdateAvailable`, or `NotInstalled`) and the canonical download link defined in the catalog.
4. The `RuntimeCatalogService` merges the script output with catalog details and exposes a typed model to the WPF layer.

## Using the page

Open **Runtime updates** from the left navigation. The page presents:

-   A summary banner showing counts of available updates and missing runtimes.
-   A grid with runtime details, detected/expected versions, and a quick link to the official download page.
-   Color-coded status badges for readability.

Use **Refresh now** to re-run the detection script. The status bar reflects the outcome so you can keep the shell minimized while checks run in the background.

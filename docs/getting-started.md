# Getting Started

## Prerequisites

-   Windows 10 or later
-   .NET SDK 8.0+
-   PowerShell 7 (`pwsh`)

### Install Prerequisites with winget

```powershell
winget install --id Git.Git -e --accept-package-agreements --accept-source-agreements
winget install --id Microsoft.DotNet.SDK.8 -e --accept-package-agreements --accept-source-agreements
winget install --id Microsoft.PowerShell -e --accept-package-agreements --accept-source-agreements
```

## Setup

1. Clone the repository.
2. Install optional package managers you plan to manage (winget, Chocolatey, Scoop).
3. Restore dependencies:
    ```powershell
    dotnet restore src/TidyWindow.sln
    ```
4. Build the solution:
    ```powershell
    dotnet build src/TidyWindow.sln
    ```
5. Run the app shell:
    ```powershell
    dotnet run --project src/TidyWindow.App/TidyWindow.App.csproj
    ```

## Recommended Checks

-   Run `dotnet format` before committing.
-   Execute the CI workflow locally with `dotnet test` once tests exist.

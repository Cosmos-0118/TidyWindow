# TidyWindow Tech Stack and Rationale

## 1. Product Overview

TidyWindow keeps Windows machines tidy by combining a desktop app with curated maintenance automations. The WPF application gives users a friendly dashboard, while PowerShell scripts and catalog data files drive repair, cleanup, and package-management tasks behind the scenes.

## 2. Application Layers

-   **Presentation (WPF + MVVM)**

    -   `TidyWindow.App` uses Windows Presentation Foundation running on .NET 8.
    -   CommunityToolkit.Mvvm helps us express the MVVM pattern without manual boilerplate.
    -   The desktop app binds UI controls to view models, so state changes flow cleanly and we keep logic testable.

-   **Core Services (.NET class library)**

    -   `TidyWindow.Core` holds the business logic that decides when and how to call maintenance routines, read catalog metadata, and report progress back to the UI.
    -   Targeting .NET 8 gives us modern language features, long-term support, and high performance on Windows.

-   **Automation Assets (PowerShell)**

    -   The `automation/` folder contains PowerShell modules and scripts that actually perform maintenance operations: disk checks, network resets, RAM purges, app repairs, and more.
    -   Scripts rely on Windows-native utilities, so PowerShell is the natural choice for privileged system work.
    -   The `TidyWindow.Automation` module provides helper functions and logging so that every script has a consistent experience.

-   **Catalog Data (YAML)**
    -   YAML files in `data/catalog/` describe available maintenance bundles, package groups, and installable tools.
    -   Keeping metadata outside the binaries means we can update or extend catalog entries without shipping new code.

## 3. Key Technology Choices

| Component            | Technology                                       | Why we use it                                                                                     |
| -------------------- | ------------------------------------------------ | ------------------------------------------------------------------------------------------------- |
| UI framework         | WPF (.NET 8)                                     | Native Windows UI, rich styling, MVVM friendly, easy binding to view models.                      |
| MVVM utilities       | CommunityToolkit.Mvvm                            | Reduces boilerplate for `INotifyPropertyChanged`, commands, and dependency injection.             |
| Logging              | Serilog                                          | Structured logging makes diagnostics easy and integrates with sinks if we extend telemetry later. |
| Dependency Injection | Microsoft.Extensions.DependencyInjection/Hosting | Provides a standard DI container and host builder so services stay loosely coupled.               |
| Automation language  | PowerShell 5+                                    | Built into Windows, can run admin scripts, and integrates easily with system utilities.           |
| Packaging            | Inno Setup + CI pipelines                        | Produces a familiar Windows installer and automates builds with GitHub Actions.                   |
| Metadata             | YAML                                             | Human-readable, supports comments, and is already common in infrastructure ecosystems.            |

## 4. How Things Work Together

1. **User launches TidyWindow.** The WPF shell spins up, the DI container wires view models and services, and the dashboard loads catalog data.
2. **User selects a maintenance task.** The view model resolves the right automation entry from the catalog and hands it to a service layer.
3. **Core service orchestrates execution.** Services either call internal logic or spawn PowerShell scripts under the hood. They log progress through Serilog so the UI can surface status updates.
4. **Automation script runs.** Each script leverages helper functions from `TidyWindow.Automation` to check prerequisites, demand admin rights when required, and execute Windows utilities safely.
5. **Results flow back to the UI.** The service layer captures results, updates view models, and the UI refreshes bindings to show success, warnings, or errors.

### Simplified call sequence

```
User Action → ViewModel command → Core service → PowerShell runner → Windows tools
                                ↓                       ↑
                            Serilog logs           Script output
                                ↓                       |
                         UI status updates ←—— result formatting
```

This flow keeps UI code thin: the view model issues commands, the service decides which script or internal routine to execute, and automation layers deal with the low-level Windows work.

## 5. Deployment Flow

-   GitHub Actions CI restores, builds, and tests the solution on Windows.
-   Automated checks ensure the publish directory contains every script and catalog file before packaging.
-   Release builds produce a self-contained publish folder, zip artifacts, and an Inno Setup installer.
-   The installer bundles the executable plus the automation/data assets, so users receive a ready-to-run desktop experience.

## 6. Why This Stack Works For Us

-   **Windows focus:** All critical components (WPF, PowerShell, Inno Setup) are built around delivering the best Windows experience.
-   **Separation of concerns:** UI, business logic, scripts, and data live in distinct projects or folders, making them independently maintainable.
-   **Extensibility:** Adding a new maintenance routine usually means updating catalog YAML and dropping in a new PowerShell script—no code changes needed unless the UI needs new features.
-   **Tooling ecosystem:** .NET 8 offers long-term support, strong tooling (Visual Studio, VS Code, dotnet CLI), and community libraries.

## 7. Why We Chose C# and .NET

-   **Native Windows integration:** C# on .NET gives first-class access to Windows APIs, WPF, and interop layers we need for system maintenance.
-   **Performance with safety:** Modern C# provides high-level abstractions, async flows, and pattern matching while still compiling to efficient IL. We can interop with PowerShell or unmanaged code when needed, but most logic stays memory-safe.
-   **Rich ecosystem:** NuGet delivers proven libraries (CommunityToolkit.Mvvm, Serilog, dependency-injection packages) so we focus on product features instead of infrastructure plumbing.
-   **Testability:** The language and tooling make it easy to write unit or integration tests against services in `TidyWindow.Core`, keeping regressions low.
-   **Team familiarity:** Contributors versed in Windows desktop development typically know C#, reducing onboarding time.

## 8. Looking Ahead

-   We can introduce telemetry sinks or diagnostics dashboards via Serilog without changing core logic.
-   Catalog-driven design allows us to publish new bundles as separate updates.
-   The automation module can grow with additional utilities or cross-cutting guards (for example, snapshotting registry keys before edits).

This stack keeps TidyWindow approachable for new contributors while giving power users the depth of Windows-native automation.


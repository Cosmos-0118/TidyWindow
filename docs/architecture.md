# Architecture Overview

TidyWindow pairs a WPF front end with PowerShell-run automation scripts orchestrated by .NET background services. The app keeps package managers and maintenance tasks accessible through a single dashboard while remaining lightweight for personal use.

Key components:

- WPF MVVM client (CommunityToolkit.Mvvm) for navigation, dialogs, and progress reporting.
- Core .NET services for scheduling, logging, and data access via SQLite.
- PowerShell 7 scripts invoked through runspaces for admin-friendly system operations.

# Startup Manager and Delayed Launch Feature for Windows Maintenance App

## Concept Overview

This feature aims to enhance Windows startup management by providing a comprehensive view of all startup entries, including those hidden from the default Windows Task Manager. It also introduces a safe and user-friendly way to delay non-critical startup apps to reduce system lag during boot or login.

Key objectives:
- Enumerate all startup apps from multiple OS locations for full visibility.
- Categorize startup entries by safety and impact.
- Allow safe enabling, disabling, and delayed launching of user apps.
- Protect core Windows and security services from accidental modification.
- Implement a stable, native delay mechanism using Windows Task Scheduler.

---

## Startup Sources to Monitor

1. **Registry Run Keys and RunOnce (User and Machine):**  
   - `HKLM\Software\Microsoft\Windows\CurrentVersion\Run`  
   - `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`  
   - Corresponding `RunOnce` keys and Policies keys.

2. **Startup Folders:**  
   - Per-user Startup folder (`%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup`)  
   - All Users common Startup folder (`C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Startup`)

3. **Services:**  
   - Auto-start services (`Start` = 2) in registry under `HKLM\System\CurrentControlSet\Services`

4. **Scheduled Tasks:**  
   - Tasks triggered at user logon or system startup via Task Scheduler.

---

## Design Highlights

- **Display:** List startup items with attributes—source, app name, executable path, publisher (if signed), and risk category.  
- **Control:** Enable or disable startup entries safely; delay launch of non-critical apps by specifying a delay interval.  
- **Delay Implementation:** For delayed apps, disable the original startup entry and create or update an equivalent Scheduled Task with a delay trigger.  
- **Safety Measures:**  
  - Detect and exclude core Windows processes and critical security software from modification or delay.  
  - Use digital signature validation and known system path checks to identify protected system entries.  
- **User Experience:**  
  - Clear warnings and confirmations when disabling or delaying startup apps.  
  - Predefined delay options (e.g., 30s, 60s, 120s) with an optional custom delay.  
  - Avoid overcomplication; provide simple toggles or dropdown selections.

---

## Recommended Implementation Approach (C# with Optional PowerShell Invocation)

1. **Enumerate Startup Entries:**  
   - Use C# Registry APIs (`Microsoft.Win32.RegistryKey`) to read Run and RunOnce keys.  
   - Enumerate Startup folders using `System.IO.Directory` APIs.  
   - Query services with `System.ServiceProcess.ServiceController`.  
   - Access scheduled tasks via Task Scheduler COM interface or through PowerShell (e.g., `schtasks` command).  

2. **Digital Signature & Path Validation:**  
   - Use `System.Security.Cryptography.X509Certificates` to check EXE signatures for Microsoft or trusted publishers.  
   - Validate paths against system directories to prevent accidental modifications on core OS files.

3. **Enable/Disable Startup Entries:**  
   - For registry and folder entries: Add or remove keys/files accordingly.  
   - For services: Change StartupType using `ServiceController` or P/Invoke to native API.  
   - For scheduled tasks: Create, delete, or disable tasks using COM API or PowerShell.

4. **Apply Delayed Launch via Scheduled Tasks:**  
   - When delay is requested, programmatically disable original auto-start entry (registry or folder).  
   - Create a new Scheduled Task:  
     - Action: Start the executable file.  
     - Trigger: At logon with delay interval specified.  
   - Use Windows Task Scheduler COM API (`Microsoft.Win32.TaskScheduler` NuGet package recommended) or PowerShell cmdlets (`New-ScheduledTask`, `Register-ScheduledTask`) for interaction.

5. **UI Integration (WPF with MVVM):**  
   - Bind startup entries list to an observable collection with editing capabilities.  
   - Commands or buttons for Enable/Disable/Delay actions invoking backend logic.  
   - Show status and error messages clearly.

---

## Why This Approach?

- Using **pure C# with Windows APIs and COM** gives strong control, better performance, and avoids external dependencies.  
- Calling PowerShell commands from C# is simpler for complex scheduled tasks operations if preferred, but may add a small overhead.  
- Leveraging the Task Scheduler COM API (`Microsoft.Win32.TaskScheduler` library) strikes an ideal balance: native, powerful, and accessible from managed code.  
- The delay technique using Scheduled Tasks is clean, minimizes risk, and integrates naturally with Windows’ native startup process without hacks or risky process manipulations.

---

## Further Notes

- Always run with administrator privileges to modify registry, services, and scheduled tasks reliably.  
- Maintain a backup or restore point creation step in your app before applying changes if possible, for safety.  
- Test on multiple Windows versions (Windows 10/11) to ensure compatibility, especially for scheduled task triggers.

---

This polished design provides a modern, effective, and user-friendly way to extend Windows startup management inside your maintenance app while maintaining system stability and security.

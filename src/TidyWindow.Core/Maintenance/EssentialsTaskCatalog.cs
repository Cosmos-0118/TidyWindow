using System;
using System.Collections.Immutable;

namespace TidyWindow.Core.Maintenance;

/// <summary>
/// Provides the curated list of essentials automation tasks exposed in the UI.
/// </summary>
public sealed class EssentialsTaskCatalog
{
    private readonly ImmutableArray<EssentialsTaskDefinition> _tasks;

    public EssentialsTaskCatalog()
    {
        _tasks = ImmutableArray.Create(
            new EssentialsTaskDefinition(
                "network-reset",
                "Network reset & cache flush",
                "Network",
                "Flushes DNS/ARP/TCP caches and restarts adapters for fresh connectivity.",
                ImmutableArray.Create(
                    "Flushes DNS, ARP, and Winsock stacks",
                    "Optionally restarts adapters and renews DHCP"),
                "automation/essentials/network-reset-and-cache-flush.ps1",
                DurationHint: "Approx. 3-6 minutes (adapter refresh adds a couple more)",
                DetailedDescription: "Flushes DNS, ARP, and TCP caches, resets Winsock and IP stacks, can restart adapters, renew DHCP leases, and tracks which actions succeeded with reboot guidance.",
                DocumentationLink: "docs/essentials-overview.md#1-network-reset--cache-flush",
                Options: ImmutableArray.Create(
                    new EssentialsTaskOptionDefinition(
                        id: "adapter-refresh",
                        label: "Restart adapters after resets",
                        parameterName: "IncludeAdapterRefresh",
                        defaultValue: false,
                        description: "Disables and re-enables active physical adapters."),
                    new EssentialsTaskOptionDefinition(
                        id: "dhcp-renew",
                        label: "Force DHCP release/renew",
                        parameterName: "IncludeDhcpRenew",
                        defaultValue: false,
                        description: "Runs ipconfig /release and /renew."),
                    new EssentialsTaskOptionDefinition(
                        id: "winsock-reset",
                        label: "Reset Winsock catalog",
                        parameterName: "SkipWinsockReset",
                        mode: EssentialsTaskOptionMode.EmitWhenFalse,
                        description: "Skips the wsreset-style Winsock reset when unchecked."),
                    new EssentialsTaskOptionDefinition(
                        id: "ip-reset",
                        label: "Reset IP stack",
                        parameterName: "SkipIpReset",
                        mode: EssentialsTaskOptionMode.EmitWhenFalse,
                        description: "Skips netsh int ip reset when unchecked."))),

            new EssentialsTaskDefinition(
                "system-health",
                "System health scanner",
                "Integrity",
                "Runs SFC and DISM passes to repair core Windows components.",
                ImmutableArray.Create(
                    "Runs SFC /scannow and DISM health checks",
                    "Optional component cleanup to reclaim space"),
                "automation/essentials/system-health-scanner.ps1",
                DurationHint: "Approx. 30-60 minutes (SFC/DISM phases vary by corruption)",
                DetailedDescription: "Automates a full SFC scan followed by DISM CheckHealth, ScanHealth, and RestoreHealth repairs with optional StartComponentCleanup, component store analysis, restore point creation, and transcript logging.",
                DocumentationLink: "docs/essentials-overview.md#2-system-health-scanner-sfc--dism",
                Options: ImmutableArray.Create(
                    new EssentialsTaskOptionDefinition(
                        id: "run-sfc",
                        label: "Run SFC /scannow",
                        parameterName: "SkipSfc",
                        mode: EssentialsTaskOptionMode.EmitWhenFalse),
                    new EssentialsTaskOptionDefinition(
                        id: "run-dism",
                        label: "Run DISM Check/Scan",
                        parameterName: "SkipDism",
                        mode: EssentialsTaskOptionMode.EmitWhenFalse),
                    new EssentialsTaskOptionDefinition(
                        id: "run-restorehealth",
                        label: "Run DISM RestoreHealth",
                        parameterName: "SkipRestoreHealth",
                        mode: EssentialsTaskOptionMode.EmitWhenFalse),
                    new EssentialsTaskOptionDefinition(
                        id: "component-cleanup",
                        label: "Run component cleanup",
                        parameterName: "ComponentCleanup",
                        defaultValue: false),
                    new EssentialsTaskOptionDefinition(
                        id: "analyze-store",
                        label: "Analyze component store",
                        parameterName: "AnalyzeComponentStore",
                        defaultValue: false),
                    new EssentialsTaskOptionDefinition(
                        id: "post-restore-point",
                        label: "Create restore point after repairs",
                        parameterName: "CreateSystemRestorePoint",
                        defaultValue: false))),

            new EssentialsTaskDefinition(
                "disk-check",
                "Disk checkup & repair",
                "Storage",
                "Schedules CHKDSK scans, repairs volumes, and collects SMART data.",
                ImmutableArray.Create(
                    "Detects dirty volumes and queues boot-time repairs",
                    "Captures SMART telemetry for context"),
                "automation/essentials/disk-checkup-and-fix.ps1",
                DurationHint: "Approx. 8-18 minutes to scan; offline repairs after reboot can add 30+ minutes",
                DetailedDescription: "Identifies the target volume, runs CHKDSK in scan or repair modes, schedules offline repairs when the drive is busy, and aggregates SMART telemetry and findings into a concise summary.",
                DocumentationLink: "docs/essentials-overview.md#3-disk-checkup-and-fix-chkdsk--smart",
                Options: ImmutableArray.Create(
                    new EssentialsTaskOptionDefinition(
                        id: "perform-repair",
                        label: "Attempt repairs (/f)",
                        parameterName: "PerformRepair",
                        defaultValue: false,
                        description: "Adds /f to repair logical errors and may require reboot."),
                    new EssentialsTaskOptionDefinition(
                        id: "surface-scan",
                        label: "Include surface scan (/r)",
                        parameterName: "IncludeSurfaceScan",
                        defaultValue: false,
                        description: "Adds /r to scan for bad sectors (implies /f and can take much longer)."),
                    new EssentialsTaskOptionDefinition(
                        id: "schedule-if-busy",
                        label: "Schedule repair if volume is busy",
                        parameterName: "ScheduleIfBusy",
                        defaultValue: false),
                    new EssentialsTaskOptionDefinition(
                        id: "collect-smart",
                        label: "Collect SMART telemetry",
                        parameterName: "SkipSmart",
                        mode: EssentialsTaskOptionMode.EmitWhenFalse))),

            new EssentialsTaskDefinition(
                "performance-storage-repair",
                "Performance & storage repair",
                "Performance",
                "Disables SysMain, tunes pagefile policy, purges caches, trims event logs, and resets power plans.",
                ImmutableArray.Create(
                    "Disables SysMain to reduce disk thrash",
                    "Sets pagefile policy, clears temp/prefetch, trims event logs, resets power schemes"),
                "automation/essentials/performance-and-storage-repair.ps1",
                DurationHint: "Approx. 6-12 minutes (log trims may add a minute)",
                DetailedDescription: "Disables SysMain, applies automatic or manual pagefile sizing, clears TEMP and Prefetch caches, trims System/Application/Setup event logs to 32 MB, and restores power schemes with optional High Performance activation.",
                DocumentationLink: "essentialsaddition.md#performance-and-storage-6-issues",
                Options: ImmutableArray.Create(
                    new EssentialsTaskOptionDefinition(
                        id: "disable-sysmain",
                        label: "Disable SysMain service",
                        parameterName: "SkipSysMainDisable",
                        mode: EssentialsTaskOptionMode.EmitWhenFalse,
                        description: "Stops SysMain and sets startup type to Disabled."),
                    new EssentialsTaskOptionDefinition(
                        id: "automatic-pagefile",
                        label: "Enable automatic pagefile management",
                        parameterName: "SkipPagefileTune",
                        mode: EssentialsTaskOptionMode.EmitWhenFalse,
                        description: "Sets AutomaticManagedPagefile to true unless you prefer manual sizing."),
                    new EssentialsTaskOptionDefinition(
                        id: "manual-pagefile",
                        label: "Use 1-4 GB manual pagefile",
                        parameterName: "UseManualPagefileSizing",
                        defaultValue: false,
                        description: "Creates/updates C:\\pagefile.sys with 1024-4096 MB sizing and disables automatic management."),
                    new EssentialsTaskOptionDefinition(
                        id: "clear-caches",
                        label: "Clear temp and Prefetch caches",
                        parameterName: "SkipCacheCleanup",
                        mode: EssentialsTaskOptionMode.EmitWhenFalse),
                    new EssentialsTaskOptionDefinition(
                        id: "trim-event-logs",
                        label: "Trim System/Application/Setup logs",
                        parameterName: "SkipEventLogTrim",
                        mode: EssentialsTaskOptionMode.EmitWhenFalse),
                    new EssentialsTaskOptionDefinition(
                        id: "reset-power-plans",
                        label: "Reset power plans",
                        parameterName: "SkipPowerPlanReset",
                        mode: EssentialsTaskOptionMode.EmitWhenFalse),
                    new EssentialsTaskOptionDefinition(
                        id: "enable-high-performance",
                        label: "Enable High Performance plan",
                        parameterName: "ActivateHighPerformancePlan",
                        defaultValue: false,
                        description: "After resetting schemes, activate scheme_min for maximum performance."))),

            new EssentialsTaskDefinition(
                "audio-peripheral-repair",
                "Audio & peripheral repair",
                "Devices",
                "Restarts audio stack, rescans endpoints, resets Bluetooth AVCTP, refreshes USB hubs, and re-enables mic/camera devices.",
                ImmutableArray.Create(
                    "Restarts AudioSrv/AudioEndpointBuilder",
                    "Runs pnputil rescans plus Bluetooth/USB device refresh"),
                "automation/essentials/audio-and-peripheral-repair.ps1",
                DurationHint: "Approx. 4-10 minutes (rescans can vary)",
                DetailedDescription: "Restarts core audio services, enumerates and rescans audio endpoints, restarts the Bluetooth AVCTP stack, refreshes USB hub devices, and re-enables disabled audio endpoints or camera services with PnP rescans for recovery.",
                DocumentationLink: "essentialsaddition.md#audio-and-peripherals-6-issues",
                Options: ImmutableArray.Create(
                    new EssentialsTaskOptionDefinition(
                        id: "restart-audio-stack",
                        label: "Restart audio services",
                        parameterName: "SkipAudioStackRestart",
                        mode: EssentialsTaskOptionMode.EmitWhenFalse,
                        description: "Restarts AudioSrv and AudioEndpointBuilder."),
                    new EssentialsTaskOptionDefinition(
                        id: "rescan-endpoints",
                        label: "Rescan audio endpoints",
                        parameterName: "SkipEndpointRescan",
                        mode: EssentialsTaskOptionMode.EmitWhenFalse,
                        description: "Runs pnputil enum/scan for AudioEndpoint devices."),
                    new EssentialsTaskOptionDefinition(
                        id: "reset-bluetooth-avctp",
                        label: "Reset Bluetooth AVCTP service",
                        parameterName: "SkipBluetoothReset",
                        mode: EssentialsTaskOptionMode.EmitWhenFalse),
                    new EssentialsTaskOptionDefinition(
                        id: "reset-usb-hubs",
                        label: "Reset USB hubs and rescan",
                        parameterName: "SkipUsbHubReset",
                        mode: EssentialsTaskOptionMode.EmitWhenFalse),
                    new EssentialsTaskOptionDefinition(
                        id: "enable-microphones",
                        label: "Enable audio endpoints",
                        parameterName: "SkipMicEnable",
                        mode: EssentialsTaskOptionMode.EmitWhenFalse,
                        description: "Enables non-OK AudioEndpoint devices."),
                    new EssentialsTaskOptionDefinition(
                        id: "reset-camera",
                        label: "Reset camera service and rescan",
                        parameterName: "SkipCameraReset",
                        mode: EssentialsTaskOptionMode.EmitWhenFalse))),

            new EssentialsTaskDefinition(
                "shell-ui-repair",
                "Shell & UI repair",
                "Shell",
                "Re-registers ShellExperienceHost/StartMenu, resets search indexer, recycles explorer, refreshes tray, and re-registers Settings.",
                ImmutableArray.Create(
                    "Re-registers ShellExperienceHost and StartMenuExperienceHost",
                    "Resets search indexer and recycles explorer"),
                "automation/essentials/shell-and-ui-repair.ps1",
                DurationHint: "Approx. 6-15 minutes (AppX re-registers add a few minutes)",
                DetailedDescription: "Repairs common shell failures by re-registering ShellExperienceHost and StartMenuExperienceHost for all users, restarting and resetting the Windows Search indexer, recycling explorer, re-registering the Settings app, and refreshing the tray by restarting ShellExperienceHost.",
                DocumentationLink: "essentialsaddition.md#shell-and-ui-issues-6-issues",
                Options: ImmutableArray.Create(
                    new EssentialsTaskOptionDefinition(
                        id: "reregister-shell",
                        label: "Re-register ShellExperienceHost",
                        parameterName: "SkipShellReregister",
                        mode: EssentialsTaskOptionMode.EmitWhenFalse),
                    new EssentialsTaskOptionDefinition(
                        id: "reregister-startmenu",
                        label: "Re-register StartMenuExperienceHost",
                        parameterName: "SkipStartMenuReregister",
                        mode: EssentialsTaskOptionMode.EmitWhenFalse),
                    new EssentialsTaskOptionDefinition(
                        id: "reset-search",
                        label: "Reset search indexer",
                        parameterName: "SkipSearchReset",
                        mode: EssentialsTaskOptionMode.EmitWhenFalse),
                    new EssentialsTaskOptionDefinition(
                        id: "recycle-explorer",
                        label: "Recycle explorer.exe",
                        parameterName: "SkipExplorerRecycle",
                        mode: EssentialsTaskOptionMode.EmitWhenFalse),
                    new EssentialsTaskOptionDefinition(
                        id: "reregister-settings",
                        label: "Re-register Settings app",
                        parameterName: "SkipSettingsReregister",
                        mode: EssentialsTaskOptionMode.EmitWhenFalse),
                    new EssentialsTaskOptionDefinition(
                        id: "refresh-tray",
                        label: "Refresh tray (ShellExperienceHost restart)",
                        parameterName: "SkipTrayRefresh",
                        mode: EssentialsTaskOptionMode.EmitWhenFalse))),

            new EssentialsTaskDefinition(
                "security-credential-repair",
                "Security & credentials repair",
                "Security",
                "Resets Windows Firewall, re-registers Windows Security, rebuilds the credential vault, and enforces UAC prompts.",
                ImmutableArray.Create(
                    "Resets firewall defaults and re-enables profiles",
                    "Restarts SecurityHealthService, rebuilds credential vault, enforces EnableLUA"),
                "automation/essentials/security-and-credential-repair.ps1",
                DurationHint: "Approx. 5-12 minutes (AppX re-register may add a few minutes)",
                DetailedDescription: "Resets Windows Firewall to defaults with all profiles enabled, restarts SecurityHealthService and re-registers the Windows Security (SecHealthUI) app for all users, backs up and recreates the credential vault after restarting VaultSvc/Schedule, and enforces EnableLUA=1 to restore UAC prompts.",
                DocumentationLink: "essentialsaddition.md#security-and-services-4-issues",
                Options: ImmutableArray.Create(
                    new EssentialsTaskOptionDefinition(
                        id: "reset-firewall",
                        label: "Reset Windows Firewall",
                        parameterName: "SkipFirewallReset",
                        mode: EssentialsTaskOptionMode.EmitWhenFalse),
                    new EssentialsTaskOptionDefinition(
                        id: "reregister-security-ui",
                        label: "Re-register Windows Security app",
                        parameterName: "SkipSecurityUiReregister",
                        mode: EssentialsTaskOptionMode.EmitWhenFalse),
                    new EssentialsTaskOptionDefinition(
                        id: "rebuild-credential-vault",
                        label: "Rebuild credential vault",
                        parameterName: "SkipCredentialVaultRebuild",
                        mode: EssentialsTaskOptionMode.EmitWhenFalse,
                        description: "Backs up %LOCALAPPDATA%\\Microsoft\\Credentials and recreates it after restarting vault services."),
                    new EssentialsTaskOptionDefinition(
                        id: "enforce-enablelua",
                        label: "Enforce UAC (EnableLUA=1)",
                        parameterName: "SkipEnableLuaEnforcement",
                        mode: EssentialsTaskOptionMode.EmitWhenFalse,
                        description: "Sets EnableLUA to 1; logoff or reboot may be required."))),

            new EssentialsTaskDefinition(
                "activation-licensing-repair",
                "Activation & licensing repair",
                "Licensing",
                "Re-registers activation DLLs, refreshes Software Protection, attempts online activation, and optionally runs slmgr /rearm.",
                ImmutableArray.Create(
                    "Re-registers activation/licensing DLLs",
                    "Restarts Software Protection and runs slmgr /ato"),
                "automation/essentials/activation-and-licensing-repair.ps1",
                DurationHint: "Approx. 4-10 minutes (DLL re-register + sppsvc refresh + activation)",
                DetailedDescription: "Re-registers slc/slwga/spp* DLLs, refreshes the Software Protection service, attempts slmgr /ato for activation, and can run slmgr /rearm (opt-in) to rebuild licensing state with transcript logging.",
                DocumentationLink: "essentialsaddition.md",
                Options: ImmutableArray.Create(
                    new EssentialsTaskOptionDefinition(
                        id: "dll-reregister",
                        label: "Re-register activation DLLs",
                        parameterName: "SkipDllReregister",
                        mode: EssentialsTaskOptionMode.EmitWhenFalse),
                    new EssentialsTaskOptionDefinition(
                        id: "refresh-sppsvc",
                        label: "Refresh Software Protection service",
                        parameterName: "SkipProtectionServiceRefresh",
                        mode: EssentialsTaskOptionMode.EmitWhenFalse),
                    new EssentialsTaskOptionDefinition(
                        id: "activation-attempt",
                        label: "Attempt online activation",
                        parameterName: "SkipActivationAttempt",
                        mode: EssentialsTaskOptionMode.EmitWhenFalse,
                        description: "Runs slmgr /ato via cscript."),
                    new EssentialsTaskOptionDefinition(
                        id: "attempt-rearm",
                        label: "Attempt license rearm (/rearm)",
                        parameterName: "AttemptRearm",
                        defaultValue: false,
                        description: "Consumes limited rearm count; enable only when needed."))),

            new EssentialsTaskDefinition(
                "tpm-bitlocker-secureboot-repair",
                "TPM, BitLocker & Secure Boot repair",
                "Security",
                "Cycles BitLocker protectors, requests TPM clear, surfaces Secure Boot guidance, and refreshes device encryption prerequisites.",
                ImmutableArray.Create(
                    "Suspends/resumes BitLocker protectors on the system volume",
                    "Requests TPM clear and logs Secure Boot/device encryption guidance"),
                "automation/essentials/tpm-bitlocker-secureboot-repair.ps1",
                DurationHint: "Approx. 5-12 minutes (TPM clear requires reboot/firmware confirmation)",
                DetailedDescription: "Runs a security stack refresh by suspending and resuming BitLocker protectors on the OS volume, requesting a TPM clear (owner confirmation required) with status logging, reporting Secure Boot state plus reset guidance, and re-enabling device encryption registry/service prerequisites (BDESVC, KeyIso, DeviceInstall, PlugPlay).",
                DocumentationLink: "essentialsaddition.md#tpm-bitlocker-secure-boot-4-issues",
                Options: ImmutableArray.Create(
                    new EssentialsTaskOptionDefinition(
                        id: "bitlocker-cycle",
                        label: "Suspend/resume BitLocker protectors",
                        parameterName: "SkipBitLockerCycle",
                        mode: EssentialsTaskOptionMode.EmitWhenFalse,
                        description: "Suspends BitLocker on the system volume for one reboot then resumes to refresh sealing."),
                    new EssentialsTaskOptionDefinition(
                        id: "tpm-clear",
                        label: "Request TPM clear",
                        parameterName: "SkipTpmClear",
                        mode: EssentialsTaskOptionMode.EmitWhenFalse,
                        description: "Runs Clear-Tpm; requires owner confirmation/firmware approval and may trigger reboot."),
                    new EssentialsTaskOptionDefinition(
                        id: "secureboot-guidance",
                        label: "Output Secure Boot guidance",
                        parameterName: "SkipSecureBootGuidance",
                        mode: EssentialsTaskOptionMode.EmitWhenFalse,
                        description: "Checks Secure Boot state and logs firmware reset guidance (no reboot issued)."),
                    new EssentialsTaskOptionDefinition(
                        id: "device-encryption-prereqs",
                        label: "Refresh device encryption prerequisites",
                        parameterName: "SkipDeviceEncryptionPrereqs",
                        mode: EssentialsTaskOptionMode.EmitWhenFalse,
                        description: "Enables Device Encryption and BitLocker registry keys and prepares required services."),
                    new EssentialsTaskOptionDefinition(
                        id: "show-tpm-status",
                        label: "Show TPM status (verbose)",
                        parameterName: "ShowTpmStatus",
                        defaultValue: false,
                        description: "Outputs Get-Tpm details (ready state, lockout, manufacturer, PCR banks with SHA1 warning)."),
                    new EssentialsTaskOptionDefinition(
                        id: "show-bitlocker-status",
                        label: "Show BitLocker status",
                        parameterName: "ShowBitLockerStatus",
                        defaultValue: false,
                        description: "Runs manage-bde -status on system drive and summarizes protection, conversion, and key protectors."))),

            new EssentialsTaskDefinition(
                "powershell-environment-repair",
                "PowerShell environment repair",
                "Shell",
                "Sets execution policy to RemoteSigned, resets user profiles, and enables PSRemoting/WinRM with service readiness checks.",
                ImmutableArray.Create(
                    "Sets execution policy to RemoteSigned at LocalMachine scope",
                    "Resets user PowerShell profiles and enables PSRemoting"),
                "automation/essentials/powershell-environment-repair.ps1",
                DurationHint: "Approx. 3-8 minutes (Enable-PSRemoting may restart WinRM)",
                DetailedDescription: "Repairs a broken PowerShell environment by enforcing RemoteSigned at LocalMachine scope, backing up and recreating current user profiles (all hosts and current host), and enabling PSRemoting with WinRM service startup/validation plus firewall exception setup.",
                DocumentationLink: "essentialsaddition.md#powershell-environment-3-issues",
                Options: ImmutableArray.Create(
                    new EssentialsTaskOptionDefinition(
                        id: "set-execution-policy",
                        label: "Set execution policy (RemoteSigned)",
                        parameterName: "SkipExecutionPolicy",
                        mode: EssentialsTaskOptionMode.EmitWhenFalse,
                        description: "Sets LocalMachine execution policy to RemoteSigned with -Force."),
                    new EssentialsTaskOptionDefinition(
                        id: "reset-profiles",
                        label: "Reset user profiles",
                        parameterName: "SkipProfileReset",
                        mode: EssentialsTaskOptionMode.EmitWhenFalse,
                        description: "Backs up and recreates CurrentUser profiles (all hosts and current host)."),
                    new EssentialsTaskOptionDefinition(
                        id: "enable-remoting",
                        label: "Enable PSRemoting/WinRM",
                        parameterName: "SkipRemotingEnable",
                        mode: EssentialsTaskOptionMode.EmitWhenFalse,
                        description: "Runs Enable-PSRemoting -Force/-SkipNetworkProfileCheck and ensures WinRM service is running."),
                    new EssentialsTaskOptionDefinition(
                        id: "validate-psmodulepath",
                        label: "Validate PSModulePath entries",
                        parameterName: "RepairPsModulePath",
                        defaultValue: false,
                        description: "Removes missing PSModulePath entries and deduplicates remaining paths."),
                    new EssentialsTaskOptionDefinition(
                        id: "repair-system-profiles",
                        label: "Repair system profiles (PSHOME)",
                        parameterName: "RepairSystemProfiles",
                        defaultValue: false,
                        description: "Backs up unreadable $PSHOME profile scripts and recreates safe stubs."),
                    new EssentialsTaskOptionDefinition(
                        id: "clear-runspace-cache",
                        label: "Clear runspace caches",
                        parameterName: "ClearRunspaceCaches",
                        defaultValue: false,
                        description: "Removes LocalAppData PowerShell Runspaces/RunspaceConfiguration caches."),
                    new EssentialsTaskOptionDefinition(
                        id: "reset-implicit-remoting",
                        label: "Reset implicit remoting cache",
                        parameterName: "ResetImplicitRemotingCache",
                        defaultValue: false,
                        description: "Clears TransportConnectionCache/RemoteSessions folders for remoting cache reset."),
                    new EssentialsTaskOptionDefinition(
                        id: "repair-wsman-provider",
                        label: "Repair WSMan provider",
                        parameterName: "RepairWsmanProvider",
                        defaultValue: false,
                        description: "Attempts WSMan provider re-import, winrm quickconfig, and validation against localhost."))),

            new EssentialsTaskDefinition(
                "profile-logon-repair",
                "Profile & logon repair",
                "Accounts",
                "Audits startup entries, repairs ProfileImagePath, restarts ProfSvc with userinit verification, and moves stale temp profiles.",
                ImmutableArray.Create(
                    "Audits startup Run keys and removes broken entries",
                    "Repairs ProfileImagePath/ProfSvc and cleans stale .000/.bak profiles"),
                "automation/essentials/profile-and-logon-repair.ps1",
                DurationHint: "Approx. 6-15 minutes (profile moves may add time)",
                DetailedDescription: "Performs a profile/logon health pass by auditing HKLM/HKCU Run entries and removing broken targets, ensuring the current user's ProfileImagePath matches USERPROFILE with ProfSvc set to Automatic, restarting ProfSvc while correcting the Winlogon Userinit value, and relocating stale temp profiles (.000/.bak/Temp) into a backup folder for cleanup.",
                DocumentationLink: "essentialsaddition.md#login-and-profile-4-issues",
                Options: ImmutableArray.Create(
                    new EssentialsTaskOptionDefinition(
                        id: "audit-startup",
                        label: "Audit startup Run keys",
                        parameterName: "SkipStartupAudit",
                        mode: EssentialsTaskOptionMode.EmitWhenFalse,
                        description: "Logs Run entries and removes ones pointing to missing executables."),
                    new EssentialsTaskOptionDefinition(
                        id: "repair-profileimagepath",
                        label: "Repair ProfileImagePath",
                        parameterName: "SkipProfilePathRepair",
                        mode: EssentialsTaskOptionMode.EmitWhenFalse,
                        description: "Aligns ProfileImagePath with USERPROFILE and sets ProfSvc to Automatic."),
                    new EssentialsTaskOptionDefinition(
                        id: "reset-profswc-userinit",
                        label: "Restart ProfSvc and fix Userinit",
                        parameterName: "SkipProfSvcReset",
                        mode: EssentialsTaskOptionMode.EmitWhenFalse,
                        description: "Restarts ProfSvc and enforces Winlogon Userinit to userinit.exe."),
                    new EssentialsTaskOptionDefinition(
                        id: "cleanup-stale-profiles",
                        label: "Cleanup stale temp profiles",
                        parameterName: "SkipStaleProfileCleanup",
                        mode: EssentialsTaskOptionMode.EmitWhenFalse,
                        description: "Moves .000/.bak/Temp profiles to a backup folder under C:\\Users."))),

            new EssentialsTaskDefinition(
                "recovery-boot-repair",
                "Recovery & boot repair",
                "Recovery",
                "Exits Safe Mode, runs bootrec repairs, provides offline DISM guidance, toggles testsigning off, repairs time sync, resets WMI, and captures dumps/driver inventory.",
                ImmutableArray.Create(
                    "Clears safeboot flags and runs bootrec fixes",
                    "Offers DISM recovery guidance and repairs time sync/WMI"),
                "automation/essentials/recovery-and-boot-repair.ps1",
                DurationHint: "Approx. 6-18 minutes (bootrec/WMI steps may add time)",
                DetailedDescription: "Automates recovery basics by clearing safeboot to exit Safe Mode, running bootrec /fixmbr /fixboot /rebuildbcd, surfacing offline DISM guidance, disabling testsigning, repairing time sync, salvaging/resetting the WMI repository with Winmgmt restart, and collecting recent minidumps plus driver inventory for triage.",
                DocumentationLink: "essentialsaddition.md#mission-critical-recovery-7-issues",
                Options: ImmutableArray.Create(
                    new EssentialsTaskOptionDefinition(
                        id: "exit-safemode",
                        label: "Exit Safe Mode",
                        parameterName: "SkipSafeModeExit",
                        mode: EssentialsTaskOptionMode.EmitWhenFalse),
                    new EssentialsTaskOptionDefinition(
                        id: "bootrec-fixes",
                        label: "Run bootrec repairs",
                        parameterName: "SkipBootrecFixes",
                        mode: EssentialsTaskOptionMode.EmitWhenFalse),
                    new EssentialsTaskOptionDefinition(
                        id: "dism-guidance",
                        label: "Show offline DISM guidance",
                        parameterName: "SkipDismGuidance",
                        mode: EssentialsTaskOptionMode.EmitWhenFalse),
                    new EssentialsTaskOptionDefinition(
                        id: "toggle-testsigning",
                        label: "Disable testsigning",
                        parameterName: "SkipTestSigningToggle",
                        mode: EssentialsTaskOptionMode.EmitWhenFalse),
                    new EssentialsTaskOptionDefinition(
                        id: "repair-timesync",
                        label: "Repair time sync",
                        parameterName: "SkipTimeSyncRepair",
                        mode: EssentialsTaskOptionMode.EmitWhenFalse),
                    new EssentialsTaskOptionDefinition(
                        id: "repair-wmi",
                        label: "Repair WMI repository",
                        parameterName: "SkipWmiRepair",
                        mode: EssentialsTaskOptionMode.EmitWhenFalse),
                    new EssentialsTaskOptionDefinition(
                        id: "dump-driver-scan",
                        label: "Collect dumps and driver inventory",
                        parameterName: "SkipDumpAndDriverScan",
                        mode: EssentialsTaskOptionMode.EmitWhenFalse,
                        description: "Lists recent minidumps and runs driverquery /v for triage."))),

            new EssentialsTaskDefinition(
                "graphics-display-repair",
                "Graphics & display repair",
                "Display",
                "Resets display adapters, restarts display services, refreshes HDR/night light, reapplies display configuration, triggers EDID/PnP rescans, and optionally restarts DWM/color profiles/vendor panels.",
                ImmutableArray.Create(
                    "Disables/re-enables the primary display adapter",
                    "Restarts display enhancement services and re-applies display mode"),
                "automation/essentials/graphics-and-display-repair.ps1",
                DurationHint: "Approx. 4-12 minutes (adapter reset may blink screens)",
                DetailedDescription: "Runs a graphics/display health pass by disabling and re-enabling the primary display adapter, restarting DisplayEnhancementService/UdkUserSvc, refreshing HDR/night light policies, reapplying the current display configuration via DisplaySwitch, and forcing a PnP rescan to refresh EDID/stack state, with optional DWM restart, color profile export/reload via dispdiag, and vendor control panel resets (NVIDIA app clocks reset; AMD AMDRSServ restart when available).",
                DocumentationLink: "essentialsaddition.md#graphics-and-display-5-issues",
                Options: ImmutableArray.Create(
                    new EssentialsTaskOptionDefinition(
                        id: "adapter-reset",
                        label: "Reset display adapter",
                        parameterName: "SkipAdapterReset",
                        mode: EssentialsTaskOptionMode.EmitWhenFalse,
                        description: "Disables and re-enables the primary display adapter (skipped unless risky actions are allowed)."),
                    new EssentialsTaskOptionDefinition(
                        id: "restart-display-services",
                        label: "Restart display services",
                        parameterName: "SkipDisplayServicesRestart",
                        mode: EssentialsTaskOptionMode.EmitWhenFalse,
                        description: "Restarts DisplayEnhancementService and UdkUserSvc (skipped unless risky actions are allowed)."),
                    new EssentialsTaskOptionDefinition(
                        id: "refresh-hdr-nightlight",
                        label: "Refresh HDR/night light",
                        parameterName: "SkipHdrNightLightRefresh",
                        mode: EssentialsTaskOptionMode.EmitWhenFalse,
                        description: "Restarts DisplayEnhancementService to re-apply HDR/night light policies."),
                    new EssentialsTaskOptionDefinition(
                        id: "reapply-resolution",
                        label: "Reapply current resolution",
                        parameterName: "SkipResolutionReapply",
                        mode: EssentialsTaskOptionMode.EmitWhenFalse,
                        description: "Runs DisplaySwitch /internal to reapply the active display mode (skipped unless risky actions are allowed)."),
                    new EssentialsTaskOptionDefinition(
                        id: "refresh-edid",
                        label: "Refresh EDID/PnP",
                        parameterName: "SkipEdidRefresh",
                        mode: EssentialsTaskOptionMode.EmitWhenFalse,
                        description: "Triggers pnputil /scan-devices for display stack refresh (skipped unless risky actions are allowed)."),
                    new EssentialsTaskOptionDefinition(
                        id: "restart-dwm",
                        label: "Restart DWM (compositor)",
                        parameterName: "SkipDwmRestart",
                        mode: EssentialsTaskOptionMode.EmitWhenFalse,
                        description: "Stops dwm.exe to clear window border/alt-tab/transparency glitches (skipped unless risky actions are allowed)."),
                    new EssentialsTaskOptionDefinition(
                        id: "reapply-color-profiles",
                        label: "Reinstall color profiles (dispdiag)",
                        parameterName: "SkipColorProfileReapply",
                        mode: EssentialsTaskOptionMode.EmitWhenFalse,
                        description: "Exports then reloads color profiles via dispdiag to fix ICC/profile drift (skipped unless risky actions are allowed)."),
                    new EssentialsTaskOptionDefinition(
                        id: "reset-gpu-panel",
                        label: "Reset GPU control panel settings",
                        parameterName: "SkipGpuControlPanelReset",
                        mode: EssentialsTaskOptionMode.EmitWhenFalse,
                        description: "NVIDIA: nvidia-smi --reset-app-clocks; AMD: restart AMDRSServ/reset if available (skipped unless risky actions are allowed)."),
                    new EssentialsTaskOptionDefinition(
                        id: "allow-risky-display-actions",
                        label: "Allow risky display actions (in-app)",
                        parameterName: "AllowRiskyDisplayActions",
                        defaultValue: false,
                        description: "Override the safety skip for adapter/service/resolution/EDID/DWM/color/GPU actions; may crash the TidyWindow UI."))),

            new EssentialsTaskDefinition(
                "onedrive-cloud-repair",
                "OneDrive & cloud sync repair",
                "Cloud",
                "Resets OneDrive client, restarts sync services, repairs KFM mappings back to local profiles, and restores autorun startup for OneDrive.",
                ImmutableArray.Create(
                    "Resets OneDrive client and restarts sync services",
                    "Repairs KFM shell folder mappings and autorun startup"),
                "automation/essentials/onedrive-and-cloud-repair.ps1",
                DurationHint: "Approx. 4-12 minutes (reset may re-download headers)",
                DetailedDescription: "Refreshes OneDrive/consumer sync by issuing OneDrive.exe /reset and relaunching in background, restarting OneSync/FileSync services, restoring known folder mappings back to local profile paths when they point to OneDrive, and recreating the OneDrive autorun entry in HKCU Run for startup consistency.",
                DocumentationLink: "essentialsaddition.md#onedrive-and-cloud-sync-4-issues",
                Options: ImmutableArray.Create(
                    new EssentialsTaskOptionDefinition(
                        id: "reset-onedrive",
                        label: "Reset OneDrive client",
                        parameterName: "SkipOneDriveReset",
                        mode: EssentialsTaskOptionMode.EmitWhenFalse,
                        description: "Runs OneDrive.exe /reset then relaunches /background."),
                    new EssentialsTaskOptionDefinition(
                        id: "restart-sync-services",
                        label: "Restart sync services",
                        parameterName: "SkipSyncServicesRestart",
                        mode: EssentialsTaskOptionMode.EmitWhenFalse,
                        description: "Restarts OneSyncSvc, FileSyncProvider, and FileSyncSvc if present."),
                    new EssentialsTaskOptionDefinition(
                        id: "repair-kfm",
                        label: "Repair KFM mappings",
                        parameterName: "SkipKfmMappingRepair",
                        mode: EssentialsTaskOptionMode.EmitWhenFalse,
                        description: "Resets Desktop/Documents/Pictures/Music/Videos shell folders to local profile paths when pointed at OneDrive."),
                    new EssentialsTaskOptionDefinition(
                        id: "recreate-autorun",
                        label: "Restore OneDrive autorun",
                        parameterName: "SkipAutorunTaskRecreate",
                        mode: EssentialsTaskOptionMode.EmitWhenFalse,
                        description: "Ensures HKCU Run has OneDrive /background for startup."))),

            new EssentialsTaskDefinition(
                "ram-purge",
                "RAM purge",
                "Performance",
                "Frees standby memory, trims working sets, and manages SysMain for headroom.",
                ImmutableArray.Create(
                    "Downloads EmptyStandbyList if required",
                    "Trims heavy process working sets safely"),
                "automation/essentials/ram-purge.ps1",
                DurationHint: "Approx. 2-4 minutes (download adds ~1 minute on first run)",
                DetailedDescription: "Fetches EmptyStandbyList when missing, clears standby lists, trims memory-heavy processes, optionally pauses SysMain, and restores it once headroom is reclaimed.",
                DocumentationLink: "docs/essentials-overview.md#4-ram-purge",
                Options: ImmutableArray.Create(
                    new EssentialsTaskOptionDefinition(
                        id: "clear-standby",
                        label: "Clear standby memory lists",
                        parameterName: "SkipStandbyClear",
                        mode: EssentialsTaskOptionMode.EmitWhenFalse),
                    new EssentialsTaskOptionDefinition(
                        id: "trim-working-sets",
                        label: "Trim heavy working sets",
                        parameterName: "SkipWorkingSetTrim",
                        mode: EssentialsTaskOptionMode.EmitWhenFalse),
                    new EssentialsTaskOptionDefinition(
                        id: "toggle-sysmain",
                        label: "Pause SysMain during purge",
                        parameterName: "SkipSysMainToggle",
                        mode: EssentialsTaskOptionMode.EmitWhenFalse))),

            new EssentialsTaskDefinition(
                "restore-manager",
                "System Restore manager",
                "Recovery",
                "Creates, lists, and prunes restore points to safeguard maintenance flows.",
                ImmutableArray.Create(
                    "Enables restore across targeted drives",
                    "Prunes aged or excess checkpoints"),
                "automation/essentials/system-restore-manager.ps1",
                DurationHint: "Approx. 4-8 minutes when creating a new restore point",
                DetailedDescription: "Validates System Restore configuration, enables protection per drive, creates fresh checkpoints, lists historical restore points, and prunes by age or quota in one run.",
                DocumentationLink: "docs/essentials-overview.md#5-system-restore-snapshot-manager"),

            new EssentialsTaskDefinition(
                "network-fix",
                "Network fix suite",
                "Network",
                "Runs advanced adapter resets alongside diagnostics like traceroute and pathping.",
                ImmutableArray.Create(
                    "Resets ARP/NBT/TCP heuristics and re-registers DNS",
                    "Captures latency samples and adapter stats"),
                "automation/essentials/network-fix-suite.ps1",
                DurationHint: "Approx. 6-12 minutes (pathping plus diagnostics)",
                DetailedDescription: "Resets network heuristics, re-registers DNS, runs adapter restarts, traceroute, ping sweeps, and pathping loss analysis, and captures adapter statistics for troubleshooting.",
                DocumentationLink: "docs/essentials-overview.md#6-network-fix-suite-advanced",
                Options: ImmutableArray.Create(
                    new EssentialsTaskOptionDefinition(
                        id: "diagnostics-only",
                        label: "Diagnostics only (skip remediation)",
                        parameterName: "DiagnosticsOnly",
                        defaultValue: false),
                    new EssentialsTaskOptionDefinition(
                        id: "run-traceroute",
                        label: "Run traceroute",
                        parameterName: "SkipTraceroute",
                        mode: EssentialsTaskOptionMode.EmitWhenFalse),
                    new EssentialsTaskOptionDefinition(
                        id: "run-pathping",
                        label: "Run pathping",
                        parameterName: "SkipPathPing",
                        mode: EssentialsTaskOptionMode.EmitWhenFalse),
                    new EssentialsTaskOptionDefinition(
                        id: "dns-registration",
                        label: "Re-register DNS",
                        parameterName: "SkipDnsRegistration",
                        mode: EssentialsTaskOptionMode.EmitWhenFalse))),

            new EssentialsTaskDefinition(
                "app-repair",
                "App repair helper",
                "Apps",
                "Resets Microsoft Store infrastructure and re-registers critical AppX packages.",
                ImmutableArray.Create(
                    "Clears Store cache and restarts dependent services",
                    "Re-registers App Installer and framework packages"),
                "automation/essentials/app-repair-helper.ps1",
                DurationHint: "Approx. 7-12 minutes depending on package re-registration",
                DetailedDescription: "Flushes the Microsoft Store cache, restarts licensing services, re-registers App Installer, Store, and supporting AppX frameworks for all users, and verifies provisioning state.",
                DocumentationLink: "docs/essentials-overview.md#7-app-repair-helper-storeuwp",
                Options: ImmutableArray.Create(
                    new EssentialsTaskOptionDefinition(
                        id: "reset-store-cache",
                        label: "Reset Microsoft Store cache",
                        parameterName: "ResetStoreCache"),
                    new EssentialsTaskOptionDefinition(
                        id: "re-register-store",
                        label: "Re-register Store components",
                        parameterName: "ReRegisterStore"),
                    new EssentialsTaskOptionDefinition(
                        id: "repair-app-installer",
                        label: "Repair App Installer",
                        parameterName: "ReRegisterAppInstaller"),
                    new EssentialsTaskOptionDefinition(
                        id: "re-register-builtins",
                        label: "Re-register built-in apps",
                        parameterName: "ReRegisterPackages"),
                    new EssentialsTaskOptionDefinition(
                        id: "re-register-provisioned",
                        label: "Re-register provisioned apps",
                        parameterName: "ReRegisterProvisioned",
                        defaultValue: false,
                        description: "Re-registers all provisioned AppX packages from WindowsApps payloads."),
                    new EssentialsTaskOptionDefinition(
                        id: "restart-store-services",
                        label: "Restart Store services (UwpSvc/InstallService)",
                        parameterName: "RestartStoreServices",
                        defaultValue: false,
                        description: "Restarts UwpSvc and InstallService to clear stuck deployments."),
                    new EssentialsTaskOptionDefinition(
                        id: "refresh-frameworks",
                        label: "Refresh AppX frameworks",
                        parameterName: "IncludeFrameworks"),
                    new EssentialsTaskOptionDefinition(
                        id: "repair-licensing",
                        label: "Repair licensing services",
                        parameterName: "ConfigureLicensingServices"),
                    new EssentialsTaskOptionDefinition(
                        id: "reinstall-store",
                        label: "Attempt Store reinstall if missing",
                        parameterName: "ReinstallStoreIfMissing",
                        defaultValue: false,
                        description: "Registers Microsoft.WindowsStore from WindowsApps when absent."),
                    new EssentialsTaskOptionDefinition(
                        id: "reset-capability-access",
                        label: "Reset capability access policies",
                        parameterName: "ResetCapabilityAccess",
                        defaultValue: false,
                        description: "Backs up and resets CapabilityAccessManager ConsentStore for UWP permissions."),
                    new EssentialsTaskOptionDefinition(
                        id: "current-user-only",
                        label: "Limit repairs to current user",
                        parameterName: "CurrentUserOnly",
                        defaultValue: false))),

            new EssentialsTaskDefinition(
                "edge-reset",
                "Browser reset & cache cleanup",
                "Apps",
                "Clears caches, WebView data, or stuck policies across Edge, Chrome, Brave, Firefox, and Opera with optional repair actions.",
                ImmutableArray.Create(
                    "Stops selected browsers before purging profile caches with dry-run previews",
                    "Optional WebView reset, policy removal, and signed Edge repair when applicable"),
                "automation/essentials/browser-reset-and-cache-cleanup.ps1",
                DurationHint: "Approx. 4-10 minutes (repairs add a few extra minutes)",
                DetailedDescription: "Safely closes running Microsoft Edge, Google Chrome, Brave, Firefox, or Opera instances, clears profile caches for each selected browser, optionally wipes Edge WebView2 data, removes HKCU/HKLM policy overrides, and can trigger setup.exe --force-reinstall so Edge resets to a known-good state with full transcript logging.",
                DocumentationLink: "docs/essentials.md#8-browser-reset--cache-cleanup",
                Options: ImmutableArray.Create(
                    new EssentialsTaskOptionDefinition(
                        id: "include-edge",
                        label: "Include Microsoft Edge",
                        parameterName: "IncludeEdge",
                        description: "Adds Edge caches, policies, WebView cleanup, and unlocks installer repair."),
                    new EssentialsTaskOptionDefinition(
                        id: "include-chrome",
                        label: "Include Google Chrome",
                        parameterName: "IncludeChrome",
                        defaultValue: false,
                        description: "Targets Chrome profile caches and policy keys."),
                    new EssentialsTaskOptionDefinition(
                        id: "include-brave",
                        label: "Include Brave",
                        parameterName: "IncludeBrave",
                        defaultValue: false,
                        description: "Targets Brave profile caches and policy keys."),
                    new EssentialsTaskOptionDefinition(
                        id: "include-firefox",
                        label: "Include Firefox",
                        parameterName: "IncludeFirefox",
                        defaultValue: false,
                        description: "Targets Firefox cache stores across roaming/local profiles."),
                    new EssentialsTaskOptionDefinition(
                        id: "include-opera",
                        label: "Include Opera",
                        parameterName: "IncludeOpera",
                        defaultValue: false,
                        description: "Targets Opera profile caches discovered under AppData."),
                    new EssentialsTaskOptionDefinition(
                        id: "force-close-browsers",
                        label: "Force close selected browsers",
                        parameterName: "ForceCloseBrowsers"),
                    new EssentialsTaskOptionDefinition(
                        id: "clear-profile-caches",
                        label: "Clear profile caches",
                        parameterName: "ClearProfileCaches"),
                    new EssentialsTaskOptionDefinition(
                        id: "clear-webview-caches",
                        label: "Clear Edge WebView2 caches",
                        parameterName: "ClearWebViewCaches"),
                    new EssentialsTaskOptionDefinition(
                        id: "reset-browser-policies",
                        label: "Reset browser policy keys (requires admin for HKLM)",
                        parameterName: "ResetPolicies",
                        defaultValue: false,
                        description: "Removes policy hives for each selected browser so they can revert to defaults."),
                    new EssentialsTaskOptionDefinition(
                        id: "repair-edge-install",
                        label: "Run Edge installer repair",
                        parameterName: "RepairEdgeInstall",
                        defaultValue: false,
                        description: "Executes setup.exe --force-reinstall when Edge is included and session is elevated."),
                    new EssentialsTaskOptionDefinition(
                        id: "browser-dry-run",
                        label: "Dry run (preview only)",
                        parameterName: "DryRun",
                        defaultValue: false,
                        description: "Report the directories/policies that would be touched without making changes."))),

            new EssentialsTaskDefinition(
                "windows-update-repair",
                "Windows Update repair toolkit",
                "Updates",
                "Resets Windows Update services, caches, and supporting components in one pass.",
                ImmutableArray.Create(
                    "Stops services, resets SoftwareDistribution and Catroot2",
                    "Re-registers DLLs and can trigger fresh scans"),
                "automation/essentials/windows-update-repair-toolkit.ps1",
                DurationHint: "Approx. 25-45 minutes (cache reset plus optional DISM/SFC)",
                DetailedDescription: "Stops the Windows Update service stack, clears SoftwareDistribution and Catroot2, re-registers core DLLs, removes stuck policies, optionally runs DISM/SFC, and kicks off a new update scan.",
                DocumentationLink: "docs/essentials-overview.md#9-windows-update-repair-toolkit",
                Options: ImmutableArray.Create(
                    new EssentialsTaskOptionDefinition(
                        id: "reset-services",
                        label: "Reset update services",
                        parameterName: "ResetServices"),
                    new EssentialsTaskOptionDefinition(
                        id: "reset-components",
                        label: "Reset update components",
                        parameterName: "ResetComponents"),
                    new EssentialsTaskOptionDefinition(
                        id: "reregister-libraries",
                        label: "Re-register update DLLs",
                        parameterName: "ReRegisterLibraries"),
                    new EssentialsTaskOptionDefinition(
                        id: "run-dism-restorehealth",
                        label: "Run DISM RestoreHealth",
                        parameterName: "RunDismRestoreHealth"),
                    new EssentialsTaskOptionDefinition(
                        id: "run-sfc",
                        label: "Run SFC /scannow",
                        parameterName: "RunSfc"),
                    new EssentialsTaskOptionDefinition(
                        id: "trigger-scan",
                        label: "Trigger Windows Update scan",
                        parameterName: "TriggerScan"),
                    new EssentialsTaskOptionDefinition(
                        id: "reset-policies",
                        label: "Reset WU policies",
                        parameterName: "ResetPolicies"),
                    new EssentialsTaskOptionDefinition(
                        id: "reset-network",
                        label: "Reset network stack",
                        parameterName: "ResetNetwork"))),

            new EssentialsTaskDefinition(
                "task-scheduler-repair",
                "Task Scheduler & automation repair",
                "Automation",
                "Rebuilds Task Scheduler cache, re-enables USO/Windows Update tasks, and restarts Schedule service to refresh triggers.",
                ImmutableArray.Create(
                    "Backs up TaskCache and lets Schedule rebuild metadata",
                    "Re-enables key UpdateOrchestrator tasks and restarts Schedule"),
                "automation/essentials/task-scheduler-repair.ps1",
                DurationHint: "Approx. 3-8 minutes (TaskCache rebuild may pause scheduled tasks briefly)",
                DetailedDescription: "Stops Schedule when needed, backs up the TaskCache directory to force a metadata rebuild from existing Tasks, re-enables common UpdateOrchestrator/WindowsUpdate tasks, and restarts the Schedule service to refresh triggers and registrations with logging for each step.",
                DocumentationLink: "essentialsaddition.md#task-scheduler-and-automation-3-issues",
                Options: ImmutableArray.Create(
                    new EssentialsTaskOptionDefinition(
                        id: "rebuild-task-cache",
                        label: "Rebuild TaskCache metadata",
                        parameterName: "SkipTaskCacheRebuild",
                        mode: EssentialsTaskOptionMode.EmitWhenFalse,
                        description: "Stops Schedule, backs up TaskCache, and lets it rebuild from Tasks tree."),
                    new EssentialsTaskOptionDefinition(
                        id: "enable-uso-tasks",
                        label: "Enable USO/Windows Update tasks",
                        parameterName: "SkipUsoTaskEnable",
                        mode: EssentialsTaskOptionMode.EmitWhenFalse,
                        description: "Re-enables common UpdateOrchestrator tasks like Schedule Scan and UpdateModel."),
                    new EssentialsTaskOptionDefinition(
                        id: "skip-uso-rebuild",
                        label: "Skip rebuild of missing USO tasks",
                        parameterName: "SkipUsoTaskRebuild",
                        defaultValue: false,
                        description: "Avoid recreating missing UpdateOrchestrator/WindowsUpdate tasks from baseline; only enable tasks that already exist.",
                        mode: EssentialsTaskOptionMode.EmitWhenTrue),
                    new EssentialsTaskOptionDefinition(
                        id: "restart-schedule",
                        label: "Restart Schedule service",
                        parameterName: "SkipScheduleReset",
                        mode: EssentialsTaskOptionMode.EmitWhenFalse,
                        description: "Restarts Schedule to refresh triggers after repairs."))),

            new EssentialsTaskDefinition(
                "time-region-repair",
                "Time & region repair",
                "System",
                "Sets time zone, forces NTP resync, repairs Windows Time service, and resets locale/language defaults.",
                ImmutableArray.Create(
                    "Applies target time zone and re-syncs against time.windows.com",
                    "Repairs W32Time and resets system locale/user language list"),
                "automation/essentials/time-and-region-repair.ps1",
                DurationHint: "Approx. 3-8 minutes (language list reset may prompt component download)",
                DetailedDescription: "Applies the requested or current time zone, configures time.windows.com peers with an immediate resync, repairs and restarts the Windows Time service, and resets system locale/culture plus the user language list to en-US (overrideable via parameters).",
                DocumentationLink: "essentialsaddition.md#time-region-and-ntp-3-issues",
                Options: ImmutableArray.Create(
                    new EssentialsTaskOptionDefinition(
                        id: "set-timezone",
                        label: "Set time zone and resync NTP",
                        parameterName: "SkipTimeZoneSync",
                        mode: EssentialsTaskOptionMode.EmitWhenFalse,
                        description: "Validates target time zone (defaults to current/UTC), configures time.windows.com peers, and forces NTP resync."),
                    new EssentialsTaskOptionDefinition(
                        id: "reset-locale",
                        label: "Reset locale and languages",
                        parameterName: "SkipLocaleReset",
                        mode: EssentialsTaskOptionMode.EmitWhenFalse,
                        description: "Sets system locale/culture and user language list to en-US unless overridden."),
                    new EssentialsTaskOptionDefinition(
                        id: "repair-w32time",
                        label: "Repair Windows Time service",
                        parameterName: "SkipTimeServiceRepair",
                        mode: EssentialsTaskOptionMode.EmitWhenFalse,
                        description: "Registers, sets Automatic startup, restarts W32Time, and enforces NTP peers before resync."),
                    new EssentialsTaskOptionDefinition(
                        id: "fallback-ntp",
                        label: "Use fallback NTP peers",
                        parameterName: "UseFallbackNtpPeers",
                        defaultValue: false,
                        description: "If primary NTP sync fails, retry with time.google.com, pool.ntp.org, and time.nist.gov."),
                    new EssentialsTaskOptionDefinition(
                        id: "report-clock-offset",
                        label: "Report clock offset",
                        parameterName: "ReportClockOffset",
                        defaultValue: false,
                        description: "Runs w32tm /stripchart against the primary peer to show current clock drift."))),

            new EssentialsTaskDefinition(
                "defender-repair",
                "Windows Defender repair & deep scan",
                "Security",
                "Restores Microsoft Defender services, forces signature updates, and runs targeted scans.",
                ImmutableArray.Create(
                    "Restarts WinDefend, WdNisSvc, and SecurityHealthService with safe defaults",
                    "Updates signatures and supports quick, full, or custom scans with dry-run logging"),
                "automation/essentials/windows-defender-repair-and-deep-scan.ps1",
                DurationHint: "Approx. 15-30 minutes (full scans or large path sets extend runtime)",
                DetailedDescription: "Heals Microsoft Defender by restarting critical services, forcing signature and engine refreshes, optionally re-enabling real-time protection, and executing quick, full, or custom scans while producing transcripts and JSON run summaries for the UI.",
                DocumentationLink: "docs/essentials-overview.md#10-windows-defender-repair--deep-scan"),

            new EssentialsTaskDefinition(
                "print-spooler-recovery",
                "Print spooler recovery suite",
                "Printing",
                "Clears jammed queues, rebuilds spooler services, and restores DLL registrations.",
                ImmutableArray.Create(
                    "Stops Spooler/PrintNotify, purges %SystemRoot%\\System32\\spool\\PRINTERS",
                    "Optional DLL re-registration, stale driver cleanup, and isolation policy reset"),
                "automation/essentials/print-spooler-recovery-suite.ps1",
                DurationHint: "Approx. 5-12 minutes (driver cleanup may extend runtime)",
                DetailedDescription: "Automates end-to-end spooler remediation by restarting services, flushing stuck print jobs, optionally removing stale drivers, re-registering spooler DLLs, and resetting isolation policies with dry-run visibility for support teams.",
                DocumentationLink: "docs/essentials-overview.md#12-print-spooler-recovery-suite",
                Options: ImmutableArray.Create(
                    new EssentialsTaskOptionDefinition(
                        id: "service-reset",
                        label: "Stop & restart spooler services",
                        parameterName: "SkipServiceReset",
                        mode: EssentialsTaskOptionMode.EmitWhenFalse),
                    new EssentialsTaskOptionDefinition(
                        id: "queue-purge",
                        label: "Clear print queue",
                        parameterName: "SkipSpoolPurge",
                        mode: EssentialsTaskOptionMode.EmitWhenFalse),
                    new EssentialsTaskOptionDefinition(
                        id: "driver-cleanup",
                        label: "Remove stale printer drivers",
                        parameterName: "SkipDriverRefresh",
                        mode: EssentialsTaskOptionMode.EmitWhenFalse),
                    new EssentialsTaskOptionDefinition(
                        id: "dll-registration",
                        label: "Re-register spooler DLLs",
                        parameterName: "SkipDllRegistration",
                        mode: EssentialsTaskOptionMode.EmitWhenFalse),
                    new EssentialsTaskOptionDefinition(
                        id: "isolation-reset",
                        label: "Reset print isolation policies",
                        parameterName: "SkipPrintIsolationPolicy",
                        mode: EssentialsTaskOptionMode.EmitWhenFalse))));
    }

    public ImmutableArray<EssentialsTaskDefinition> Tasks => _tasks;

    public EssentialsTaskDefinition GetTask(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("Task id must be provided.", nameof(id));
        }

        foreach (var task in _tasks)
        {
            if (string.Equals(task.Id, id, StringComparison.OrdinalIgnoreCase))
            {
                return task;
            }
        }

        throw new InvalidOperationException($"Unknown essentials task '{id}'.");
    }
}

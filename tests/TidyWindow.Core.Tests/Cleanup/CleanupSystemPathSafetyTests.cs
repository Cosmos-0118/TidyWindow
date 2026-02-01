using System;
using TidyWindow.Core.Cleanup;
using Xunit;

namespace TidyWindow.Core.Tests.Cleanup;

public class CleanupSystemPathSafetyTests
{
    [Fact]
    public void NullPath_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => CleanupSystemPathSafety.IsSystemCriticalPath(null!));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void EmptyOrWhitespace_Throws(string path)
    {
        Assert.Throws<ArgumentException>(() => CleanupSystemPathSafety.IsSystemCriticalPath(path));
    }

    [Fact]
    public void InvalidCharacters_Throws()
    {
        Assert.Throws<ArgumentException>(() => CleanupSystemPathSafety.IsSystemCriticalPath("C:<>\\"));
    }

    [Theory]
    [InlineData("C:../bootmgr")]
    [InlineData("..\\..\\..\\bootmgr")]
    [InlineData("../../Windows/System32")]
    public void Traversal_ReturnsFalse(string path)
    {
        var result = CleanupSystemPathSafety.IsSystemCriticalPath(path);
        Assert.False(result);
    }

    [Theory]
    [InlineData("C\\Windows\\System32\\..\\System32\\config\\SAM")]
    public void NormalizedCriticalSubpath_ReturnsTrue(string path)
    {
        var normalized = path.Replace('\\', System.IO.Path.DirectorySeparatorChar);
        Assert.True(CleanupSystemPathSafety.IsSystemCriticalPath(normalized));
    }

    [Theory]
    [InlineData("C:\\bootmgr")]
    [InlineData("X:\\BCD")]
    public void CriticalFiles_ReturnTrue(string path)
    {
        Assert.True(CleanupSystemPathSafety.IsSystemCriticalPath(path));
    }

    [Fact]
    public void SystemDirectorySubpath_ReturnsTrue()
    {
        var system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var sam = System.IO.Path.Combine(system32, "config", "SAM");
        Assert.True(CleanupSystemPathSafety.IsSystemCriticalPath(sam));
    }

    [Fact]
    public void NonCritical_WindowsTemp_ReturnsFalse()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var temp = System.IO.Path.Combine(windows, "Temp");
        Assert.False(CleanupSystemPathSafety.IsSystemCriticalPath(temp));
    }

    [Fact]
    public void CaseInsensitive_Matches()
    {
        var system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var mixed = system32.Replace("System32", "SySTeM32", StringComparison.OrdinalIgnoreCase);
        Assert.True(CleanupSystemPathSafety.IsSystemCriticalPath(mixed));
    }

    [Fact]
    public void AdditionalRoots_AreHonored()
    {
        var customRoot = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "DangerZone");
        CleanupSystemPathSafety.SetAdditionalCriticalRoots(new[] { customRoot });

        Assert.True(CleanupSystemPathSafety.IsSystemCriticalPath(customRoot));
    }

    // ================ NEW TESTS FOR SAFE CLEANUP PATHS ================

    [Fact]
    public void SafeCleanup_WindowsPrefetch_IsNotCritical()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var prefetch = System.IO.Path.Combine(windows, "Prefetch");
        Assert.False(CleanupSystemPathSafety.IsSystemCriticalPath(prefetch));
    }

    [Fact]
    public void SafeCleanup_SoftwareDistributionDownload_IsNotCritical()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var download = System.IO.Path.Combine(windows, "SoftwareDistribution", "Download");
        Assert.False(CleanupSystemPathSafety.IsSystemCriticalPath(download));
    }

    [Fact]
    public void SafeCleanup_WindowsUpdateLogs_IsNotCritical()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var logs = System.IO.Path.Combine(windows, "Logs", "WindowsUpdate");
        Assert.False(CleanupSystemPathSafety.IsSystemCriticalPath(logs));
    }

    [Fact]
    public void SafeCleanup_CBSLogs_IsNotCritical()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var cbs = System.IO.Path.Combine(windows, "Logs", "CBS");
        Assert.False(CleanupSystemPathSafety.IsSystemCriticalPath(cbs));
    }

    [Fact]
    public void SafeCleanup_InstallerPatchCache_IsNotCritical()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var patchCache = System.IO.Path.Combine(windows, "Installer", "$PatchCache$");
        Assert.False(CleanupSystemPathSafety.IsSystemCriticalPath(patchCache));
    }

    [Fact]
    public void SafeCleanup_Minidump_IsNotCritical()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var minidump = System.IO.Path.Combine(windows, "Minidump");
        Assert.False(CleanupSystemPathSafety.IsSystemCriticalPath(minidump));
    }

    [Fact]
    public void SafeCleanup_Panther_IsNotCritical()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var panther = System.IO.Path.Combine(windows, "Panther");
        Assert.False(CleanupSystemPathSafety.IsSystemCriticalPath(panther));
    }

    [Fact]
    public void SafeCleanup_WER_IsNotCritical()
    {
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var wer = System.IO.Path.Combine(programData, "Microsoft", "Windows", "WER");
        Assert.False(CleanupSystemPathSafety.IsSystemCriticalPath(wer));
    }

    [Fact]
    public void SafeCleanup_PackageCache_IsNotCritical()
    {
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var packageCache = System.IO.Path.Combine(programData, "Package Cache");
        Assert.False(CleanupSystemPathSafety.IsSystemCriticalPath(packageCache));
    }

    [Fact]
    public void SafeCleanup_WindowsDebug_IsNotCritical()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var debug = System.IO.Path.Combine(windows, "Debug");
        Assert.False(CleanupSystemPathSafety.IsSystemCriticalPath(debug));
    }

    // ================ CRITICAL PATHS MUST STILL BE PROTECTED ================

    [Fact]
    public void Critical_System32_IsCritical()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var system32 = System.IO.Path.Combine(windows, "System32");
        Assert.True(CleanupSystemPathSafety.IsSystemCriticalPath(system32));
    }

    [Fact]
    public void Critical_WinSxS_IsCritical()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var winsxs = System.IO.Path.Combine(windows, "WinSxS");
        Assert.True(CleanupSystemPathSafety.IsSystemCriticalPath(winsxs));
    }

    [Fact]
    public void Critical_Fonts_IsCritical()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var fonts = System.IO.Path.Combine(windows, "Fonts");
        Assert.True(CleanupSystemPathSafety.IsSystemCriticalPath(fonts));
    }

    [Fact]
    public void Critical_Boot_IsCritical()
    {
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var systemDrive = System.IO.Path.GetPathRoot(system);
        var boot = System.IO.Path.Combine(systemDrive!, "Boot");
        Assert.True(CleanupSystemPathSafety.IsSystemCriticalPath(boot));
    }

    [Fact]
    public void Critical_EFI_IsCritical()
    {
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var systemDrive = System.IO.Path.GetPathRoot(system);
        var efi = System.IO.Path.Combine(systemDrive!, "EFI");
        Assert.True(CleanupSystemPathSafety.IsSystemCriticalPath(efi));
    }

    [Fact]
    public void Critical_Recovery_IsCritical()
    {
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var systemDrive = System.IO.Path.GetPathRoot(system);
        var recovery = System.IO.Path.Combine(systemDrive!, "Recovery");
        Assert.True(CleanupSystemPathSafety.IsSystemCriticalPath(recovery));
    }

    [Fact]
    public void Critical_WindowsApps_IsCritical()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var windowsApps = System.IO.Path.Combine(programFiles, "WindowsApps");
        Assert.True(CleanupSystemPathSafety.IsSystemCriticalPath(windowsApps));
    }

    [Fact]
    public void Critical_WindowsDefender_IsCritical()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var defender = System.IO.Path.Combine(programFiles, "Windows Defender");
        Assert.True(CleanupSystemPathSafety.IsSystemCriticalPath(defender));
    }

    [Fact]
    public void Critical_CryptoStore_IsCritical()
    {
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var crypto = System.IO.Path.Combine(programData, "Microsoft", "Crypto");
        Assert.True(CleanupSystemPathSafety.IsSystemCriticalPath(crypto));
    }

    [Fact]
    public void Critical_ProtectStore_IsCritical()
    {
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var protect = System.IO.Path.Combine(programData, "Microsoft", "Protect");
        Assert.True(CleanupSystemPathSafety.IsSystemCriticalPath(protect));
    }

    [Fact]
    public void Critical_KernelFiles_AreCritical()
    {
        Assert.True(CleanupSystemPathSafety.IsSystemCriticalPath("C:\\ntoskrnl.exe"));
        Assert.True(CleanupSystemPathSafety.IsSystemCriticalPath("C:\\hal.dll"));
        Assert.True(CleanupSystemPathSafety.IsSystemCriticalPath("C:\\ntdll.dll"));
    }

    [Fact]
    public void Critical_RegistryHives_AreCritical()
    {
        Assert.True(CleanupSystemPathSafety.IsSystemCriticalPath("C:\\NTUSER.DAT"));
    }

    // ================ USER FOLDERS ARE NOT CRITICAL ================

    [Fact]
    public void NonCritical_UserAppDataTemp_IsNotCritical()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var temp = System.IO.Path.Combine(localAppData, "Temp");
        Assert.False(CleanupSystemPathSafety.IsSystemCriticalPath(temp));
    }

    [Fact]
    public void NonCritical_UserDownloads_IsNotCritical()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var downloads = System.IO.Path.Combine(userProfile, "Downloads");
        Assert.False(CleanupSystemPathSafety.IsSystemCriticalPath(downloads));
    }

    [Fact]
    public void NonCritical_BrowserCache_IsNotCritical()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var chromeCache = System.IO.Path.Combine(localAppData, "Google", "Chrome", "User Data", "Default", "Cache");
        Assert.False(CleanupSystemPathSafety.IsSystemCriticalPath(chromeCache));
    }

    [Fact]
    public void NonCritical_ProgramFilesThirdPartyApp_IsNotCritical()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var someApp = System.IO.Path.Combine(programFiles, "SomeThirdPartyApp", "logs");
        Assert.False(CleanupSystemPathSafety.IsSystemCriticalPath(someApp));
    }
}

using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using TidyWindow.Core.Maintenance;

namespace TidyWindow.App.Services;

public enum PrivilegeMode
{
    Standard,
    Administrator
}

public readonly struct PrivilegeRestartResult
{
    private PrivilegeRestartResult(bool success, bool alreadyInTargetMode, string? errorMessage)
    {
        Success = success;
        AlreadyInTargetMode = alreadyInTargetMode;
        ErrorMessage = errorMessage;
    }

    public bool Success { get; }

    public bool AlreadyInTargetMode { get; }

    public string? ErrorMessage { get; }

    public static PrivilegeRestartResult SuccessResult() => new(true, false, null);

    public static PrivilegeRestartResult AlreadyRunning() => new(false, true, null);

    public static PrivilegeRestartResult Failure(string message) => new(false, false, string.IsNullOrWhiteSpace(message) ? "Unknown privilege restart error." : message);
}

public interface IPrivilegeService
{
    PrivilegeMode CurrentMode { get; }

    PrivilegeRestartResult Restart(PrivilegeMode targetMode);
}

public sealed class PrivilegeService : IPrivilegeService
{
    public PrivilegeMode CurrentMode => IsProcessElevated() ? PrivilegeMode.Administrator : PrivilegeMode.Standard;

    public PrivilegeRestartResult Restart(PrivilegeMode targetMode)
    {
        var current = CurrentMode;
        if (current == targetMode)
        {
            return PrivilegeRestartResult.AlreadyRunning();
        }

        if (!OperatingSystem.IsWindows())
        {
            return PrivilegeRestartResult.Failure("Privilege switching is only supported on Windows.");
        }

        var processPath = ResolveProcessPath();
        if (string.IsNullOrWhiteSpace(processPath))
        {
            return PrivilegeRestartResult.Failure("Unable to determine the current executable path.");
        }

        var argumentsTail = AppendOriginalUserSidArgument(BuildArgumentsTail());
        var commandLine = BuildCommandLine(processPath, argumentsTail);
        var workingDirectory = Environment.CurrentDirectory;

        try
        {
            if (targetMode == PrivilegeMode.Administrator)
            {
                StartElevated(processPath, argumentsTail, workingDirectory);
            }
            else
            {
                WindowsProcessLauncher.StartNonElevated(commandLine, workingDirectory);
            }

            return PrivilegeRestartResult.SuccessResult();
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return PrivilegeRestartResult.Failure("Privilege switch was cancelled.");
        }
        catch (Exception ex)
        {
            return PrivilegeRestartResult.Failure(ex.Message);
        }
    }

    private static bool IsProcessElevated()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        using var identity = WindowsIdentity.GetCurrent();
        if (identity is null)
        {
            return false;
        }

        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static string BuildArgumentsTail()
    {
        var args = Environment.GetCommandLineArgs();
        if (args.Length <= 1)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        for (var index = 1; index < args.Length; index++)
        {
            if (index > 1)
            {
                builder.Append(' ');
            }

            builder.Append(QuoteArgument(args[index]));
        }

        return builder.ToString();
    }

    private static string AppendOriginalUserSidArgument(string argumentsTail)
    {
        var sid = TryGetCurrentUserSid();
        if (string.IsNullOrWhiteSpace(sid))
        {
            return argumentsTail;
        }

        var argument = QuoteArgument($"{RegistryUserContext.OriginalUserSidArgumentPrefix}{sid}");
        if (string.IsNullOrEmpty(argumentsTail))
        {
            return argument;
        }

        return string.Concat(argumentsTail, ' ', argument);
    }

    private static string? TryGetCurrentUserSid()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return identity?.User?.Value;
        }
        catch
        {
            return null;
        }
    }

    private static string BuildCommandLine(string processPath, string argumentsTail)
    {
        var builder = new StringBuilder(QuoteArgument(processPath));
        if (!string.IsNullOrEmpty(argumentsTail))
        {
            builder.Append(' ');
            builder.Append(argumentsTail);
        }

        return builder.ToString();
    }

    private static string? ResolveProcessPath()
    {
        if (!string.IsNullOrWhiteSpace(Environment.ProcessPath))
        {
            return Environment.ProcessPath;
        }

        using var current = Process.GetCurrentProcess();
        return current.MainModule?.FileName;
    }

    private static void StartElevated(string processPath, string arguments, string workingDirectory)
    {
        var info = new ProcessStartInfo
        {
            FileName = processPath,
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = workingDirectory
        };

        if (!string.IsNullOrEmpty(arguments))
        {
            info.Arguments = arguments;
        }

        Process.Start(info);
    }

    private static string QuoteArgument(string argument)
    {
        if (string.IsNullOrEmpty(argument))
        {
            return "\"\"";
        }

        var requiresQuotes = false;
        foreach (var ch in argument)
        {
            if (char.IsWhiteSpace(ch) || ch == '"')
            {
                requiresQuotes = true;
                break;
            }
        }

        if (!requiresQuotes)
        {
            return argument;
        }

        var builder = new StringBuilder();
        builder.Append('"');

        var backslashes = 0;
        foreach (var ch in argument)
        {
            if (ch == '\\')
            {
                backslashes++;
                continue;
            }

            if (ch == '"')
            {
                builder.Append('\\', backslashes * 2 + 1);
                builder.Append('"');
                backslashes = 0;
                continue;
            }

            builder.Append('\\', backslashes);
            builder.Append(ch);
            backslashes = 0;
        }

        builder.Append('\\', backslashes * 2);
        builder.Append('"');
        return builder.ToString();
    }

    private static class WindowsProcessLauncher
    {
        private const uint TokenAllAccess = NativeMethods.TOKEN_ASSIGN_PRIMARY
                                            | NativeMethods.TOKEN_DUPLICATE
                                            | NativeMethods.TOKEN_QUERY
                                            | NativeMethods.TOKEN_ADJUST_DEFAULT
                                            | NativeMethods.TOKEN_ADJUST_SESSIONID;

        public static void StartNonElevated(string commandLineText, string workingDirectory)
        {
            var shellWindow = NativeMethods.GetShellWindow();
            if (shellWindow == IntPtr.Zero)
            {
                throw new InvalidOperationException("Unable to locate the Windows shell window.");
            }

            if (NativeMethods.GetWindowThreadProcessId(shellWindow, out var shellProcessId) == 0 || shellProcessId == 0)
            {
                throw new InvalidOperationException("Unable to determine the shell process identifier.");
            }

            using var shellProcess = Process.GetProcessById((int)shellProcessId);
            if (!NativeMethods.OpenProcessToken(shellProcess.Handle, TokenAllAccess, out var shellToken))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to open the shell access token.");
            }

            try
            {
                if (!NativeMethods.DuplicateTokenEx(shellToken, TokenAllAccess, IntPtr.Zero, NativeMethods.SECURITY_IMPERSONATION_LEVEL.SecurityImpersonation, NativeMethods.TOKEN_TYPE.TokenPrimary, out var primaryToken))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to duplicate the shell token.");
                }

                try
                {
                    var startupInfo = new NativeMethods.STARTUPINFO();
                    startupInfo.cb = Marshal.SizeOf<NativeMethods.STARTUPINFO>();

                    var processInfo = new NativeMethods.PROCESS_INFORMATION();
                    var commandLine = new StringBuilder(string.IsNullOrEmpty(commandLineText) ? QuoteArgument(ResolveProcessPath() ?? string.Empty) : commandLineText);

                    if (!NativeMethods.CreateProcessWithTokenW(primaryToken, 0, null, commandLine, 0, IntPtr.Zero, workingDirectory, ref startupInfo, out processInfo))
                    {
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to launch the process in user mode.");
                    }

                    NativeMethods.CloseHandle(processInfo.hProcess);
                    NativeMethods.CloseHandle(processInfo.hThread);
                }
                finally
                {
                    NativeMethods.CloseHandle(primaryToken);
                }
            }
            finally
            {
                NativeMethods.CloseHandle(shellToken);
            }
        }
    }

    private static class NativeMethods
    {
        public const uint TOKEN_ASSIGN_PRIMARY = 0x0001;
        public const uint TOKEN_DUPLICATE = 0x0002;
        public const uint TOKEN_QUERY = 0x0008;
        public const uint TOKEN_ADJUST_DEFAULT = 0x0080;
        public const uint TOKEN_ADJUST_SESSIONID = 0x0100;

        [StructLayout(LayoutKind.Sequential)]
        public struct STARTUPINFO
        {
            public int cb;
            public string? lpReserved;
            public string? lpDesktop;
            public string? lpTitle;
            public int dwX;
            public int dwY;
            public int dwXSize;
            public int dwYSize;
            public int dwXCountChars;
            public int dwYCountChars;
            public int dwFillAttribute;
            public int dwFlags;
            public short wShowWindow;
            public short cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput;
            public IntPtr hStdOutput;
            public IntPtr hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct PROCESS_INFORMATION
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public int dwProcessId;
            public int dwThreadId;
        }

        public enum SECURITY_IMPERSONATION_LEVEL
        {
            SecurityAnonymous,
            SecurityIdentification,
            SecurityImpersonation,
            SecurityDelegation
        }

        public enum TOKEN_TYPE
        {
            TokenPrimary = 1,
            TokenImpersonation
        }

        [DllImport("user32.dll")]
        public static extern IntPtr GetShellWindow();

        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool DuplicateTokenEx(IntPtr hExistingToken, uint dwDesiredAccess, IntPtr lpTokenAttributes, SECURITY_IMPERSONATION_LEVEL ImpersonationLevel, TOKEN_TYPE TokenType, out IntPtr phNewToken);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool CreateProcessWithTokenW(IntPtr hToken, uint dwLogonFlags, string? lpApplicationName, StringBuilder lpCommandLine, uint dwCreationFlags, IntPtr lpEnvironment, string? lpCurrentDirectory, ref STARTUPINFO lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr hObject);
    }
}

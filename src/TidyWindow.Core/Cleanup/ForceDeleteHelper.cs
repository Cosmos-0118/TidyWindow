using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;

namespace TidyWindow.Core.Cleanup;

/// <summary>
/// Provides aggressive file deletion capabilities using multiple Windows APIs.
/// This is the nuclear option for stubborn files.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class ForceDeleteHelper
{
    /// <summary>
    /// Attempts to forcefully delete a file or directory using all available methods.
    /// This is the most aggressive deletion approach.
    /// </summary>
    /// <returns>True if the item was deleted or no longer exists.</returns>
    public static bool TryAggressiveDelete(string path, bool isDirectory, CancellationToken cancellationToken, out Exception? failure)
    {
        failure = null;

        if (string.IsNullOrWhiteSpace(path))
        {
            return true;
        }

        // Check if already gone
        if (!PathExists(path, isDirectory))
        {
            return true;
        }

        // Step 1: Clear all restrictive attributes
        TryClearAllAttributes(path, isDirectory);

        // Step 2: Try basic delete first
        if (TryBasicDelete(path, isDirectory, out failure))
        {
            return true;
        }

        // Step 3: Try taking ownership and granting full control
        if (TryTakeOwnershipAndDelete(path, isDirectory, out failure))
        {
            return true;
        }

        // Step 4: Try closing handles using Restart Manager
        if (TryCloseHandlesAndDelete(path, isDirectory, cancellationToken, out failure))
        {
            return true;
        }

        // Step 5: Try renaming to random name and deleting (bypass filename locks)
        if (TryRenameAndDelete(path, isDirectory, cancellationToken, out failure))
        {
            return true;
        }

        // Step 6: For directories, try depth-first aggressive cleanup
        if (isDirectory && TryAggressiveDirectoryPurge(path, cancellationToken, out failure))
        {
            return true;
        }

        return false;
    }

    private static bool PathExists(string path, bool isDirectory)
    {
        return isDirectory ? Directory.Exists(path) : File.Exists(path);
    }

    private static void TryClearAllAttributes(string path, bool isDirectory)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            var cleared = attributes & ~(FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System | FileAttributes.Archive);
            if (cleared == 0)
            {
                cleared = FileAttributes.Normal;
            }

            if (cleared != attributes)
            {
                File.SetAttributes(path, cleared);
            }

            // Also clear attributes on children for directories
            if (isDirectory && Directory.Exists(path))
            {
                try
                {
                    foreach (var entry in Directory.EnumerateFileSystemEntries(path, "*", new EnumerationOptions
                    {
                        RecurseSubdirectories = true,
                        IgnoreInaccessible = true,
                        AttributesToSkip = FileAttributes.None
                    }))
                    {
                        try
                        {
                            var entryAttrs = File.GetAttributes(entry);
                            var entryCleared = entryAttrs & ~(FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System);
                            if (entryCleared == 0)
                            {
                                entryCleared = FileAttributes.Normal;
                            }

                            if (entryCleared != entryAttrs)
                            {
                                File.SetAttributes(entry, entryCleared);
                            }
                        }
                        catch
                        {
                            // Continue with other entries
                        }
                    }
                }
                catch
                {
                    // Ignore enumeration errors
                }
            }
        }
        catch
        {
            // Ignore attribute errors
        }
    }

    private static bool TryBasicDelete(string path, bool isDirectory, out Exception? failure)
    {
        failure = null;

        try
        {
            if (isDirectory)
            {
                if (!Directory.Exists(path))
                {
                    return true;
                }

                Directory.Delete(path, recursive: true);
            }
            else
            {
                if (!File.Exists(path))
                {
                    return true;
                }

                File.Delete(path);
            }

            return !PathExists(path, isDirectory);
        }
        catch (Exception ex)
        {
            failure = ex;
            return false;
        }
    }

    private static bool TryTakeOwnershipAndDelete(string path, bool isDirectory, out Exception? failure)
    {
        failure = null;

        try
        {
            // Get current user's identity
            var currentUser = WindowsIdentity.GetCurrent();
            var userSid = currentUser.User;
            if (userSid == null)
            {
                return false;
            }

            if (isDirectory)
            {
                var dirInfo = new DirectoryInfo(path);
                if (!dirInfo.Exists)
                {
                    return true;
                }

                var security = dirInfo.GetAccessControl();

                // Take ownership
                security.SetOwner(userSid);

                // Grant full control
                security.AddAccessRule(new FileSystemAccessRule(
                    userSid,
                    FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None,
                    AccessControlType.Allow));

                dirInfo.SetAccessControl(security);

                // Apply to children recursively
                ApplyPermissionsRecursively(path, userSid);
            }
            else
            {
                var fileInfo = new FileInfo(path);
                if (!fileInfo.Exists)
                {
                    return true;
                }

                var security = fileInfo.GetAccessControl();

                // Take ownership
                security.SetOwner(userSid);

                // Grant full control
                security.AddAccessRule(new FileSystemAccessRule(
                    userSid,
                    FileSystemRights.FullControl,
                    AccessControlType.Allow));

                fileInfo.SetAccessControl(security);
            }

            // Now try to delete again
            return TryBasicDelete(path, isDirectory, out failure);
        }
        catch (Exception ex)
        {
            failure = ex;
            return false;
        }
    }

    private static void ApplyPermissionsRecursively(string directoryPath, SecurityIdentifier userSid)
    {
        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directoryPath, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.None
            }))
            {
                try
                {
                    if (Directory.Exists(entry))
                    {
                        var dirInfo = new DirectoryInfo(entry);
                        var security = dirInfo.GetAccessControl();
                        security.SetOwner(userSid);
                        security.AddAccessRule(new FileSystemAccessRule(
                            userSid,
                            FileSystemRights.FullControl,
                            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                            PropagationFlags.None,
                            AccessControlType.Allow));
                        dirInfo.SetAccessControl(security);
                    }
                    else if (File.Exists(entry))
                    {
                        var fileInfo = new FileInfo(entry);
                        var security = fileInfo.GetAccessControl();
                        security.SetOwner(userSid);
                        security.AddAccessRule(new FileSystemAccessRule(
                            userSid,
                            FileSystemRights.FullControl,
                            AccessControlType.Allow));
                        fileInfo.SetAccessControl(security);
                    }
                }
                catch
                {
                    // Continue with other entries
                }
            }
        }
        catch
        {
            // Ignore enumeration errors
        }
    }

    private static bool TryCloseHandlesAndDelete(string path, bool isDirectory, CancellationToken cancellationToken, out Exception? failure)
    {
        failure = null;

        try
        {
            // Use Windows Restart Manager to close file handles
            if (!TryCloseFileHandles(path))
            {
                return false;
            }

            // Give processes a moment to release
            Thread.Sleep(100);
            cancellationToken.ThrowIfCancellationRequested();

            return TryBasicDelete(path, isDirectory, out failure);
        }
        catch (Exception ex)
        {
            failure = ex;
            return false;
        }
    }

    private static bool TryCloseFileHandles(string path)
    {
        try
        {
            // Use Restart Manager to find and close processes with handles to this file
            var result = RestartManagerNative.RmStartSession(out var sessionHandle, 0, Guid.NewGuid().ToString("N"));
            if (result != 0)
            {
                return false;
            }

            try
            {
                var resources = new[] { path };
                result = RestartManagerNative.RmRegisterResources(sessionHandle, (uint)resources.Length, resources, 0, null, 0, null);
                if (result != 0)
                {
                    return false;
                }

                uint processCount = 0;
                uint neededSize = 0;
                uint reason = 0;

                // Get the list of processes
                result = RestartManagerNative.RmGetList(sessionHandle, out neededSize, ref processCount, null, ref reason);
                if (result == RestartManagerNative.ERROR_MORE_DATA && neededSize > 0)
                {
                    var processes = new RestartManagerNative.RM_PROCESS_INFO[neededSize];
                    processCount = neededSize;
                    result = RestartManagerNative.RmGetList(sessionHandle, out neededSize, ref processCount, processes, ref reason);

                    if (result == 0 && processCount > 0)
                    {
                        // Try to terminate each process holding a handle
                        foreach (var processInfo in processes)
                        {
                            if (processInfo.Process.dwProcessId == 0)
                            {
                                continue;
                            }

                            try
                            {
                                using var process = Process.GetProcessById((int)processInfo.Process.dwProcessId);
                                // Don't kill critical system processes
                                var processName = process.ProcessName.ToLowerInvariant();
                                if (processName is "system" or "csrss" or "smss" or "wininit" or "services" or "lsass" or "svchost")
                                {
                                    continue;
                                }

                                // Try graceful shutdown first
                                if (process.CloseMainWindow())
                                {
                                    process.WaitForExit(2000);
                                }

                                // If still running, force terminate
                                if (!process.HasExited)
                                {
                                    process.Kill();
                                    process.WaitForExit(1000);
                                }
                            }
                            catch
                            {
                                // Process may have already exited
                            }
                        }

                        return true;
                    }
                }

                return false;
            }
            finally
            {
                RestartManagerNative.RmEndSession(sessionHandle);
            }
        }
        catch
        {
            return false;
        }
    }

    private static bool TryRenameAndDelete(string path, bool isDirectory, CancellationToken cancellationToken, out Exception? failure)
    {
        failure = null;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            var trimmed = Path.TrimEndingDirectorySeparator(path);
            var parent = Path.GetDirectoryName(trimmed);
            if (string.IsNullOrWhiteSpace(parent))
            {
                return false;
            }

            // Generate a random tombstone name
            var tombstone = Path.Combine(parent, $".tidywindow-force-{Guid.NewGuid():N}");

            if (isDirectory)
            {
                Directory.Move(path, tombstone);
            }
            else
            {
                File.Move(path, tombstone);
            }

            // Original path is now clear, try to delete the tombstone
            if (TryBasicDelete(tombstone, isDirectory, out failure))
            {
                return true;
            }

            // At minimum, the original path is now cleared
            // Schedule tombstone for reboot cleanup
            if (OperatingSystem.IsWindows())
            {
                TryScheduleDeleteOnReboot(tombstone);
            }

            // Return true if the original path no longer exists
            return !PathExists(path, isDirectory);
        }
        catch (Exception ex)
        {
            failure = ex;
            return false;
        }
    }

    private static bool TryAggressiveDirectoryPurge(string path, CancellationToken cancellationToken, out Exception? failure)
    {
        failure = null;

        if (!Directory.Exists(path))
        {
            return true;
        }

        try
        {
            // Collect all entries depth-first (files and directories)
            var entries = new List<string>();
            var stack = new Stack<string>();
            stack.Push(path);

            while (stack.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var current = stack.Pop();

                try
                {
                    // Add files first
                    foreach (var file in Directory.GetFiles(current))
                    {
                        entries.Add(file);
                    }

                    // Then push directories for later processing
                    foreach (var dir in Directory.GetDirectories(current))
                    {
                        stack.Push(dir);
                    }

                    // Add the directory itself after its contents
                    if (current != path)
                    {
                        entries.Add(current);
                    }
                }
                catch
                {
                    // Continue with other paths
                }
            }

            // Process in reverse order (deepest first)
            entries.Reverse();

            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var isDir = Directory.Exists(entry);
                TryClearAllAttributes(entry, isDir);

                try
                {
                    if (isDir)
                    {
                        Directory.Delete(entry, recursive: false);
                    }
                    else
                    {
                        File.Delete(entry);
                    }
                }
                catch
                {
                    // Try take ownership on individual item
                    try
                    {
                        var currentUser = WindowsIdentity.GetCurrent();
                        var userSid = currentUser.User;
                        if (userSid != null)
                        {
                            if (isDir)
                            {
                                var dirInfo = new DirectoryInfo(entry);
                                var security = dirInfo.GetAccessControl();
                                security.SetOwner(userSid);
                                security.AddAccessRule(new FileSystemAccessRule(userSid, FileSystemRights.FullControl, AccessControlType.Allow));
                                dirInfo.SetAccessControl(security);
                                Directory.Delete(entry, recursive: false);
                            }
                            else
                            {
                                var fileInfo = new FileInfo(entry);
                                var security = fileInfo.GetAccessControl();
                                security.SetOwner(userSid);
                                security.AddAccessRule(new FileSystemAccessRule(userSid, FileSystemRights.FullControl, AccessControlType.Allow));
                                fileInfo.SetAccessControl(security);
                                File.Delete(entry);
                            }
                        }
                    }
                    catch
                    {
                        // Schedule for reboot deletion as last resort
                        if (OperatingSystem.IsWindows())
                        {
                            TryScheduleDeleteOnReboot(entry);
                        }
                    }
                }
            }

            // Now try to delete the root directory
            if (TryBasicDelete(path, isDirectory: true, out failure))
            {
                return true;
            }

            // Check if effectively empty
            try
            {
                var remaining = Directory.GetFileSystemEntries(path);
                if (remaining.Length == 0)
                {
                    Directory.Delete(path);
                    return true;
                }
            }
            catch
            {
                // Ignore
            }

            return !Directory.Exists(path);
        }
        catch (Exception ex)
        {
            failure = ex;
            return false;
        }
    }

    private static bool TryScheduleDeleteOnReboot(string path)
    {
        try
        {
            return MoveFileEx(path, null, MoveFileFlags.MOVEFILE_DELAY_UNTIL_REBOOT);
        }
        catch
        {
            return false;
        }
    }

    [Flags]
    private enum MoveFileFlags : uint
    {
        MOVEFILE_REPLACE_EXISTING = 0x1,
        MOVEFILE_DELAY_UNTIL_REBOOT = 0x4
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool MoveFileEx(string lpExistingFileName, string? lpNewFileName, MoveFileFlags dwFlags);

    /// <summary>
    /// Windows Restart Manager native methods for detecting and closing file handles.
    /// </summary>
    private static class RestartManagerNative
    {
        public const int ERROR_MORE_DATA = 234;

        [StructLayout(LayoutKind.Sequential)]
        public struct RM_UNIQUE_PROCESS
        {
            public uint dwProcessId;
            public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct RM_PROCESS_INFO
        {
            public RM_UNIQUE_PROCESS Process;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string strAppName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string strServiceShortName;
            public uint ApplicationType;
            public uint AppStatus;
            public uint TSSessionId;
            [MarshalAs(UnmanagedType.Bool)]
            public bool bRestartable;
        }

        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
        public static extern int RmStartSession(out uint pSessionHandle, int dwSessionFlags, string strSessionKey);

        [DllImport("rstrtmgr.dll")]
        public static extern int RmEndSession(uint pSessionHandle);

        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
        public static extern int RmRegisterResources(
            uint pSessionHandle,
            uint nFiles,
            string[]? rgsFilenames,
            uint nApplications,
            RM_UNIQUE_PROCESS[]? rgApplications,
            uint nServices,
            string[]? rgsServiceNames);

        [DllImport("rstrtmgr.dll")]
        public static extern int RmGetList(
            uint dwSessionHandle,
            out uint pnProcInfoNeeded,
            ref uint pnProcInfo,
            [In, Out] RM_PROCESS_INFO[]? rgAffectedApps,
            ref uint lpdwRebootReasons);
    }
}

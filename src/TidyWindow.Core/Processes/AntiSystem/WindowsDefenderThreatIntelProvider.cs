using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace TidyWindow.Core.Processes.AntiSystem;

/// <summary>
/// Invokes MpCmdRun.exe to obtain a quick Defender verdict before falling back to remote services.
/// </summary>
public sealed class WindowsDefenderThreatIntelProvider : IThreatIntelProvider
{
    private static readonly IReadOnlyList<int> SuspiciousExitCodes = new[] { 2, 3, 4, 6 };
    private readonly string? _mpCmdRunPath;

    public WindowsDefenderThreatIntelProvider()
    {
        _mpCmdRunPath = LocateExecutable();
    }

    public ThreatIntelProviderKind Kind => ThreatIntelProviderKind.OperatingSystem;

    public async ValueTask<ThreatIntelResult> EvaluateAsync(string filePath, string? sha256, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(_mpCmdRunPath))
        {
            return ThreatIntelResult.Unknown(sha256);
        }

        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return ThreatIntelResult.Unknown(sha256);
        }

        try
        {
            var info = new ProcessStartInfo
            {
                FileName = _mpCmdRunPath,
                Arguments = $"-Scan -ScanType 3 -DisableRemediation -File \"{filePath}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = Process.Start(info);
            if (process is null)
            {
                return ThreatIntelResult.Unknown(sha256);
            }

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var exitCode = process.ExitCode;
            if (SuspiciousExitCodes.Contains(exitCode))
            {
                return ThreatIntelResult.KnownBad(sha256, "windows-defender", $"MpCmdRun exited with code {exitCode}");
            }

            return ThreatIntelResult.Unknown(sha256);
        }
        catch
        {
            return ThreatIntelResult.Unknown(sha256);
        }
    }

    private static string? LocateExecutable()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var candidates = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void AddCandidate(string? path)
        {
            if (!string.IsNullOrWhiteSpace(path) && seen.Add(path))
            {
                candidates.Add(path);
            }
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        AddCandidate(Combine(programFiles, "Windows Defender", "MpCmdRun.exe"));
        AddCandidate(Combine(programFiles, "Microsoft Defender", "MpCmdRun.exe"));
        AddCandidate(Combine(programFilesX86, "Windows Defender", "MpCmdRun.exe"));
        AddCandidate(Combine(programFilesX86, "Microsoft Defender", "MpCmdRun.exe"));

        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var platformRoot = Combine(programData, "Microsoft", "Windows Defender", "Platform");
        if (!string.IsNullOrWhiteSpace(platformRoot) && Directory.Exists(platformRoot))
        {
            try
            {
                foreach (var directory in new DirectoryInfo(platformRoot).GetDirectories())
                {
                    AddCandidate(Path.Combine(directory.FullName, "MpCmdRun.exe"));
                }
            }
            catch
            {
                // Ignore IO errors and continue with other candidates.
            }
        }

        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string? Combine(string? root, params string[] segments)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return null;
        }

        return Path.Combine(root, Path.Combine(segments));
    }
}

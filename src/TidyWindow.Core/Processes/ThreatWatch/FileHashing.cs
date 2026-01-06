using System;
using System.IO;
using System.Security.Cryptography;

namespace TidyWindow.Core.Processes.ThreatWatch;

internal static class FileHashing
{
    public static string? TryComputeSha256(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return null;
        }

        try
        {
            if (!File.Exists(filePath))
            {
                return null;
            }

            using var stream = File.OpenRead(filePath);
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(stream);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
        catch
        {
            return null;
        }
    }
}

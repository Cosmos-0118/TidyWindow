using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace TidyWindow.Core.Backup;

/// <summary>
/// Discovers user profiles and installed applications for Reset Rescue backup selection.
/// Best-effort and Windows-only; returns empty results on unsupported platforms.
/// </summary>
public sealed class InventoryService
{
    private static readonly string[] UninstallRoots = new[]
    {
        "HKEY_LOCAL_MACHINE\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall",
        "HKEY_LOCAL_MACHINE\\SOFTWARE\\WOW6432Node\\Microsoft\\Windows\\CurrentVersion\\Uninstall",
        "HKEY_CURRENT_USER\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall"
    };

    public Task<IReadOnlyList<BackupProfile>> DiscoverProfilesAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Task.FromResult<IReadOnlyList<BackupProfile>>(Array.Empty<BackupProfile>());
        }

        return Task.Run(() => DiscoverProfilesInternal(cancellationToken), cancellationToken);
    }

    public Task<IReadOnlyList<BackupApp>> DiscoverAppsAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Task.FromResult<IReadOnlyList<BackupApp>>(Array.Empty<BackupApp>());
        }

        return Task.Run(() => DiscoverAppsInternal(cancellationToken), cancellationToken);
    }

    private static IReadOnlyList<BackupProfile> DiscoverProfilesInternal(CancellationToken cancellationToken)
    {
        var results = new List<BackupProfile>();
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey("SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\ProfileList");
            if (key is null)
            {
                return results;
            }

            foreach (var sid in key.GetSubKeyNames())
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var profileKey = key.OpenSubKey(sid);
                if (profileKey is null)
                {
                    continue;
                }

                var profilePath = profileKey.GetValue("ProfileImagePath") as string;
                if (string.IsNullOrWhiteSpace(profilePath) || !Directory.Exists(profilePath))
                {
                    continue;
                }

                var accountName = TryTranslateSid(sid);
                var known = BuildKnownFolders(profilePath);
                results.Add(new BackupProfile
                {
                    Sid = sid,
                    Name = accountName,
                    Root = profilePath,
                    KnownFolders = known
                });
            }
        }
        catch (Exception)
        {
            // Best-effort; ignore registry or security failures.
        }

        return results;
    }

    private static IReadOnlyList<string> BuildKnownFolders(string profileRoot)
    {
        var known = new List<string>();
        void Add(string relative)
        {
            var path = Path.Combine(profileRoot, relative);
            if (Directory.Exists(path))
            {
                known.Add(path);
            }
        }

        Add("Desktop");
        Add("Documents");
        Add("Pictures");
        Add("Downloads");
        Add("Music");
        Add("Videos");
        Add(Path.Combine("AppData", "Roaming", "Microsoft", "Windows", "Start Menu", "Programs"));
        return known;
    }

    private static string TryTranslateSid(string sid)
    {
        try
        {
            var securityIdentifier = new SecurityIdentifier(sid);
            var account = (NTAccount?)securityIdentifier.Translate(typeof(NTAccount));
            return account?.Value ?? sid;
        }
        catch (Exception)
        {
            return sid;
        }
    }

    private static IReadOnlyList<BackupApp> DiscoverAppsInternal(CancellationToken cancellationToken)
    {
        var results = new List<BackupApp>();

        // Win32/MSI via uninstall registry
        foreach (var root in UninstallRoots)
        {
            foreach (var app in ReadUninstallRoot(root, cancellationToken))
            {
                results.Add(app);
            }
        }

        // Store packages (per-user)
        foreach (var app in DiscoverStorePackages())
        {
            results.Add(app);
        }

        // Portable heuristics (shallow scan for portable-style directories under user profile)
        foreach (var app in DiscoverPortableApps())
        {
            results.Add(app);
        }

        return results;
    }

    private static IEnumerable<BackupApp> ReadUninstallRoot(string root, CancellationToken cancellationToken)
    {
        using var baseKey = RegistryKey.OpenBaseKey(GetHive(root), RegistryView.Registry64);
        var subPath = GetSubPath(root);
        using var uninstall = baseKey.OpenSubKey(subPath);
        if (uninstall is null)
        {
            yield break;
        }

        foreach (var name in uninstall.GetSubKeyNames())
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var appKey = uninstall.OpenSubKey(name);
            if (appKey is null)
            {
                continue;
            }

            var displayName = appKey.GetValue("DisplayName") as string;
            if (string.IsNullOrWhiteSpace(displayName))
            {
                continue;
            }

            var version = appKey.GetValue("DisplayVersion") as string ?? string.Empty;
            var installLocation = appKey.GetValue("InstallLocation") as string;
            var registryKeys = new List<string> { $"{root}\\{name}" };

            var dataPaths = new List<string>();
            if (!string.IsNullOrWhiteSpace(installLocation) && Directory.Exists(installLocation))
            {
                dataPaths.Add(installLocation);
            }

            yield return new BackupApp
            {
                Id = name,
                Name = displayName,
                Type = "Win32",
                Version = version,
                InstallLocation = installLocation,
                DataPaths = dataPaths.ToArray(),
                RegistryKeys = registryKeys.ToArray()
            };
        }
    }

    private static RegistryHive GetHive(string root)
    {
        if (root.StartsWith("HKEY_CURRENT_USER", StringComparison.OrdinalIgnoreCase))
        {
            return RegistryHive.CurrentUser;
        }

        return RegistryHive.LocalMachine;
    }

    private static string GetSubPath(string root)
    {
        var index = root.IndexOf('\\');
        return index <= 0 ? string.Empty : root[(index + 2 - 1)..];
    }

    private static IEnumerable<BackupApp> DiscoverStorePackages()
    {
        var results = new List<BackupApp>();
        try
        {
            var packagesRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");
            if (!Directory.Exists(packagesRoot))
            {
                return results;
            }

            foreach (var dir in Directory.EnumerateDirectories(packagesRoot))
            {
                var name = Path.GetFileName(dir);
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                results.Add(new BackupApp
                {
                    Id = name,
                    Name = name,
                    Type = "Store",
                    Version = string.Empty,
                    InstallLocation = dir,
                    DataPaths = new[] { dir },
                    RegistryKeys = Array.Empty<string>()
                });
            }
        }
        catch (Exception)
        {
            // Ignore access issues and return what we have.
        }

        return results;
    }

    private static IEnumerable<BackupApp> DiscoverPortableApps()
    {
        var results = new List<BackupApp>();
        try
        {
            var roots = new List<string>
            {
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "PortableApps")
            };

            foreach (var root in roots.Where(Directory.Exists))
            {
                foreach (var dir in Directory.EnumerateDirectories(root))
                {
                    var exe = Directory.EnumerateFiles(dir, "*.exe", SearchOption.TopDirectoryOnly).FirstOrDefault();
                    if (exe is null)
                    {
                        continue;
                    }

                    var name = Path.GetFileName(dir);
                    results.Add(new BackupApp
                    {
                        Id = dir,
                        Name = name,
                        Type = "Portable",
                        Version = string.Empty,
                        InstallLocation = dir,
                        DataPaths = new[] { dir },
                        RegistryKeys = Array.Empty<string>()
                    });
                }
            }
        }
        catch (Exception)
        {
            // Ignore access or IO issues and return what we found.
        }

        return results;
    }
}

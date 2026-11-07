<#
.SYNOPSIS
  Stops Windows Search (and the Apps list in Start search) from listing this repo's build output.

.DESCRIPTION
  Documents and the rest of the user profile are indexed by default, and Start search shows every indexed .exe under Apps. Each build of
  this repo leaves TidyWindow.Next.exe, TidyWindow.CleanHost.exe, TidyWindow.SearchHost.exe and others under bin/, obj/ and artifacts/
  (and again under .claude/worktrees), so a search for "TidyWindow" lists dozens of copies beside the real, installed app.

  The "not content indexed" file attribute does not help: it only skips file contents, the names are still indexed and still listed.
  What works is a crawl scope rule that excludes the folder, the same thing Settings > Privacy & security > Searching Windows >
  Exclude folders does. This script adds those rules through the Windows Search API (ISearchCrawlScopeManager::AddUserScopeRule).

  It changes a per-user search setting, so it is a script you run yourself, once per clone. Nothing in the build runs it. Windows drops
  the old entries by itself within a few minutes after the rules are saved.

.PARAMETER Remove
  Takes the rules back out (reverts to Windows' default scope for these folders).

.PARAMETER Check
  Read-only: shows which of these folders Windows Search currently indexes.

.PARAMETER WhatIf
  Lists the folders it would exclude and changes nothing.

.EXAMPLE
  pwsh scripts/exclude-build-output-from-search.ps1
#>
[CmdletBinding(SupportsShouldProcess)]
param([switch]$Remove, [switch]$Check)
$ErrorActionPreference = 'Stop'

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class WindowsSearchScope
{
    // Only the methods this script calls are named. The rest are placeholders so the vtable slots line up with searchapi.h.
    [ComImport, Guid("AB310581-AC80-11D1-8DF3-00C04FB6EF69"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ISearchManager
    {
        void GetIndexerVersionStr(); void GetIndexerVersion(); void GetParameter(); void SetParameter();
        void get_ProxyName(); void get_BypassList(); void SetProxy();
        [PreserveSig] int GetCatalog([MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.Interface)] out ISearchCatalogManager catalog);
    }

    [ComImport, Guid("AB310581-AC80-11D1-8DF3-00C04FB6EF50"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ISearchCatalogManager
    {
        void get_Name(); void GetParameter(); void SetParameter(); void GetCatalogStatus(); void Reset(); void Reindex();
        void ReindexMatchingURLs(); void ReindexSearchRoot(); void put_ConnectTimeout(); void get_ConnectTimeout();
        void put_DataTimeout(); void get_DataTimeout(); void NumberOfItems(); void NumberOfItemsToIndex(); void URLBeingIndexed();
        void GetURLIndexingState(); void GetPersistentItemsChangedSink(); void RegisterViewForNotification(); void GetItemsChangedSink();
        void UnregisterViewForNotification(); void SetExtensionClusion(); void EnumerateExcludedExtensions(); void GetQueryHelper();
        void put_DiacriticSensitivity(); void get_DiacriticSensitivity();
        [PreserveSig] int GetCrawlScopeManager([MarshalAs(UnmanagedType.Interface)] out ISearchCrawlScopeManager manager);
    }

    [ComImport, Guid("AB310581-AC80-11D1-8DF3-00C04FB6EF55"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ISearchCrawlScopeManager
    {
        void AddDefaultScopeRule(); void AddRoot(); void RemoveRoot(); void EnumerateRoots(); void AddHierarchicalScope();
        [PreserveSig] int AddUserScopeRule([MarshalAs(UnmanagedType.LPWStr)] string url, [MarshalAs(UnmanagedType.Bool)] bool include,
                                           [MarshalAs(UnmanagedType.Bool)] bool overrideChildren, uint followFlags);
        [PreserveSig] int RemoveScopeRule([MarshalAs(UnmanagedType.LPWStr)] string url);
        void EnumerateScopeRules(); void HasParentScopeRule(); void HasChildScopeRule();
        [PreserveSig] int IncludedInCrawlScope([MarshalAs(UnmanagedType.LPWStr)] string url, [MarshalAs(UnmanagedType.Bool)] out bool included);
        void IncludedInCrawlScopeEx(); void RevertToDefaultScopes();
        [PreserveSig] int SaveAll();
    }

    static ISearchCrawlScopeManager Open()
    {
        var manager = (ISearchManager)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("7D096C5F-AC08-4F1F-BEB7-5C22C517CE39")));
        ISearchCatalogManager catalog;
        Marshal.ThrowExceptionForHR(manager.GetCatalog("SystemIndex", out catalog));
        ISearchCrawlScopeManager scope;
        Marshal.ThrowExceptionForHR(catalog.GetCrawlScopeManager(out scope));
        return scope;
    }

    // The Windows Search URL form of a folder; an exclusion must end in "*" so it covers everything beneath.
    static string ScopeUrl(string folder) { return "file:///" + folder.TrimEnd('\\') + "\\*"; }

    public static bool IsIncluded(string folder)
    {
        bool included;
        Marshal.ThrowExceptionForHR(Open().IncludedInCrawlScope("file:///" + folder.TrimEnd('\\') + "\\", out included));
        return included;
    }

    public static void Apply(string[] folders, bool exclude)
    {
        var scope = Open();
        foreach (var folder in folders)
        {
            // FF_INDEXCOMPLEXURLS (1): the value the Settings page itself uses for file:// rules.
            int hr = exclude ? scope.AddUserScopeRule(ScopeUrl(folder), false, false, 1) : scope.RemoveScopeRule(ScopeUrl(folder));
            // Removing a rule that is not there is not an error worth stopping for.
            if (hr < 0 && exclude) Marshal.ThrowExceptionForHR(hr);
        }
        Marshal.ThrowExceptionForHR(scope.SaveAll());
    }
}
'@

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

# The outermost build-output folders only: a rule on artifacts/ already covers everything beneath it. Worktrees under .claude/ are
# separate checkouts with their own output, so each of their folders gets a rule too.
$names = 'bin', 'obj', 'artifacts', 'TestResults'
$folders = New-Object System.Collections.Generic.List[string]
function Find-Output([string]$dir) {
    foreach ($child in Get-ChildItem $dir -Directory -Force -ErrorAction SilentlyContinue) {
        if ($child.Name -in '.git', 'node_modules') { continue }
        if ($child.Name -in $names) { $folders.Add($child.FullName); continue }   # do not descend: the rule covers the children
        Find-Output $child.FullName
    }
}
Find-Output $root

if (-not $folders.Count) { Write-Host 'No build-output folders found; nothing to do.'; return }

if ($Check) {
    # Read-only: says whether Windows Search currently indexes each folder.
    foreach ($f in $folders) { '{0,-9} {1}' -f $(if ([WindowsSearchScope]::IsIncluded($f)) { 'indexed' } else { 'excluded' }), $f }
    return
}

if ($PSCmdlet.ShouldProcess("$($folders.Count) folders under $root", $(if ($Remove) { 'include in the search index again' } else { 'exclude from the search index' }))) {
    [WindowsSearchScope]::Apply($folders.ToArray(), -not $Remove)
    $verb = if ($Remove) { 'Restored' } else { 'Excluded' }
    Write-Host "$verb $($folders.Count) folders. Windows removes the old results within a few minutes."
}
else {
    $folders | ForEach-Object { Write-Host "would $(if ($Remove) { 'include' } else { 'exclude' }): $_" }
}

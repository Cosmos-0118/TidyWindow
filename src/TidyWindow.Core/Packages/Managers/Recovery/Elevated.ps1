# Goes first, before Common.ps1, in every script the app starts through the Windows permission prompt. With administrator rights nothing may be
# loaded from a folder an ordinary account can write, so modules come only from Windows' own folders, none is loaded by name on first use, and the
# ones needed are imported here by full path; downloads, unpacking and execution then happen in a folder only administrators can change.
$ErrorActionPreference = 'Stop'
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { exit 5 }
$env:PSModulePath = "$PSHOME\Modules;" + (Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'WindowsPowerShell\Modules')
$PSModuleAutoLoadingPreference = 'None'
foreach ($name in 'Microsoft.PowerShell.Utility', 'Microsoft.PowerShell.Management', 'Microsoft.PowerShell.Security', 'Microsoft.PowerShell.Host') { Import-Module "$PSHOME\Modules\$name\$name.psd1" }
$hardened = $true
$sidAdministrators = [Security.Principal.SecurityIdentifier]::new([Security.Principal.WellKnownSidType]::BuiltinAdministratorsSid, $null)
$sidSystem = [Security.Principal.SecurityIdentifier]::new([Security.Principal.WellKnownSidType]::LocalSystemSid, $null)
function Test-ProtectedFolder([string]$path) {
    # Administrators own it, it takes no permissions from its parent, and nobody else may write to it; a link or a file in its place does not count.
    $item = Get-Item -LiteralPath $path -Force
    if (-not $item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { return $false }
    $acl = Get-Acl -LiteralPath $path
    $owner = $acl.GetOwner([Security.Principal.SecurityIdentifier]).Value
    if ($owner -ne $sidAdministrators.Value -and $owner -ne $sidSystem.Value) { return $false }
    if (-not $acl.AreAccessRulesProtected) { return $false }
    $writes = [Security.AccessControl.FileSystemRights]'WriteData, AppendData, Delete, DeleteSubdirectoriesAndFiles, WriteAttributes, WriteExtendedAttributes, ChangePermissions, TakeOwnership'
    foreach ($rule in $acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier])) {
        if ($rule.AccessControlType -ne 'Allow') { continue }
        $who = $rule.IdentityReference.Value
        if ($who -ne $sidAdministrators.Value -and $who -ne $sidSystem.Value -and ($rule.FileSystemRights -band $writes) -ne 0) { return $false }
    }
    return $true
}
function New-ProtectedFolder([string]$path) {
    # Made with its permissions in one step, so there is no moment when the folder exists with permissions anyone could use.
    $security = [Security.AccessControl.DirectorySecurity]::new()
    $security.SetAccessRuleProtection($true, $false)
    $users = [Security.Principal.SecurityIdentifier]::new([Security.Principal.WellKnownSidType]::BuiltinUsersSid, $null)
    foreach ($grant in @(@($sidAdministrators, 'FullControl'), @($sidSystem, 'FullControl'), @($users, 'ReadAndExecute'))) {
        $security.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($grant[0], [Security.AccessControl.FileSystemRights]$grant[1], [Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit', [Security.AccessControl.PropagationFlags]::None, [Security.AccessControl.AccessControlType]::Allow))
    }
    $security.SetOwner($sidAdministrators)
    $info = [IO.DirectoryInfo]::new($path)
    if ($PSVersionTable.PSVersion.Major -ge 6) { [IO.FileSystemAclExtensions]::Create($info, $security) } else { $info.Create($security) }
    if (-not (Test-ProtectedFolder $path)) { throw "The working folder $path could not be given administrator-only permissions." }
}
function Use-ProtectedWorkspace() {
    # An administrator-only working folder under ProgramData, named after the job. Anything in the way that administrators do not own is moved aside, never used.
    $path = [Environment]::GetFolderPath('CommonApplicationData')
    foreach ($part in 'TidyWindow.Next', 'Recovery', (Split-Path -Leaf $job)) {
        $path = Join-Path $path $part
        if (Test-Path -LiteralPath $path) {
            if (Test-ProtectedFolder $path) { continue }
            [IO.Directory]::Move($path, $path + '.untrusted-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
        }
        New-ProtectedFolder $path
    }
    return $path
}

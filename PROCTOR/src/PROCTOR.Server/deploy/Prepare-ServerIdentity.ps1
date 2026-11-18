<#
.SYNOPSIS
    Run by the PROCTOR Teacher setup (as SYSTEM, before the classroom service starts): keeps an
    existing server identity usable by the service, or sets aside one that can't be trusted.

.DESCRIPTION
    The service (LocalSystem) only uses %ProgramData%\PROCTOR\Server\server-certificate.pfx when it is
    owned by SYSTEM or Administrators and nobody else can write it. An identity created earlier by
    running the server from Visual Studio, or copied from another PC to keep its fingerprint, is
    owned by that user instead. This script:

      * adopts it (owner Administrators; exactly SYSTEM + Administrators, every other entry
        removed) when its owner is SYSTEM, Administrators or a member of the local Administrators
        group, and no account outside those can write it, so the fingerprint every agent pins
        stays the same;
      * otherwise renames it to *.untrusted-<date> (never deleted, never trusted), so the service
        creates a new identity and starts; agents pinned to the old key must then be reinstalled
        with a setup carrying the new fingerprint.

    Never blesses a file of unknown origin: a standard user who planted a key there could otherwise
    impersonate the classroom server to every student PC.
#>
param(
    # The server identity folder (a parameter only so the script can be tested on a copy).
    [string]$Dir = (Join-Path $env:ProgramData 'PROCTOR\Server')
)
$ErrorActionPreference = 'Stop'

$pfx = Join-Path $dir 'server-certificate.pfx'
if (-not (Test-Path -LiteralPath $pfx)) { exit 0 }   # first install: the service creates its own

$System = 'S-1-5-18'
$Admins = 'S-1-5-32-544'
$TrustedInstaller = 'S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464'
$Write = [Security.AccessControl.FileSystemRights]'WriteData,AppendData,Delete,DeleteSubdirectoriesAndFiles,ChangePermissions,TakeOwnership,WriteAttributes,WriteExtendedAttributes'

$adminMembers = @()
try { $adminMembers = @(Get-LocalGroupMember -SID $Admins -ErrorAction Stop | ForEach-Object { $_.SID.Value }) } catch { }
$okOwners = @($System, $Admins, $TrustedInstaller) + $adminMembers

function Get-Problem([string]$path) {
    $acl = Get-Acl -LiteralPath $path
    $owner = $acl.GetOwner([Security.Principal.SecurityIdentifier]).Value
    if ($okOwners -notcontains $owner) { return "owned by $($acl.Owner)" }
    foreach ($ace in $acl.Access) {
        if ($ace.AccessControlType -ne 'Allow' -or ($ace.FileSystemRights -band $Write) -eq 0) { continue }
        $sid = try { $ace.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value } catch { $ace.IdentityReference.Value }
        if ($sid -eq 'S-1-3-0') { continue }                                   # CREATOR OWNER
        if ($sid -eq $System -or $sid -eq $Admins -or $sid -eq $TrustedInstaller -or $sid -eq $owner) { continue }
        # An administrator (e.g. the teacher who ran the server from Visual Studio) could take this
        # file over anyway, so their write access is no sign of a planted key.
        if ($adminMembers -contains $sid) { continue }
        return "writable by $($ace.IdentityReference)"
    }
    return $null
}

$stamp = Get-Date -Format yyyyMMdd-HHmmss
$ReparsePoint = [IO.FileAttributes]::ReparsePoint
function Test-Link([IO.FileSystemInfo]$item) { ($item.Attributes -band $ReparsePoint) -ne 0 }

# This runs as SYSTEM: never follow a link (junction / symbolic link) that someone placed here, or
# the permission changes below would land wherever it points (e.g. C:\Windows\System32).
$parent = Get-Item -LiteralPath (Split-Path $dir -Parent) -Force
if (Test-Link $parent) {
    Write-Output "Left $dir alone: its parent folder is a link. The service decides whether to trust it."
    exit 0
}
$root = Get-Item -LiteralPath $dir -Force

function Set-Aside([string]$why) {
    # Renames the folder (or the link itself, never its target). Nothing in it (key, enrolled-PC
    # list) is trusted again, and the service creates a new identity.
    Rename-Item -LiteralPath $dir -NewName "Server.untrusted-$stamp"
    Write-Output "Set aside an untrusted server folder ($why). The service creates a new identity."
    exit 0
}

if (Test-Link $root) { Set-Aside 'it is a link' }
$dirProblem = Get-Problem $dir
if ($dirProblem) { Set-Aside $dirProblem }

# Everything inside, found without entering links (Get-ChildItem -Recurse in Windows PowerShell
# follows them). A link, or anything another account could have written, means the folder can't
# be trusted as a whole.
$items = New-Object 'Collections.Generic.List[IO.FileSystemInfo]'
$pending = New-Object 'Collections.Generic.Stack[IO.DirectoryInfo]'
$pending.Push($root)
while ($pending.Count -gt 0) {
    # GetFileSystemInfos (not Enumerate...): reads the whole listing and closes the folder, so a
    # Set-Aside from inside this loop can still rename it.
    foreach ($child in $pending.Pop().GetFileSystemInfos()) {
        if (Test-Link $child) { Set-Aside "it contains a link: $($child.Name)" }
        $items.Add($child)
        if ($child -is [IO.DirectoryInfo]) { $pending.Push($child) }
    }
}
foreach ($item in $items) {
    $problem = Get-Problem $item.FullName
    if (-not $problem) { continue }
    if ($item.FullName -eq $pfx) {
        $aside = "$pfx.untrusted-$stamp"
        Rename-Item -LiteralPath $pfx -NewName (Split-Path $aside -Leaf)
        Write-Output "Set aside an untrusted server identity ($problem): $aside. The service creates a new one."
        exit 0
    }
    Set-Aside "$($item.Name) is $problem"
}

# Adopt: owner Administrators; SYSTEM (the service) and Administrators only, every other entry
# removed (the service refuses a key any other account can write, even an administrator's own
# user account). Users must never read the private key. Item by item over the list above (no
# icacls /T, which would follow links).
function Set-AdminOnly([IO.FileSystemInfo]$item) {
    & icacls $item.FullName /setowner "*$Admins" /C /Q | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Couldn't make Administrators the owner of $($item.FullName) (icacls exit code $LASTEXITCODE)." }
    $isDir = $item -is [IO.DirectoryInfo]
    $acl = if ($isDir) { New-Object Security.AccessControl.DirectorySecurity } else { New-Object Security.AccessControl.FileSecurity }
    $acl.SetAccessRuleProtection($true, $false)   # no inherited entries
    $inherit = if ($isDir) { [Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit' } else { [Security.AccessControl.InheritanceFlags]::None }
    foreach ($sid in $System, $Admins) {
        $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule(
            (New-Object Security.Principal.SecurityIdentifier($sid)), 'FullControl', $inherit, 'None', 'Allow')))
    }
    $item.SetAccessControl($acl)   # writes the access list only; the owner set above stays
}
# Contents first (deepest first), the folder itself last: it stays listable until the end.
foreach ($item in ($items | Sort-Object { $_.FullName.Length } -Descending)) { Set-AdminOnly $item }
Set-AdminOnly $root

Write-Output "Kept the existing server identity (same fingerprint) for the PROCTOR service."
exit 0

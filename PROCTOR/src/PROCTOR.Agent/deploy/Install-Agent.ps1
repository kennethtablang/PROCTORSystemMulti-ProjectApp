<#
.SYNOPSIS
    Installs the PROCTOR student agent as an auto-start Windows service (FR-AGENT-02).

.DESCRIPTION
    Copies the agent into a protected program folder, locks its ACL so only administrators and
    SYSTEM can modify it, and registers PROCTOR.Agent.exe as a Windows service that starts
    automatically at boot - before any user logs in - so a managed lab PC joins the classroom
    with no student action and no per-machine conclaudefiguration (discovery finds the server). Also
    configures restart-on-failure so the agent recovers if it exits (FR-AGENT-07, within the lab
    trust model).

    At run time the service itself only supervises: it starts the same exe with --session-agent
    inside the signed-in student's console session (services can't see the user's desktop from
    session 0), as SYSTEM, and relaunches it if it exits or the console user changes.

    Run from an elevated (Administrator) PowerShell. Intended for IT to run directly, via GPO
    startup script, or baked into a machine image.

    SECURITY: the service runs as LocalSystem, so the folder it runs from MUST NOT be writable by
    standard users - otherwise a student could replace the exe and run arbitrary code as SYSTEM
    at boot. This script therefore refuses to leave the installed binary writable by non-admins.

.PARAMETER BinaryPath
    Full path to the SOURCE PROCTOR.Agent.exe to install. Defaults to a published 'agent'
    folder next to this script, then to the exe beside this script. The whole folder containing
    this exe is copied into -InstallDir, so point it at a clean published output.

.PARAMETER InstallDir
    Protected destination folder. Default: %ProgramFiles%\PROCTOR.

.PARAMETER ServiceName
    Windows service key name. Default: PROCTORAgent.

.EXAMPLE
    .\Install-Agent.ps1
.EXAMPLE
    .\Install-Agent.ps1 -BinaryPath 'C:\build\agent\PROCTOR.Agent.exe'
#>
[CmdletBinding()]
param(
    [string]$BinaryPath,
    [string]$InstallDir = (Join-Path $env:ProgramFiles 'PROCTOR'),
    [string]$ServiceName = 'PROCTORAgent',
    [string]$DisplayName = 'PROCTOR Agent'
)

$ErrorActionPreference = 'Stop'

# --- must be elevated -------------------------------------------------------
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'This script must be run as Administrator (needed to register a Windows service).'
}

# --- helpers ----------------------------------------------------------------
# Allowlist, not denylist: a SYSTEM service's install tree is safe only if the *only* principals
# that can write/delete/retake it are trusted system principals. Anything else -- a custom group,
# a specific user, Everyone -- is fatal. A denylist would fail open on SIDs it didn't enumerate.
$TrustedSids = @(
    'S-1-5-32-544',  # BUILTIN\Administrators
    'S-1-5-18',      # NT AUTHORITY\SYSTEM (LocalSystem)
    'S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464'  # NT SERVICE\TrustedInstaller
)
# Atomic mutate bits only. NOTE: do NOT use composite rights like Modify/FullControl here --
# those bundle read/execute bits, so AND-ing them would flag even a correct read-only grant.
$WriteMask = [System.Security.AccessControl.FileSystemRights]'WriteData,AppendData,WriteExtendedAttributes,WriteAttributes,Delete,DeleteSubdirectoriesAndFiles,ChangePermissions,TakeOwnership'

function ConvertTo-Sid {
    param($IdentityReference)
    try { return $IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value }
    catch { return $IdentityReference.Value }
}

# Returns a human-readable problem string if $Path is NOT securely owned/ACLed, else $null.
function Get-ItemSecurityProblem {
    param([Parameter(Mandatory)][string]$Path)
    $acl = Get-Acl -LiteralPath $Path

    $ownerSid = try { $acl.GetOwner([Security.Principal.SecurityIdentifier]).Value } catch { '' }
    if ($TrustedSids -notcontains $ownerSid) {
        return "owner is '$($acl.Owner)' ($ownerSid); expected Administrators or SYSTEM"
    }

    foreach ($ace in $acl.Access) {
        if ($ace.AccessControlType -ne [System.Security.AccessControl.AccessControlType]::Allow) { continue }
        if (($ace.FileSystemRights -band $WriteMask) -eq 0) { continue }   # read/execute only -> fine
        $sid = ConvertTo-Sid $ace.IdentityReference
        if ($TrustedSids -notcontains $sid) {
            return "grants '$($ace.FileSystemRights)' to '$($ace.IdentityReference)' ($sid)"
        }
    }
    return $null
}

# --- resolve the source agent executable ------------------------------------
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $BinaryPath) {
    $candidates = @(
        (Join-Path $here 'agent\PROCTOR.Agent.exe'),
        (Join-Path $here 'PROCTOR.Agent.exe')
    )
    $BinaryPath = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $BinaryPath -or -not (Test-Path $BinaryPath)) {
    throw "Could not find a source PROCTOR.Agent.exe. Pass -BinaryPath explicitly."
}
$BinaryPath = (Resolve-Path $BinaryPath).Path
$sourceDir  = Split-Path -Parent $BinaryPath
Write-Host "Source bundle:  $sourceDir"
Write-Host "Install target: $InstallDir"

# --- stop/remove any prior install so files aren't locked -------------------
$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "Service '$ServiceName' already exists; stopping and removing it first."
    if ($existing.Status -ne 'Stopped') { Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue }
    & sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 2
}

# --- start from a clean, protected folder -----------------------------------
# Wipe any pre-existing InstallDir first: a leftover folder could carry a weak ACL or planted
# DLLs that the SYSTEM service would load (DLL search-order hijack). Sanity-guard the path so a
# bad -InstallDir can't turn this into a destructive delete.
$InstallDir = [System.IO.Path]::GetFullPath($InstallDir)
$leaf = Split-Path $InstallDir -Leaf
$qualifier = Split-Path $InstallDir -Qualifier   # e.g. 'C:'
if ([string]::IsNullOrWhiteSpace($leaf) -or $InstallDir -eq "$qualifier\" -or $InstallDir.Length -le 4) {
    throw "Unsafe -InstallDir '$InstallDir' (refusing to operate on a drive root)."
}
if (Test-Path -LiteralPath $InstallDir) {
    Write-Host "Removing existing $InstallDir (avoids stale ACLs / planted DLLs)..."
    Remove-Item -LiteralPath $InstallDir -Recurse -Force
    if (Test-Path -LiteralPath $InstallDir) {
        throw "Could not remove existing $InstallDir (files in use?). Resolve and re-run."
    }
}
# --- create the folder ALREADY holding the protected DACL -------------------
# Build the DACL first and create the directory with it, so there is never an instant where the
# folder exists under the weaker inherited ACL (TOCTOU). %ProgramFiles% is not user-writable to
# begin with, so this is defence-in-depth, but it closes the window cleanly.
Write-Host "Creating $InstallDir with a protected ACL (Administrators/SYSTEM full; Users read & execute)..."
$admins = New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544')
$system = New-Object Security.Principal.SecurityIdentifier('S-1-5-18')
$users  = New-Object Security.Principal.SecurityIdentifier('S-1-5-32-545')
$inherit = [System.Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit'
$none    = [System.Security.AccessControl.PropagationFlags]::None
$allow   = [System.Security.AccessControl.AccessControlType]::Allow

$sd = New-Object System.Security.AccessControl.DirectorySecurity
$sd.SetAccessRuleProtection($true, $false)   # break inheritance, drop inherited ACEs
$sd.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($admins,'FullControl',$inherit,$none,$allow)))
$sd.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($system,'FullControl',$inherit,$none,$allow)))
$sd.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($users,'ReadAndExecute',$inherit,$none,$allow)))
[System.IO.Directory]::CreateDirectory($InstallDir, $sd) | Out-Null

Write-Host "Copying agent files into $InstallDir..."
Copy-Item -Path (Join-Path $sourceDir '*') -Destination $InstallDir -Recurse -Force

$targetExe = Join-Path $InstallDir 'PROCTOR.Agent.exe'
if (-not (Test-Path $targetExe)) {
    throw "Copy did not produce $targetExe - check the source bundle."
}

# --- verify installed files match the source bundle EXACTLY -----------------
# By relative path + SHA-256: this rejects any file that appears in the install tree but not in
# the trusted source (e.g. something planted during the copy), so the later /setowner cannot mask
# an unexpected file by relabelling its owner.
Write-Host "Verifying installed files match the source bundle (SHA-256)..."
function Get-RelHashMap {
    param([Parameter(Mandatory)][string]$Root)
    $base = (Resolve-Path -LiteralPath $Root).Path.TrimEnd('\')
    $map = @{}
    Get-ChildItem -LiteralPath $base -Recurse -File -Force | ForEach-Object {
        $rel = $_.FullName.Substring($base.Length + 1)
        $map[$rel] = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
    }
    return $map
}
$srcMap = Get-RelHashMap -Root $sourceDir
$dstMap = Get-RelHashMap -Root $InstallDir
$fileProblems = @()
foreach ($k in $dstMap.Keys) {
    if (-not $srcMap.ContainsKey($k)) { $fileProblems += "unexpected installed file: $k" }
    elseif ($srcMap[$k] -ne $dstMap[$k]) { $fileProblems += "hash mismatch: $k" }
}
foreach ($k in $srcMap.Keys) { if (-not $dstMap.ContainsKey($k)) { $fileProblems += "missing file: $k" } }
if ($fileProblems) {
    throw ("Refusing to install: installed files do not match the source bundle:`n  " +
           ($fileProblems -join "`n  "))
}

# Normalize: re-inherit the protected DACL on children (drops any explicit ACE the copy carried
# over) and set uniform Administrators ownership. /T recurse, /C continue, /Q quiet.
& icacls $InstallDir /reset /T /C /Q | Out-Null
& icacls $InstallDir /setowner "*S-1-5-32-544" /T /C /Q | Out-Null

# --- verify the WHOLE install tree is securely owned and not user-writable ---
Write-Host "Verifying the install tree is owned by Administrators and not writable by standard users..."
$items = @($InstallDir) + @(Get-ChildItem -LiteralPath $InstallDir -Recurse -Force |
                            Select-Object -ExpandProperty FullName)
$problems = foreach ($it in $items) {
    $p = Get-ItemSecurityProblem -Path $it
    if ($p) { "  $it -> $p" }
}
if ($problems) {
    throw ("Refusing to install: the install tree is not secure (a standard user could run code " +
           "as SYSTEM). Problems:`n" + ($problems -join "`n"))
}

# --- protected state folder for the agent's random install id (FR-SEC-07) -------------------
# %ProgramData% lets standard users create files in new subfolders, so pre-create ours with a
# protected DACL. Anything a non-admin planted there before the first run (agent-id, agent-key)
# is discarded so a student can't choose (or copy) the identity this machine reports.
$stateDir = Join-Path $env:ProgramData 'PROCTOR'
$stateSd = New-Object System.Security.AccessControl.DirectorySecurity
$stateSd.SetAccessRuleProtection($true, $false)
$stateSd.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($admins,'FullControl',$inherit,$none,$allow)))
$stateSd.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($system,'FullControl',$inherit,$none,$allow)))
$stateSd.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($users,'ReadAndExecute',$inherit,$none,$allow)))
# Never "bless" files of unknown origin with /reset + /setowner: a student could have pre-created
# the folder and planted an agent-key whose private half they kept, then impersonated this PC.
# If the folder or anything in it isn't owned by (and writable only by) trusted principals, wipe
# it and start clean; the agent re-enrols with a fresh key. Files the agent itself created pass.
if (Test-Path -LiteralPath $stateDir) {
    $stateProblem = Get-ItemSecurityProblem -Path $stateDir
    if (-not $stateProblem) {
        foreach ($item in @(Get-ChildItem -LiteralPath $stateDir -Recurse -Force -ErrorAction Stop)) {
            $p = Get-ItemSecurityProblem -Path $item.FullName
            if ($p) { $stateProblem = "$($item.FullName): $p"; break }
        }
    }
    if ($stateProblem) {
        Write-Host "Discarding untrusted $stateDir ($stateProblem)."
        Remove-Item -LiteralPath $stateDir -Recurse -Force
        if (Test-Path -LiteralPath $stateDir) {
            throw "Could not remove untrusted $stateDir (files in use?). Resolve and re-run."
        }
    }
}
if (Test-Path -LiteralPath $stateDir) {
    Set-Acl -LiteralPath $stateDir -AclObject $stateSd
    & icacls $stateDir /reset /T /C /Q | Out-Null
    & icacls $stateDir /setowner "*S-1-5-32-544" /T /C /Q | Out-Null
} else {
    [System.IO.Directory]::CreateDirectory($stateDir, $stateSd) | Out-Null
}

# --- register: auto-start, runs as LocalSystem ------------------------------
# LocalSystem is used so the agent runs before/independent of a student login and can launch its
# monitor into the student's desktop session (SeTcbPrivilege) for screen streaming. The protected
# ACL above is what keeps that safe.
Write-Host "Creating service '$ServiceName' (auto-start)..."
New-Service -Name $ServiceName `
            -BinaryPathName "`"$targetExe`"" `
            -DisplayName $DisplayName `
            -Description 'PROCTOR classroom monitoring agent for institution-owned lab PCs.' `
            -StartupType Automatic | Out-Null

# Restart on failure: 5s, 5s, then every 10s; reset the failure count daily (FR-AGENT-07).
& sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/5000/restart/10000 | Out-Null
# Treat a non-zero exit as a failure so the recovery actions above apply even on a clean crash.
& sc.exe failureflag $ServiceName 1 | Out-Null

# --- firewall: same rule as the MSI ---------------------------------------
# The agent only dials out to the server, but WebRTC media (the teacher's broadcast, remote
# control) arrives over UDP from other lab PCs. Scoped to this exe and the local subnet.
$fwRule = "$ServiceName-WebRtc-Udp"
Remove-NetFirewallRule -Name $fwRule -ErrorAction SilentlyContinue
Write-Host "Adding firewall rule '$fwRule' (UDP, local subnet, $targetExe)..."
New-NetFirewallRule -Name $fwRule `
                    -DisplayName 'PROCTOR Agent (screen sharing)' `
                    -Description 'Lets the PROCTOR agent receive screen-share video from PCs on the local network.' `
                    -Group 'PROCTOR' `
                    -Direction Inbound -Action Allow -Protocol UDP `
                    -Program $targetExe -RemoteAddress LocalSubnet -Profile Any | Out-Null

# --- start ------------------------------------------------------------------
Write-Host "Starting '$ServiceName'..."
Start-Service -Name $ServiceName
$svc = Get-Service -Name $ServiceName
Write-Host "Done. '$($svc.DisplayName)' is $($svc.Status), runs from $targetExe, and starts automatically at boot." -ForegroundColor Green

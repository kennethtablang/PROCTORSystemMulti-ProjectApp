<#
.SYNOPSIS
  Builds ClassInsight-Agent-Setup.exe, optionally with one lab's settings built in, so IT can
  install the student agent on each lab PC by double-clicking one file.

.DESCRIPTION
  Publishes the agent (self-contained: lab PCs need no .NET install), links the MSI, wraps it in
  the setup .exe and copies the result to ClassInsight\dist\.

  Every setting is optional. Values built in here are the defaults; the same names given on the
  setup's command line (NAME=value) still override them. Values a PC was installed with before are
  kept on upgrade when the new setup doesn't set them.

.PARAMETER ServerFingerprint
  The classroom server's key fingerprint, printed by the server at start-up
  ("Server fingerprint ... 47E5 EF43 ..."). Strongly recommended: agents then talk to that
  server only. Required for -AllowRemoteControl.

.PARAMETER ServerAddress
  host or host:port, when UDP broadcast discovery is blocked on the lab network.

.PARAMETER RoomName
  Join only the room with this name.

.PARAMETER EnrollmentKey
  The lab enrolment key (server setting AgentEnrollment=Key). WARNING: anyone holding a setup
  built with it can read it out of the file and enrol PCs, so keep that setup like a password
  (don't leave it on a share students can reach). Alternative: build without it and pass
  ENROLLMENTKEY=... on the command line at install time.

.PARAMETER AllowRemoteControl
  Allow teachers to take remote keyboard/mouse control of these PCs (needs -ServerFingerprint).

.PARAMETER Name
  Suffix for the output file, e.g. "Lab3" -> ClassInsight-Agent-Setup-Lab3.exe.
  Defaults to the room name, if any.

.EXAMPLE
  .\Build-AgentSetup.ps1
  # Generic setup: finds the server on the LAN and trusts the first one it meets.

.EXAMPLE
  .\Build-AgentSetup.ps1 -ServerFingerprint "47E5 EF43 ... 49B9" -RoomName "Lab 3"
#>
[CmdletBinding()]
param(
    [string]$ServerFingerprint,
    [string]$ServerAddress,
    [string]$RoomName,
    [string]$EnrollmentKey,
    [switch]$AllowRemoteControl,
    [string]$Name,
    [string]$OutputDir = (Join-Path $PSScriptRoot '..\dist')
)

$ErrorActionPreference = 'Stop'

function Assert-Plain([string]$label, [string]$value) {
    # '$(' is a WiX preprocessor reference and '[ ]' are installer variable references: refuse them
    # rather than build a setup that silently installs something else.
    if ($value -match '\$|\[|\]|[\x00-\x1F]') {
        throw "$label must not contain '`$', '[', ']' or control characters."
    }
}

# --- Validate -------------------------------------------------------------------------------
if ($ServerFingerprint) {
    $hex = ($ServerFingerprint -replace '[\s:-]', '')
    if ($hex -notmatch '^[0-9A-Fa-f]{64}$') {
        throw "ServerFingerprint must be the 64 hex digits the server prints (spaces allowed), e.g. '47E5 EF43 ...'."
    }
    $ServerFingerprint = (($hex.ToUpperInvariant() -split '(.{4})' | Where-Object { $_ }) -join ' ')
}
if ($AllowRemoteControl -and -not $ServerFingerprint) {
    throw "-AllowRemoteControl needs -ServerFingerprint: agents only accept remote control from a server they are pinned to."
}
if ($ServerAddress) {
    $ServerAddress = $ServerAddress.Trim()
    if ($ServerAddress -notmatch '^[A-Za-z0-9.\-:]+$') { throw "ServerAddress must be host or host:port." }
}
if ($RoomName) { $RoomName = $RoomName.Trim(); Assert-Plain 'RoomName' $RoomName }
if ($EnrollmentKey) {
    $EnrollmentKey = $EnrollmentKey.Trim()
    Assert-Plain 'EnrollmentKey' $EnrollmentKey
    if ($EnrollmentKey.Length -lt 12) { Write-Warning "The enrolment key is short; a long random one is much harder to guess." }
}

# --- Settings include for the bundle ----------------------------------------------------------
$setupDir = Join-Path $PSScriptRoot 'setup'
$objDir   = Join-Path $setupDir 'obj'
New-Item -ItemType Directory -Force $objDir | Out-Null
$settings = Join-Path $objDir 'lab-settings.wxi'

function Var([string]$varName, [string]$value, [switch]$hidden) {
    $attrs = "Name=`"$varName`" bal:Overridable=`"yes`""
    if ($hidden) { $attrs += ' Hidden="yes"' }
    if ($value) { $attrs += " Value=`"$([System.Security.SecurityElement]::Escape($value))`"" }
    "  <Variable $attrs />"
}
$lines = @(
    '<?xml version="1.0" encoding="utf-8"?>'
    '<Include xmlns="http://wixtoolset.org/schemas/v4/wxs" xmlns:bal="http://wixtoolset.org/schemas/v4/wxs/bal">'
    (Var 'SERVERFINGERPRINT' $ServerFingerprint)
    (Var 'SERVERADDRESS' $ServerAddress)
    (Var 'ROOMNAME' $RoomName)
    (Var 'ALLOWREMOTECONTROL' $(if ($AllowRemoteControl) { 'true' } else { '' }))
    (Var 'ENROLLMENTKEY' $EnrollmentKey -hidden)
    '</Include>'
)
[System.IO.File]::WriteAllLines($settings, $lines, (New-Object System.Text.UTF8Encoding $false))

# --- Build ------------------------------------------------------------------------------------
try {
    Write-Host "Building the agent, MSI and setup (this takes a minute or two)..."
    & dotnet build (Join-Path $setupDir 'ClassInsight.Agent.Setup.wixproj') -c Release --no-incremental -nologo -v m -tl:off `
        "-p:LabSettings=$settings"
    if ($LASTEXITCODE -ne 0) { throw "Build failed (exit code $LASTEXITCODE)." }
}
finally {
    # The include can hold the enrolment key: don't leave it lying around.
    Remove-Item $settings -Force -ErrorAction SilentlyContinue
}

$built = Join-Path $setupDir 'bin\x64\Release\ClassInsight-Agent-Setup.exe'
if (-not $Name -and $RoomName) { $Name = $RoomName }
$suffix = if ($Name) { '-' + ($Name -replace '[^A-Za-z0-9_-]', '') } else { '' }
New-Item -ItemType Directory -Force $OutputDir | Out-Null
$target = Join-Path (Resolve-Path $OutputDir) "ClassInsight-Agent-Setup$suffix.exe"
Copy-Item $built $target -Force

if ($EnrollmentKey) {
    # The build output and its debug database carry the key too: keep only the copy in dist\.
    Remove-Item $built, ($built -replace '\.exe$', '.wixpdb') -Force -ErrorAction SilentlyContinue
}

Write-Host ""
Write-Host "Setup ready: $target" -ForegroundColor Green
Write-Host ("  Server key     : " + $(if ($ServerFingerprint) { $ServerFingerprint } else { '(not pinned - trusts the first server it finds)' }))
Write-Host ("  Server address : " + $(if ($ServerAddress) { $ServerAddress } else { '(found by LAN discovery)' }))
Write-Host ("  Room           : " + $(if ($RoomName) { $RoomName } else { '(any)' }))
Write-Host ("  Enrolment key  : " + $(if ($EnrollmentKey) { 'built in - keep this setup private' } else { '(none)' }))
Write-Host ("  Remote control : " + $(if ($AllowRemoteControl) { 'allowed' } else { 'off' }))
Write-Host ""
Write-Host "Install on each lab PC: double-click it (needs an administrator), or silently:"
Write-Host "  ClassInsight-Agent-Setup$suffix.exe /quiet /norestart"

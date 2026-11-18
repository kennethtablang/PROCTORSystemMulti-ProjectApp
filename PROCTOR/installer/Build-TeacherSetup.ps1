<#
.SYNOPSIS
  Builds PROCTOR-Teacher-Setup.exe: the installer for the teacher's PC (teacher app + the
  classroom service student PCs connect to + the agent package they update from).

.DESCRIPTION
  Publishes the teacher app and the classroom service (self-contained), builds the generic
  student-agent MSI, links the teacher MSI, wraps it in the setup .exe and copies it, with its
  SHA-256 (PROCTOR-Teacher-Setup.exe.sha256, checked by the app's auto-update), to PROCTOR\dist\.

  The version is ProctorVersion in Directory.Build.props at the repository root.

.EXAMPLE
  .\Build-TeacherSetup.ps1
#>
[CmdletBinding()]
param(
    [string]$OutputDir = (Join-Path $PSScriptRoot '..\dist')
)

$ErrorActionPreference = 'Stop'

$project = Join-Path $PSScriptRoot 'teacher\setup\PROCTOR.Teacher.Setup.wixproj'
Write-Host 'Building the teacher app, classroom service, agent package, MSI and setup (several minutes)...'
& dotnet build $project -c Release --no-incremental -nologo -v m -tl:off
if ($LASTEXITCODE -ne 0) { throw "Build failed (exit code $LASTEXITCODE)." }

$built = Join-Path $PSScriptRoot 'teacher\setup\bin\x64\Release\PROCTOR-Teacher-Setup.exe'
New-Item -ItemType Directory -Force $OutputDir | Out-Null
$target = Join-Path (Resolve-Path $OutputDir) 'PROCTOR-Teacher-Setup.exe'
Copy-Item $built $target -Force

$hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $target).Hash.ToLowerInvariant()
# "<hash>  <name>" (sha256sum format); the app reads the first word.
[IO.File]::WriteAllText("$target.sha256", "$hash  PROCTOR-Teacher-Setup.exe`n")

[xml]$props = Get-Content (Join-Path $PSScriptRoot '..\..\Directory.Build.props')
$version = $props.Project.PropertyGroup.ProctorVersion
Write-Host ''
Write-Host "Setup ready: $target" -ForegroundColor Green
Write-Host "  Version : $version"
Write-Host "  Size    : $([math]::Round((Get-Item $target).Length / 1MB, 1)) MB"
Write-Host "  SHA-256 : $hash"
Write-Host ''
Write-Host "Install on the teacher's PC: double-click it (needs an administrator once)."

<#
.SYNOPSIS
  Publishes a PROCTOR update on GitHub: every teacher app then shows the "Update available" pop-up,
  and student PCs follow their teacher's PC after it updates.

.DESCRIPTION
  1. Reads the version from Directory.Build.props (ProctorVersion). Bump it first: the apps only
     offer a release whose version is higher than their own.
  2. Builds PROCTOR-Teacher-Setup.exe (+ .sha256) and a generic PROCTOR-Agent-Setup.exe (no lab
     settings built in, for new student PCs; existing ones update from the teacher's PC).
  3. Creates GitHub release v<version> on the current commit with those files, after you confirm.

  Needs the GitHub CLI signed in (gh auth login) with push rights to the repository, and the
  current commit pushed. The release is public as soon as it is published.

.PARAMETER Notes
  Release notes shown in the update pop-up ("What's new"). Or use -NotesFile.

.PARAMETER Draft
  Create the release as a draft (apps ignore drafts until you publish it on GitHub).

.EXAMPLE
  .\Publish-Release.ps1 -Notes "Faster screen sharing; fixes for discovery on Wi-Fi."
#>
[CmdletBinding()]
param(
    [string]$Notes,
    [string]$NotesFile,
    [switch]$Draft,
    [switch]$Yes
)

$ErrorActionPreference = 'Stop'
$repo = 'kennethtablang/PROCTORSystemMulti-ProjectApp'
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')

[xml]$props = Get-Content (Join-Path $root 'Directory.Build.props')
$version = "$($props.Project.PropertyGroup.ProctorVersion)".Trim()
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "ProctorVersion '$version' in Directory.Build.props must look like 1.2.3." }
$tag = "v$version"

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { throw 'The GitHub CLI (gh) is not installed: https://cli.github.com' }

# Windows PowerShell 5.1 turns a native program's stderr into terminating errors under
# ErrorActionPreference=Stop, so relax it around gh: it writes progress and expected answers
# ("release not found") there. Success is judged by the exit code.
function Test-Gh {
    $saved = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { & gh @args 2>&1 | Out-Null; return ($LASTEXITCODE -eq 0) }
    finally { $ErrorActionPreference = $saved }
}
function Invoke-Gh {
    $saved = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { & gh @args 2>&1 | ForEach-Object { Write-Host "$_" }; return $LASTEXITCODE }
    finally { $ErrorActionPreference = $saved }
}

if (-not (Test-Gh auth status)) { throw 'Sign in to GitHub first: gh auth login' }
if (Test-Gh release view $tag --repo $repo) { throw "Release $tag already exists. Bump ProctorVersion in Directory.Build.props first." }

$commit = (& git -C $root rev-parse HEAD).Trim()
if (-not (& git -C $root branch -r --contains $commit)) { throw "The current commit $($commit.Substring(0,7)) isn't pushed to GitHub yet: push it first." }
if (& git -C $root status --porcelain) { Write-Warning 'There are uncommitted changes; the release is built from the working tree but tagged on the last commit.' }

# --- build ---------------------------------------------------------------------------------
$stage = Join-Path $root "PROCTOR\dist\release-$tag"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory $stage | Out-Null

& (Join-Path $PSScriptRoot 'Build-TeacherSetup.ps1') -OutputDir $stage
& (Join-Path $PSScriptRoot 'Build-AgentSetup.ps1') -OutputDir $stage
$assets = @(
    (Join-Path $stage 'PROCTOR-Teacher-Setup.exe'),
    (Join-Path $stage 'PROCTOR-Teacher-Setup.exe.sha256'),
    (Join-Path $stage 'PROCTOR-Agent-Setup.exe')
)
foreach ($a in $assets) { if (-not (Test-Path $a)) { throw "Missing $a after the build." } }

# --- publish -------------------------------------------------------------------------------
Write-Host ''
Write-Host "Ready to publish PROCTOR $version as GitHub release $tag on $($commit.Substring(0,7)) ($repo)."
Write-Host "  Assets: $(($assets | ForEach-Object { Split-Path $_ -Leaf }) -join ', ')"
if (-not $Draft) { Write-Host '  Every teacher app will offer this update within a few hours.' -ForegroundColor Yellow }
if (-not $Yes) {
    $answer = Read-Host 'Publish now? (y/N)'
    if ($answer -notmatch '^(y|yes)$') { Write-Host "Not published. The setups are in $stage."; return }
}

$ghArgs = @('release', 'create', $tag) + $assets + @('--repo', $repo, '--target', $commit, '--title', "PROCTOR $version")
if ($NotesFile) { $ghArgs += @('--notes-file', $NotesFile) }
elseif ($Notes) { $ghArgs += @('--notes', $Notes) }
else { $ghArgs += '--generate-notes' }
if ($Draft) { $ghArgs += '--draft' }
$code = Invoke-Gh @ghArgs
if ($code -ne 0) { throw "gh release create failed (exit code $code)." }
Write-Host "Published $tag." -ForegroundColor Green

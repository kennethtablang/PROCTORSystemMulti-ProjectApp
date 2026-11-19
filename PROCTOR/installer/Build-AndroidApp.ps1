<#
.SYNOPSIS
  Builds PROCTOR-Teacher-Android.apk: the teacher app for Android phones and tablets.

.DESCRIPTION
  Publishes the Android teacher app (Release), signs it with the project's signing key and copies
  it, with its SHA-256, to PROCTOR\dist\. Install it by opening the .apk on the phone/tablet
  (allow "Install unknown apps" for the browser or file manager once), or with
  adb install -r PROCTOR-Teacher-Android.apk.

  SIGNING KEY. The first run creates one in %LOCALAPPDATA%\PROCTOR\android-signing\ (the keystore
  plus a file with its password). BACK UP BOTH FILES. Android installs an update over the app only
  when it is signed with the same key: with a lost key, every tablet must uninstall the app and
  install it again (losing its saved servers and settings).

  The version is ProctorVersion in Directory.Build.props at the repository root.

.PARAMETER KeystorePath
  The signing keystore to use (created when missing). Its password is read from
  "<KeystorePath>.password".

.EXAMPLE
  .\Build-AndroidApp.ps1
#>
[CmdletBinding()]
param(
    [string]$OutputDir = (Join-Path $PSScriptRoot '..\dist'),
    [string]$KeystorePath = (Join-Path $env:LOCALAPPDATA 'PROCTOR\android-signing\proctor-teacher.keystore')
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$project = Join-Path $root 'PROCTORSystemMulti-ProjectApp\PROCTORSystemMulti-ProjectApp.Droid\PROCTORSystemMulti-ProjectApp.Droid.csproj'
$alias = 'proctor'
$passwordFile = "$KeystorePath.password"

# --- signing key -----------------------------------------------------------------------------
function Find-Keytool {
    $candidates = @()
    if ($env:JAVA_HOME) { $candidates += Join-Path $env:JAVA_HOME 'bin\keytool.exe' }
    foreach ($base in 'C:\Program Files\Android\openjdk', 'C:\Program Files\Microsoft', 'C:\Program Files\Eclipse Adoptium', 'C:\Program Files\Java') {
        if (Test-Path $base) {
            $candidates += Get-ChildItem $base -Recurse -Filter keytool.exe -ErrorAction SilentlyContinue |
                Sort-Object FullName -Descending | ForEach-Object FullName
        }
    }
    $onPath = Get-Command keytool -ErrorAction SilentlyContinue
    if ($onPath) { $candidates += $onPath.Source }
    $candidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
}

# apksigner reads ONE file given for both passwords line by line: line 1 = keystore, line 2 = key.
# A PKCS12 keystore has one password for both, so the file holds it twice.
function Write-PasswordFile([string]$password) {
    [IO.File]::WriteAllText($passwordFile, "$password`n$password`n")
}

if (-not (Test-Path $KeystorePath)) {
    if (Test-Path $passwordFile) { throw "$passwordFile exists but its keystore doesn't. Restore $KeystorePath from your backup." }
    $keytool = Find-Keytool
    if (-not $keytool) { throw 'keytool.exe (Java) not found. Install the Android workload in Visual Studio, or set JAVA_HOME.' }

    New-Item -ItemType Directory -Force (Split-Path $KeystorePath) | Out-Null
    $bytes = New-Object byte[] 24
    [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
    $password = [Convert]::ToBase64String($bytes).Replace('+', 'A').Replace('/', 'B').TrimEnd('=')
    Write-PasswordFile $password

    Write-Host "Creating the Android signing key: $KeystorePath"
    $saved = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'   # keytool writes its notes to stderr
    & $keytool -genkeypair -noprompt -keystore $KeystorePath -storetype PKCS12 -alias $alias `
        -keyalg RSA -keysize 3072 -validity 10000 -dname 'CN=PROCTOR Teacher' `
        -storepass $password -keypass $password 2>&1 | Out-Null
    $code = $LASTEXITCODE
    $ErrorActionPreference = $saved
    if ($code -ne 0 -or -not (Test-Path $KeystorePath)) {
        Remove-Item $passwordFile -ErrorAction SilentlyContinue
        throw "keytool failed (exit code $code)."
    }
    Write-Warning "New signing key. Back up BOTH files now (USB drive / password manager): updates to the app need this exact key.`n  $KeystorePath`n  $passwordFile"
}
if (-not (Test-Path $passwordFile)) { throw "Missing $passwordFile (the keystore's password). Restore it from your backup." }
$lines = @([IO.File]::ReadAllLines($passwordFile) | Where-Object { $_ -ne '' })
if ($lines.Count -eq 1) { Write-PasswordFile $lines[0] }   # a one-line file (typed by hand / older script)

# --- build -----------------------------------------------------------------------------------
Write-Host 'Building the Android teacher app (several minutes)...'
$droidDir = Join-Path $root 'PROCTORSystemMulti-ProjectApp\PROCTORSystemMulti-ProjectApp.Droid'
$publishDir = Join-Path $droidDir 'bin\Release\net10.0-android\publish'
# Start clean: a signing step that failed once can leave an unsigned "-Signed.apk" behind that
# later builds treat as up to date.
foreach ($d in (Join-Path $droidDir 'bin\Release'), (Join-Path $droidDir 'obj\Release')) {
    if (Test-Path $d) { Remove-Item $d -Recurse -Force }
}
# "file:" makes the build read the password from the file, so it never appears on a command line.
& dotnet publish $project -c Release -f net10.0-android -nologo -v m -tl:off `
    -p:AndroidKeyStore=true `
    "-p:AndroidSigningKeyStore=$KeystorePath" `
    "-p:AndroidSigningKeyAlias=$alias" `
    "-p:AndroidSigningStorePass=file:$passwordFile" `
    "-p:AndroidSigningKeyPass=file:$passwordFile"
if ($LASTEXITCODE -ne 0) { throw "Build failed (exit code $LASTEXITCODE)." }

$built = Get-ChildItem $publishDir -Filter '*-Signed.apk' | Select-Object -First 1
if (-not $built) { throw "No signed .apk in $publishDir." }

# Don't hand out an .apk that Android would refuse ("no certificates"): check its signature.
$sdkDirs = @($env:ANDROID_HOME, $env:ANDROID_SDK_ROOT, 'C:\Program Files (x86)\Android\android-sdk', (Join-Path $env:LOCALAPPDATA 'Android\Sdk')) |
    Where-Object { $_ -and (Test-Path (Join-Path $_ 'build-tools')) }
$apksigner = $sdkDirs | ForEach-Object { Get-ChildItem (Join-Path $_ 'build-tools') -Recurse -Filter apksigner.jar -ErrorAction SilentlyContinue } |
    Sort-Object FullName -Descending | Select-Object -First 1
$keytool = Find-Keytool
if ($apksigner -and $keytool) {
    $java = Join-Path (Split-Path $keytool) 'java.exe'
    $saved = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $verify = & $java -jar $apksigner.FullName verify $built.FullName 2>&1
    $code = $LASTEXITCODE
    $ErrorActionPreference = $saved
    if ($code -ne 0) { throw "The built .apk isn't signed correctly: $($verify -join ' ')" }
} else {
    Write-Warning 'apksigner not found: the .apk signature was not checked.'
}

New-Item -ItemType Directory -Force $OutputDir | Out-Null
$target = Join-Path (Resolve-Path $OutputDir) 'PROCTOR-Teacher-Android.apk'
Copy-Item $built.FullName $target -Force
$hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $target).Hash.ToLowerInvariant()
[IO.File]::WriteAllText("$target.sha256", "$hash  PROCTOR-Teacher-Android.apk`n")

[xml]$props = Get-Content (Join-Path $root 'Directory.Build.props')
$version = $props.Project.PropertyGroup.ProctorVersion
Write-Host ''
Write-Host "App ready: $target" -ForegroundColor Green
Write-Host "  Version : $version"
Write-Host "  Size    : $([math]::Round((Get-Item $target).Length / 1MB, 1)) MB"
Write-Host "  SHA-256 : $hash"
Write-Host ''
Write-Host 'Install: copy it to the phone/tablet and open it (allow "Install unknown apps" once),'
Write-Host '         or with USB debugging on: adb install -r PROCTOR-Teacher-Android.apk'

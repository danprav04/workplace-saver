<#
.SYNOPSIS
    Compiles, packages, and optionally publishes the Workplace Saver installer to GitHub Releases.

.DESCRIPTION
    1. Publishes a clean Release build of Workplace Saver.
    2. Builds the Windows setup installer (.exe) via Inno Setup 6.
    3. Packages a standalone portable zip (.zip).
    4. Computes SHA-256 checksums.
    5. Optionally uploads all release assets to GitHub Releases using GitHub CLI.

.PARAMETER Version
    The version tag for the build and release (default: "1.0.0").

.PARAMETER UploadRelease
    If specified, creates a GitHub Release and uploads installer artifacts.

.PARAMETER Prerelease
    Marks the GitHub Release as a pre-release.

.EXAMPLE
    .\Create-Installer.ps1
    .\Create-Installer.ps1 -Version "1.0.0" -UploadRelease
#>

[CmdletBinding()]
param (
    [string]$Version = "1.0.0",
    [string]$Notes = "",
    [string]$TargetCommit = "",
    [switch]$UploadRelease,
    [switch]$Prerelease
)

$ErrorActionPreference = "Stop"
$ScriptDir = $PSScriptRoot
$RootDir = (Resolve-Path "$ScriptDir\..").Path
$PublishDir = Join-Path $RootDir "publish"
$DistDir = Join-Path $RootDir "dist"
$AppIcon = Join-Path $RootDir "src\WorkplaceSaver\Assets\app.ico"
$IssPath = Join-Path $DistDir "WorkplaceSaver.iss"
$ProjectFile = Join-Path $RootDir "src\WorkplaceSaver\WorkplaceSaver.csproj"

Write-Host "========================================================" -ForegroundColor Cyan
Write-Host " Workplace Saver Installer & Release Builder v$Version" -ForegroundColor Cyan
Write-Host "========================================================" -ForegroundColor Cyan

# ---------------------------------------------------------------------
# 1. Locate .NET SDK
# ---------------------------------------------------------------------
$dotnet = "dotnet"
$localDotnet = "$env:LocalAppData\Microsoft\dotnet\dotnet.exe"
if (Test-Path $localDotnet) {
    $dotnet = $localDotnet
}

Write-Host "`n[1/5] Checking .NET SDK..." -ForegroundColor Yellow
$sdkVersion = & $dotnet --version
Write-Host "  Using .NET SDK: $sdkVersion ($dotnet)" -ForegroundColor Gray

# ---------------------------------------------------------------------
# 2. Locate Inno Setup Compiler (ISCC)
# ---------------------------------------------------------------------
Write-Host "`n[2/5] Locating Inno Setup Compiler (ISCC)..." -ForegroundColor Yellow
$isccCandidates = @(
    "$env:LocalAppData\Programs\Inno Setup 6\ISCC.exe",
    "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
    "C:\Program Files\Inno Setup 6\ISCC.exe"
)

$isccPath = $null
foreach ($candidate in $isccCandidates) {
    if (Test-Path $candidate) {
        $isccPath = $candidate
        break
    }
}

if (-not $isccPath) {
    $whichIscc = (Get-Command "ISCC.exe" -ErrorAction SilentlyContinue)
    if ($whichIscc) {
        $isccPath = $whichIscc.Source
    }
}

if (-not $isccPath) {
    Write-Error "Inno Setup Compiler (ISCC.exe) was not found. Please install Inno Setup 6 (e.g. winget install JRSoftware.InnoSetup --source winget)."
}
Write-Host "  Using Inno Setup: $isccPath" -ForegroundColor Gray

# ---------------------------------------------------------------------
# 3. Publish Release Binary
# ---------------------------------------------------------------------
Write-Host "`n[3/5] Publishing Release build of Workplace Saver..." -ForegroundColor Yellow
if (Test-Path $PublishDir) {
    # Stop running instance if locking publish files
    Stop-Process -Name WorkplaceSaver -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 300
    Remove-Item $PublishDir -Recurse -Force
}

& $dotnet publish $ProjectFile -c Release -o $PublishDir /p:Version=$Version /p:AssemblyVersion=$Version
if ($LASTEXITCODE -ne 0) {
    Write-Error "Dotnet publish failed with exit code $LASTEXITCODE."
}
Write-Host "  Release published to: $PublishDir" -ForegroundColor Green

# ---------------------------------------------------------------------
# 4. Compile Installer & Portable Zip
# ---------------------------------------------------------------------
Write-Host "`n[4/5] Building Installer and Portable ZIP..." -ForegroundColor Yellow
if (-not (Test-Path $DistDir)) {
    New-Item -ItemType Directory -Path $DistDir | Out-Null
}

$installerName = "WorkplaceSaver-Setup-v$Version.exe"
$installerPath = Join-Path $DistDir $installerName
$portableZipName = "WorkplaceSaver-v$Version-Portable.zip"
$portableZipPath = Join-Path $DistDir $portableZipName
$checksumsFile = Join-Path $DistDir "checksums.txt"

# Generate dynamic Inno Setup script in dist directory
Write-Host "  Generating Inno Setup script dynamically..." -ForegroundColor Gray
$issTemplate = @'
#define MyAppVersion "__VERSION__"
#define MyAppName "Workplace Saver"
#define MyAppPublisher "Daniel Pravutiner"
#define MyAppURL "https://github.com/danprav04/workplace-saver"
#define MyAppExeName "WorkplaceSaver.exe"
#define SourceDir "__SOURCEDIR__"
#define OutputDir "__OUTPUTDIR__"
#define IconFile "__ICONFILE__"

[Setup]
AppId={{E1D4A0B7-3475-4309-80EF-6B64883A3870}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} v{#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
AppUpdatesURL={#MyAppURL}/releases

DefaultDirName={autopf}\WorkplaceSaver
DefaultGroupName={#MyAppName}
AllowNoIcons=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog

OutputDir={#OutputDir}
OutputBaseFilename=WorkplaceSaver-Setup-v{#MyAppVersion}
SetupIconFile={#IconFile}
UninstallDisplayIcon={app}\Assets\app.ico

Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
DisableProgramGroupPage=auto
CloseApplications=yes
RestartApplications=no

ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "autostart"; Description: "Automatically start Workplace Saver in background on Windows startup"; GroupDescription: "System Integration:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\Assets\app.ico"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\Assets\app.ico"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "WorkplaceSaver"; ValueData: """{app}\{#MyAppExeName}"" --startup"; Tasks: autostart; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
'@

$issContent = $issTemplate.Replace("__VERSION__", $Version).Replace("__SOURCEDIR__", $PublishDir).Replace("__OUTPUTDIR__", $DistDir).Replace("__ICONFILE__", $AppIcon)
[System.IO.File]::WriteAllText($IssPath, $issContent, [System.Text.Encoding]::UTF8)

# Run Inno Setup Compiler
Write-Host "  Running Inno Setup compiler..." -ForegroundColor Gray
& $isccPath $IssPath
if ($LASTEXITCODE -ne 0) {
    Write-Error "Inno Setup compilation failed with exit code $LASTEXITCODE."
}
Remove-Item $IssPath -Force -ErrorAction SilentlyContinue
Write-Host "  [OK] Installer created: $installerPath" -ForegroundColor Green

# Create Portable Zip
Write-Host "  Creating portable zip archive..." -ForegroundColor Gray
if (Test-Path $portableZipPath) {
    Remove-Item $portableZipPath -Force
}
Compress-Archive -Path "$PublishDir\*" -DestinationPath $portableZipPath -CompressionLevel Optimal
Write-Host "  [OK] Portable archive created: $portableZipPath" -ForegroundColor Green

# Generate Checksums
Write-Host "  Generating SHA-256 checksums..." -ForegroundColor Gray
$setupHash = (Get-FileHash -Path $installerPath -Algorithm SHA256).Hash
$zipHash = (Get-FileHash -Path $portableZipPath -Algorithm SHA256).Hash

$checksumLines = @(
    "# Workplace Saver v$Version SHA-256 Checksums",
    "$setupHash  $installerName",
    "$zipHash  $portableZipName"
)
Set-Content -Path $checksumsFile -Value ($checksumLines -join "`r`n")
Write-Host "  [OK] Checksums written to: $checksumsFile" -ForegroundColor Green

Write-Host "`nArtifacts generated in '$DistDir':" -ForegroundColor Cyan
Write-Host "  - $installerName ($([math]::Round((Get-Item $installerPath).Length / 1MB, 2)) MB)" -ForegroundColor Gray
Write-Host "  - $portableZipName ($([math]::Round((Get-Item $portableZipPath).Length / 1MB, 2)) MB)" -ForegroundColor Gray
Write-Host "  - checksums.txt" -ForegroundColor Gray

# ---------------------------------------------------------------------
# 5. Upload to GitHub Releases (Optional)
# ---------------------------------------------------------------------
if ($UploadRelease) {
    Write-Host "`n[5/5] Uploading to GitHub Releases..." -ForegroundColor Yellow

    # Temporarily set ErrorAction to Continue for external tools
    $origEap = $ErrorActionPreference
    $ErrorActionPreference = "Continue"

    # Ensure GH_TOKEN is available
    if (-not $env:GH_TOKEN) {
        $gcmPath = "C:\Program Files\Git\mingw64\bin\git-credential-manager.exe"
        if (Test-Path $gcmPath) {
            Write-Host "  Fetching GitHub token from Git Credential Manager..." -ForegroundColor Gray
            $gcmInput = @("protocol=https", "host=github.com", "")
            $creds = $gcmInput | & $gcmPath get 2>$null
            $tokenLine = $creds | Select-String "password="
            if ($tokenLine) {
                $env:GH_TOKEN = ($tokenLine -replace "^password=", "").Trim()
                Write-Host "  Authentication token loaded successfully." -ForegroundColor Gray
            }
        }
    }

    $tag = "v$Version"
    $title = "Workplace Saver v$Version"

    if ([string]::IsNullOrWhiteSpace($TargetCommit)) {
        $TargetCommit = (git rev-parse HEAD 2>$null)
    }

    if ([string]::IsNullOrWhiteSpace($Notes)) {
        $releaseNotes = "Workplace Saver v$Version"
    } else {
        $releaseNotes = $Notes
    }

    $notesFile = Join-Path $DistDir "release-notes.md"
    [System.IO.File]::WriteAllText($notesFile, $releaseNotes, [System.Text.Encoding]::UTF8)

    # Check if release already exists
    $existingRelease = & gh release view $tag 2>$null
    if ($LASTEXITCODE -eq 0) {
        Write-Host "  Release '$tag' already exists. Updating release notes and uploading assets..." -ForegroundColor Cyan
        & gh release edit $tag --title $title -F $notesFile
        & gh release upload $tag $installerPath $portableZipPath $checksumsFile --clobber
    } else {
        Write-Host "  Creating release '$tag' on GitHub..." -ForegroundColor Cyan
        $ghArgs = @(
            "release", "create", $tag,
            $installerPath,
            $portableZipPath,
            $checksumsFile,
            "--title", $title,
            "-F", $notesFile
        )

        if (-not [string]::IsNullOrWhiteSpace($TargetCommit)) {
            $ghArgs += @("--target", $TargetCommit)
        }

        if ($Prerelease) {
            $ghArgs += "--prerelease"
        }

        & gh @ghArgs
    }

    $uploadExitCode = $LASTEXITCODE
    $ErrorActionPreference = $origEap

    if ($uploadExitCode -eq 0) {
        Write-Host "`n[OK] Successfully published GitHub Release '$tag'!" -ForegroundColor Green
    } else {
        Write-Error "Failed to create/update GitHub release. Exit code: $uploadExitCode"
    }
} else {
    Write-Host "`n[5/5] Skipping GitHub upload (-UploadRelease was not specified)." -ForegroundColor Gray
    Write-Host "  To upload, run: .\Create-Installer.ps1 -Version '$Version' -UploadRelease" -ForegroundColor Gray
}

Write-Host "`nAll done!" -ForegroundColor Green

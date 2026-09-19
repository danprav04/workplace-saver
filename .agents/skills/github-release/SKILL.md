---
name: github-release
description: >-
  Build, package, and publish GitHub Releases for Workplace Saver.
  Use whenever creating, publishing, updating, or automating GitHub releases,
  packaging installers and portable zip archives, or generating release checksums.
---

# GitHub Release Skill for Workplace Saver

This skill guides the preparation, packaging, and publishing of official releases for Workplace Saver on GitHub.

## Release Policy & Requirements

1. **Minimalist Release Description**:
   - Do **NOT** include long descriptive marketing or change text in the GitHub release notes.
   - Release Title: `Workplace Saver v<Version>` (e.g. `Workplace Saver v1.0.2`).
   - Release Notes: Exactly the release name and version (e.g. `Workplace Saver v1.0.2`), with no additional text.
2. **Tag and Commit Alignment**:
   - All source code changes and tests must be committed and pushed to `main` before releasing.
   - The release tag must be formatted as `v<Version>` (e.g. `v1.0.2`).
   - The tag and release must target the exact commit on `origin/main` representing that version.
3. **Mandatory Release Artifacts**:
   - `WorkplaceSaver-Setup-v<Version>.exe` (Inno Setup 6 installer)
   - `WorkplaceSaver-v<Version>-Portable.zip` (standalone archive)
   - `checksums.txt` (SHA-256 digests for both binaries)

---

## Step-by-Step Publishing Workflow

### 1. Verify and Commit
Ensure the working tree is clean and all tests pass:
```powershell
$env:DOTNET_ROOT = "$env:LocalAppData\Microsoft\dotnet"
& "$env:DOTNET_ROOT\dotnet.exe" test
git status
```
Commit and push changes to GitHub:
```powershell
git add -A
git commit -m "feat/fix: <description>"
git push origin main
```

### 2. Run the Release Script
Workplace Saver includes an automated packaging and release script at `scripts/Create-Installer.ps1`.

Run the script specifying the version and the `-UploadRelease` flag:
```powershell
.\scripts\Create-Installer.ps1 -Version "<Version>" -UploadRelease
```
Example:
```powershell
.\scripts\Create-Installer.ps1 -Version "1.0.2" -UploadRelease
```

### 3. Authentication Handling
The script automatically queries the Windows Git Credential Manager (`C:\Program Files\Git\mingw64\bin\git-credential-manager.exe`) to obtain the user's GitHub Personal Access Token if `GH_TOKEN` is not already set in the environment.

If running in a custom environment where Git Credential Manager is unavailable, provide `GH_TOKEN`:
```powershell
$env:GH_TOKEN = "<github-token>"
.\scripts\Create-Installer.ps1 -Version "<Version>" -UploadRelease
```

### 4. Verification
Verify that the release was published successfully with the correct tag, commit, minimal notes, and attached artifacts:
```powershell
gh release view v<Version>
```

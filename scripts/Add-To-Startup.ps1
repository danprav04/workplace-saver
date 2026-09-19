$targetExe = Join-Path (Split-Path $PSScriptRoot -Parent) "publish\WorkplaceSaver.exe"

if (-not (Test-Path $targetExe)) {
    Write-Host "Publishing WorkplaceSaver first..." -ForegroundColor Cyan
    $dotnet = "$env:LocalAppData\Microsoft\dotnet\dotnet.exe"
    & $dotnet publish (Join-Path (Split-Path $PSScriptRoot -Parent) "src\WorkplaceSaver\WorkplaceSaver.csproj") -c Release -o (Join-Path (Split-Path $PSScriptRoot -Parent) "publish")
}

# Add to Current User Run Registry with --startup flag so it runs silently in the tray
Set-ItemProperty -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -Name "WorkplaceSaver" -Value "`"$targetExe`" --startup"

Write-Host "✅ Workplace Saver added to Windows Startup successfully!" -ForegroundColor Green
Write-Host "Registry path: HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -ForegroundColor DarkGray
Write-Host "Target: $targetExe" -ForegroundColor DarkGray

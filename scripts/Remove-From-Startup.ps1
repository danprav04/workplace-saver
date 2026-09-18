Remove-ItemProperty -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -Name "WorkplaceSaver" -ErrorAction SilentlyContinue
$startupShortcut = "$env:APPDATA\Microsoft\Windows\Start Menu\Programs\Startup\WorkplaceSaver.lnk"
if (Test-Path $startupShortcut) {
    Remove-Item $startupShortcut -Force
}
Write-Host "✅ Workplace Saver removed from Windows Startup." -ForegroundColor Yellow

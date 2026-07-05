# Run once:  powershell -ExecutionPolicy Bypass -File .\make-shortcuts.ps1
# Creates "Start/Stop LAN Audio" shortcuts on your Desktop that target
# powershell.exe, so Windows lets you Pin them to the taskbar or Start.

$ws      = New-Object -ComObject WScript.Shell
$desktop = [Environment]::GetFolderPath('Desktop')
$dir     = $PSScriptRoot
$psExe   = "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe"

function New-LauncherShortcut($name, $script, $iconIndex) {
    $lnk = $ws.CreateShortcut("$desktop\$name.lnk")
    $lnk.TargetPath       = $psExe
    $lnk.Arguments        = "-ExecutionPolicy Bypass -NoProfile -File `"$dir\$script`""
    $lnk.WorkingDirectory = $dir
    $lnk.IconLocation     = "$env:SystemRoot\System32\SHELL32.dll,$iconIndex"
    $lnk.Save()
    Write-Host "Created: $desktop\$name.lnk"
}

New-LauncherShortcut "Start LAN Audio" "run.ps1"  137   # green-ish
New-LauncherShortcut "Stop LAN Audio"  "stop.ps1" 131   # red-ish

Write-Host ""
Write-Host "Done. Right-click a shortcut on your Desktop -> Pin to taskbar / Pin to Start."

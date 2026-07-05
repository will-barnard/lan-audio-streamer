# Turn the Windows receiver OFF.
$p = Get-Process -Name "LANAudioReceiver" -ErrorAction SilentlyContinue
if ($p) {
    $p | Stop-Process -Force
    Write-Host "Receiver stopped."
} else {
    Write-Host "Receiver was not running."
}

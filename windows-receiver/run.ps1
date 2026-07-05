# Turn the Windows receiver ON. Run from PowerShell:  .\run.ps1
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

if (-not (Test-Path .env)) {
    Write-Host "No .env found. Copy .env.example to .env and edit it first."
    exit 1
}

Write-Host "Building (first run may take a moment)..."
dotnet build -c Release | Out-Host

Write-Host "Starting LAN Audio Bridge receiver. Press Ctrl-C to stop."
# The app reads .env from the current directory.
dotnet run -c Release --no-build -- $args

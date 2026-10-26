param(
    [switch]$Run
)

$ErrorActionPreference = 'Stop'
$env:DOTNET_ROOT = 'C:\Users\opc\AppData\Local\Microsoft\dotnet'
$env:PATH = "$env:DOTNET_ROOT;C:\Users\opc\AppData\Local\Microsoft\WindowsApps;$env:PATH"

$projectDir = Join-Path (Split-Path -Parent $PSScriptRoot) 'src\Flinch.App'
Set-Location $projectDir

Write-Host "Building WinUI 3 project at $projectDir..." -ForegroundColor Cyan
dotnet build -c Debug --verbosity normal

if ($Run) {
    Write-Host "Launching WinUI 3 project..." -ForegroundColor Green
    winapp run
}

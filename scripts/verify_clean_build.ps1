# ==============================================================================
# Flinch Master Clean Build & Offline Verification Script
# Verifies zero secret leaks, runs all 36 unit tests offline, and builds all targets.
# ==============================================================================

$ErrorActionPreference = "Stop"

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host "  Flinch Fresh-Checkout Build & Security Verification     " -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan

$dotnet = "C:\Users\opc\AppData\Local\Microsoft\dotnet\dotnet.exe"
if (-not (Test-Path $dotnet)) {
    $dotnet = "dotnet"
}

$root = Resolve-Path "$PSScriptRoot\.."

# Step 1: Security & Privacy Audit
Write-Host "`n>>> [1/4] Running Privacy & Secret-Leak Audit..." -ForegroundColor Yellow
& powershell -ExecutionPolicy Bypass -File "$root\scripts\audit_privacy_and_secrets.ps1"
if ($LASTEXITCODE -ne 0) {
    Write-Host "[FATAL] Privacy/Secret audit failed!" -ForegroundColor Red
    exit 1
}

# Step 2: Offline Unit & Regression Tests (36 Tests)
Write-Host "`n>>> [2/4] Running 100% Offline Core Test Suite..." -ForegroundColor Yellow
& $dotnet test "$root\tests\Flinch.Core.Tests\Flinch.Core.Tests.csproj" --verbosity normal --no-restore
if ($LASTEXITCODE -ne 0) {
    Write-Host "[FATAL] Unit tests failed!" -ForegroundColor Red
    exit 1
}

# Step 3: ASP.NET Core API Build
Write-Host "`n>>> [3/4] Building ASP.NET Core Backend (Flinch.Api)..." -ForegroundColor Yellow
& powershell -ExecutionPolicy Bypass -File "$root\scripts\build_api.ps1"
if ($LASTEXITCODE -ne 0) {
    Write-Host "[FATAL] Flinch.Api build failed!" -ForegroundColor Red
    exit 1
}

# Step 4: WinUI 3 Desktop App Build
Write-Host "`n>>> [4/4] Building WinUI 3 Desktop Client (Flinch.App)..." -ForegroundColor Yellow
& powershell -ExecutionPolicy Bypass -File "$root\scripts\build_winui.ps1"
if ($LASTEXITCODE -ne 0) {
    Write-Host "[FATAL] Flinch.App build failed!" -ForegroundColor Red
    exit 1
}

Write-Host "`n==========================================================" -ForegroundColor Green
Write-Host "  VERIFICATION SUCCESSFUL: 100% Offline & Clean Checkout!  " -ForegroundColor Green
Write-Host "==========================================================" -ForegroundColor Green
exit 0

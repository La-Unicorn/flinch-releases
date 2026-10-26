# ==============================================================================
# Flinch Release Compiler & Package Script
# Compiles self-contained executable for Windows x64 into dist\Flinch-Release
# ==============================================================================

$dotnet = "C:\Users\opc\AppData\Local\Microsoft\dotnet\dotnet.exe"
if (-not (Test-Path $dotnet)) {
    $dotnet = "dotnet"
}

$root = Resolve-Path "$PSScriptRoot\.."
$outDir = "$root\dist\Flinch-Release"

Write-Host "Compiling Flinch Desktop Release to $outDir..." -ForegroundColor Cyan

& $dotnet publish "$root\src\Flinch.App\Flinch.App.csproj" `
    -c Release `
    -r win-x64 `
    --self-contained false `
    -p:WindowsPackageType=None `
    -p:PublishTrimmed=false `
    -o $outDir

if ($LASTEXITCODE -eq 0) {
    Write-Host "`n[SUCCESS] Flinch Desktop compiled successfully to $outDir!" -ForegroundColor Green
} else {
    Write-Host "`n[FAILED] Compilation failed." -ForegroundColor Red
}

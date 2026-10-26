# Compiles and runs the Phase 1 Screen Service Test Suite
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$fw = 'C:\WINDOWS\Microsoft.NET\Framework64\v4.0.30319'
$csc = Join-Path $fw 'csc.exe'

$wpf = @('PresentationCore','PresentationFramework','WindowsBase','UIAutomationClient','UIAutomationTypes') | ForEach-Object { "/r:$fw\WPF\$_.dll" }
$sys = @('System.Xaml','System.Windows.Forms','System.Drawing','System.Web.Extensions','System.Net.Http','System.Security') | ForEach-Object { "/r:$fw\$_.dll" }
$speechDll = (Get-ChildItem -Path "C:\Windows\Microsoft.NET\assembly\GAC_MSIL\System.Speech" -Filter "System.Speech.dll" -Recurse | Select-Object -First 1).FullName
$speech = "/r:$speechDll"

$gen = Join-Path $root 'src\BuildConfig.g.cs'
$flinchSrc = Join-Path $root 'src\Flinch.cs'
$testSrc = Join-Path $PSScriptRoot 'TestScreenServices.cs'
$testExe = Join-Path $PSScriptRoot 'TestScreenServices.exe'

Write-Host "Compiling TestScreenServices.exe..." -ForegroundColor Cyan
& $csc /nologo /target:exe /main:FlinchApp.Tests.TestScreenServices /out:$testExe $wpf $sys $speech $flinchSrc $gen $testSrc

if ($LASTEXITCODE -ne 0) {
    Write-Error "Test compile failed with exit code $LASTEXITCODE"
    exit 1
}

Write-Host "Running TestScreenServices.exe..." -ForegroundColor Green
& $testExe
exit $LASTEXITCODE

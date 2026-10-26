# Builds Flinch.exe and the one-file installer (dist\FlinchSetup.exe).
# Reads only the PUBLIC Supabase URL + anon key from .env and compiles them in.
# No AI keys are ever read here; they live in Supabase (flinch_private.secrets).
param(
    [switch]$Publish
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$fw   = 'C:\WINDOWS\Microsoft.NET\Framework64\v4.0.30319'
$csc  = Join-Path $fw 'csc.exe'

$url = ''; $anon = ''; $repo = 'La-Unicorn/flinch-releases'; $tag = 'v0.1.0'
foreach ($line in Get-Content (Join-Path $root '.env')) {
    if ($line -match '^\s*SUPABASE_URL\s*=\s*(.+)$')      { $url  = $matches[1].Trim() }
    if ($line -match '^\s*SUPABASE_ANON_KEY\s*=\s*(.+)$') { $anon = $matches[1].Trim() }
    if ($line -match '^\s*GITHUB_REPO\s*=\s*(.+)$')       { $repo = $matches[1].Trim() }
    if ($line -match '^\s*GITHUB_TAG\s*=\s*(.+)$')        { $tag  = $matches[1].Trim() }
}
if (-not $url -or -not $anon) { throw 'SUPABASE_URL / SUPABASE_ANON_KEY missing in .env' }

$buildTimeUtc = [DateTime]::UtcNow.ToString("o")
$version = $tag.TrimStart('v')

$unicornData = ""
$unicornFile = Join-Path $root 'src\UnicornData.txt'
if (Test-Path $unicornFile) {
    $unicornData = (Get-Content $unicornFile -Raw).Trim()
}

$gen = Join-Path $root 'src\BuildConfig.g.cs'
@"
namespace FlinchApp { internal static class BuildConfig {
    public const string SupabaseUrl = "$url";
    public const string SupabaseAnonKey = "$anon";
    public const string GitHubRepo = "$repo";
    public const string BuildVersion = "$version";
    public const string BuildTimeUtc = "$buildTimeUtc";
    public const string UnicornSvgPath = @"$unicornData";
} }
"@ | Set-Content -Path $gen -Encoding UTF8

$wpf = @('PresentationCore','PresentationFramework','WindowsBase','UIAutomationClient','UIAutomationTypes') | ForEach-Object { "/r:$fw\WPF\$_.dll" }
$sys = @('System.Xaml','System.Windows.Forms','System.Drawing','System.Web.Extensions','System.Net.Http','System.Security') | ForEach-Object { "/r:$fw\$_.dll" }
$speechDll = (Get-ChildItem -Path "C:\Windows\Microsoft.NET\assembly\GAC_MSIL\System.Speech" -Filter "System.Speech.dll" -Recurse | Select-Object -First 1).FullName
$speech = "/r:$speechDll"

$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force -Path $dist | Out-Null
$appExe = Join-Path $dist 'Flinch.exe'

$ico = Join-Path $root 'flinch.ico'
if (-not (Test-Path $ico)) { throw "flinch.ico missing at $ico" }
$catPng = Join-Path $root 'cat_logo.png'
if (-not (Test-Path $catPng)) { throw "cat_logo.png missing at $catPng" }

$manifest = Join-Path $root 'app.manifest'
if (-not (Test-Path $manifest)) { throw "app.manifest missing at $manifest" }

Get-Process -Name Flinch -ErrorAction SilentlyContinue | Stop-Process -Force
& $csc /nologo /target:winexe /win32icon:$ico "/win32manifest:$manifest" "/resource:$ico,flinch.ico" "/resource:$catPng,cat_logo.png" /out:$appExe $wpf $sys $speech (Join-Path $root 'src\Flinch.cs') $gen
if ($LASTEXITCODE -ne 0) { throw 'Flinch.exe compile failed' }

$setupExe = Join-Path $dist 'FlinchSetup.exe'
& $csc /nologo /target:winexe /win32icon:$ico "/win32manifest:$manifest" "/resource:$appExe,Flinch.exe" "/resource:$ico,flinch.ico" "/resource:$catPng,cat_logo.png" /out:$setupExe $wpf '/r:System.Xaml.dll' '/r:System.Windows.Forms.dll' (Join-Path $root 'installer\FlinchInstaller.cs')
if ($LASTEXITCODE -ne 0) { throw 'Installer compile failed' }

# Copy binaries to workspace root for quick access
$workspaceRoot = Split-Path -Parent $root
Copy-Item $appExe -Destination (Join-Path $workspaceRoot 'Flinch.exe') -Force
Copy-Item $setupExe -Destination (Join-Path $workspaceRoot 'FlinchSetup.exe') -Force

Write-Host "Built: $setupExe" -ForegroundColor Green

if ($Publish) {
    Write-Host "Publishing FlinchSetup.exe to GitHub Releases..." -ForegroundColor Cyan
    & (Join-Path $root 'publish_release.ps1')
}


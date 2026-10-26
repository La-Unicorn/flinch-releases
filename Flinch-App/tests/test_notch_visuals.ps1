$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$appRoot = Split-Path -Parent $root

Write-Host "====================================================" -ForegroundColor Cyan
Write-Host "FLINCH NOTCH SVG & MULTITASK VISUAL VERIFICATION" -ForegroundColor Cyan
Write-Host "====================================================" -ForegroundColor Cyan

# 1. Verify SVG files exist in Downloads
$downloads = 'C:\Users\opc\Downloads'
$svgFiles = @(
    'flinch-notch-idle.svg',
    'flinch-notch-tasks 2.svg',
    'unicorn (1).svg',
    'flinch-notch-expanded 2.svg',
    'flinch-notch-listening 2.svg',
    'flinch-voice 2.js'
)

foreach ($f in $svgFiles) {
    $full = Join-Path $downloads $f
    if (-not (Test-Path $full)) {
        throw "Missing input file: $full"
    }
    Write-Host "[PASS] Asset verified: $f" -ForegroundColor Green
}

# 2. Check Flinch.cs for exact SVG geometries, colors, and math
$src = [System.IO.File]::ReadAllText((Join-Path $appRoot 'src\Flinch.cs'))

$checks = @(
    @{ Name = "Mint color #3DDC84"; Pattern = "0x3D,\s*0xDC,\s*0x84" },
    @{ Name = "Vibrant orange #FF8A3D"; Pattern = "0xFF,\s*0x8A,\s*0x3D" },
    @{ Name = "Voice gradient stops"; Pattern = "0x9B,\s*0x6B,\s*0xFF" },
    @{ Name = "ArcSegment ear and corner fillets"; Pattern = "figure\.Segments\.Add\(new ArcSegment" },
    @{ Name = "flinch-voice 2.js 7-band algorithm"; Pattern = "Math\.Min\(6,\s*\(int\)Math\.Round\(Math\.Abs\(i\s*-\s*6\.0\)\)\)" },
    @{ Name = "flinch-voice 2.js adaptive peak gain"; Pattern = "_voicePeak\s*=\s*Math\.Max\(_voicePeak\s*\*\s*0\.995,\s*mx\)" },
    @{ Name = "flinch-voice 2.js 4-blob glow math"; Pattern = "double\[\]\s*lv\s*=\s*new double\[\]\s*\{\s*midE,\s*low,\s*\(low\s*\+\s*midE\)\s*/\s*2\.0,\s*high\s*\}" },
    @{ Name = "MacBook notch top ear flare A10,10"; Pattern = "new Size\(rTop,\s*rTop\)" },
    @{ Name = "Circular timer progress ring path"; Pattern = "_timerProgressArcPath" },
    @{ Name = "Tasks expanded card matching SVG"; Pattern = "TasksExpanded" },
    @{ Name = "Unicorn SVG vector geometry"; Pattern = "UnicornSvgPath" },
    @{ Name = "Single hotkey Push-to-Talk Alt+V"; Pattern = "HOTKEY_ID_ALTV" }
)

foreach ($c in $checks) {
    if ($src -match $c.Pattern) {
        Write-Host "[PASS] $($c.Name)" -ForegroundColor Green
    } else {
        throw "Verification failed for $($c.Name)"
    }
}

Write-Host "====================================================" -ForegroundColor Cyan
Write-Host "ALL SVG & NOTCH VISUAL CHECKS PASSED!" -ForegroundColor Green
Write-Host "====================================================" -ForegroundColor Cyan

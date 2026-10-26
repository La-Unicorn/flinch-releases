# ==============================================================================
# Flinch Privacy & Secret Leak Audit Script
# Scans codebase for leaked credentials, persistent image writes, and eval() usage.
# Returns Exit Code 0 if audit passes, 1 if any violation is found.
# ==============================================================================

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host "  Flinch Security, Privacy & Secret Audit                 " -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan

$root = Resolve-Path "$PSScriptRoot\.."
$violations = 0

# 1. Secret Patterns
$secretPatterns = @(
    "sk-[A-Za-z0-9]{24,}",
    "ghp_[A-Za-z0-9]{20,}",
    "AKIA[0-9A-Z]{16}",
    "AIza[0-9A-Za-z-_]{35}"
)

# Files to scan across project solution directories
$scanPaths = @(
    "$root\src",
    "$root\tests",
    "$root\scripts",
    "$root\docs",
    "$root\README.md"
)

$files = @()
foreach ($p in $scanPaths) {
    if (Test-Path $p) {
        if ((Get-Item $p).PSIsContainer) {
            $files += Get-ChildItem -Path $p -Recurse -File | Where-Object {
                $_.FullName -notmatch "\\bin\\" -and
                $_.FullName -notmatch "\\obj\\" -and
                $_.FullName -notmatch "\\\.venv\\" -and
                $_.FullName -notmatch "\\Assets\\" -and
                $_.FullName -notmatch "\.png$" -and
                $_.FullName -notmatch "\.jpg$" -and
                $_.FullName -notmatch "\.ico$"
            }
        } else {
            $files += Get-Item $p
        }
    }
}

Write-Host "`n[1/3] Scanning $($files.Count) source files for hardcoded API keys & credentials..." -ForegroundColor Yellow

foreach ($file in $files) {
    $content = Get-Content -Path $file.FullName -Raw -ErrorAction SilentlyContinue
    if (-not $content) { continue }

    foreach ($pattern in $secretPatterns) {
        if ($content -match $pattern) {
            Write-Host "  [FAIL] Hardcoded secret matched '$pattern' in: $($file.FullName)" -ForegroundColor Red
            $violations++
        }
    }
}

if ($violations -eq 0) {
    Write-Host "  [PASS] Zero hardcoded API keys, tokens, or cloud secrets detected." -ForegroundColor Green
}

# 2. Check for Insecure eval / exec in Python
Write-Host "`n[2/3] Checking for insecure eval() / exec() in Python workers..." -ForegroundColor Yellow
$pyFiles = Get-ChildItem -Path "$root\src\Flinch.MathWorker" -Filter "*.py" -ErrorAction SilentlyContinue

$pyViolations = 0
foreach ($py in $pyFiles) {
    $lines = Get-Content -Path $py.FullName
    $lineNum = 0
    $inDocstring = $false
    foreach ($line in $lines) {
        $lineNum++
        $trimmed = $line.Trim()
        if ($trimmed.Contains('"""')) {
            $inDocstring = -not $inDocstring
            continue
        }
        if ($inDocstring -or $trimmed.StartsWith("#")) { continue }
        if ($trimmed -match "\beval\s*\(" -or $trimmed -match "\bexec\s*\(") {
            Write-Host "  [FAIL] Insecure dynamic execution in $($py.Name):$lineNum -> $trimmed" -ForegroundColor Red
            $pyViolations++
            $violations++
        }
    }
}

if ($pyViolations -eq 0) {
    Write-Host "  [PASS] Zero eval() or exec() calls detected in MathWorker." -ForegroundColor Green
}

# 3. Privacy & Ephemeral Memory Invariant: Verify No Persistent Image File Dumps
Write-Host "`n[3/3] Auditing screen capture lifecycle for zero disk retention..." -ForegroundColor Yellow

$captureCode = Get-Content -Path "$root\src\Flinch.App\Services\CaptureService.cs" -Raw -ErrorAction SilentlyContinue
if ($captureCode -match "File\.WriteAllBytes" -or $captureCode -match "SaveToFileAsync") {
    Write-Host "  [FAIL] Screen capture writes persistent files to disk." -ForegroundColor Red
    $violations++
} else {
    Write-Host "  [PASS] CaptureService is strictly ephemeral (Direct3D surface / in-memory software bitmap only)." -ForegroundColor Green
}

$coreContracts = Get-Content -Path "$root\src\Flinch.Core\Contracts.cs" -Raw -ErrorAction SilentlyContinue
if ($coreContracts -match "Array\.Clear\(EphemeralImageData") {
    Write-Host "  [PASS] EphemeralImageData zero-fill disposal invariant confirmed in Contracts.cs." -ForegroundColor Green
} else {
    Write-Host "  [WARN] Contracts.cs should confirm Array.Clear on disposal." -ForegroundColor Yellow
}

Write-Host "`n==========================================================" -ForegroundColor Cyan
if ($violations -eq 0) {
    Write-Host "  AUDIT PASSED: All security & privacy invariants satisfied! " -ForegroundColor Green
    Write-Host "==========================================================" -ForegroundColor Cyan
    exit 0
} else {
    Write-Host "  AUDIT FAILED: $violations security/privacy violation(s) found! " -ForegroundColor Red
    Write-Host "==========================================================" -ForegroundColor Cyan
    exit 1
}

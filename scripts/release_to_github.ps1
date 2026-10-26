# ==============================================================================
# Flinch GitHub Automated Release Script
# Packages release zip, pushes git commits, and publishes official GitHub Release.
# ==============================================================================

$ErrorActionPreference = "Stop"
$root = Resolve-Path "$PSScriptRoot\.."

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host "  Flinch Official GitHub Release Pipeline (v1.0.0)        " -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan

# 1. Load Token securely from environment or legacy .env
$token = $env:GITHUB_TOKEN
if (-not $token) {
    $envFile = "$root\Flinch-App\.env"
    if (Test-Path $envFile) {
        $line = Get-Content $envFile | Where-Object { $_ -match "^GITHUB_TOKEN=" }
        if ($line) {
            $token = $line.Substring("GITHUB_TOKEN=".Length).Trim()
        }
    }
}

if (-not $token) {
    Write-Host "[ERROR] GITHUB_TOKEN not found!" -ForegroundColor Red
    exit 1
}

$repo = "La-Unicorn/flinch-releases"
$tag = "v1.0.0"
$releaseTitle = "Flinch v1.0.0 - Privacy-First Screen-Aware Voice Companion"

# 2. Package Release Zip
$releaseDir = "$root\dist\Flinch-Release"
$zipPath = "$root\dist\Flinch-Windows-x64-v1.0.0.zip"

Write-Host "`n[1/4] Compressing release package to $zipPath..." -ForegroundColor Yellow
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path "$releaseDir\*" -DestinationPath $zipPath -CompressionLevel Optimal
$zipItem = Get-Item $zipPath
$zipSizeMb = [math]::Round($zipItem.Length / 1MB, 2)
Write-Host "  [SUCCESS] Created release archive size: $zipSizeMb MB" -ForegroundColor Green

# 3. Setup Remote and Push Git Commits and Tags
Write-Host "`n[2/4] Pushing source commits and tag to GitHub ($repo)..." -ForegroundColor Yellow

$env:GIT_TERMINAL_PROMPT = "0"
$env:GCM_INTERACTIVE = "never"

$remoteUrl = "https://$($token)@github.com/$repo.git"

$existingRemotes = git remote
if ($existingRemotes -contains "origin") {
    git remote remove origin
}
git remote add origin $remoteUrl

try {
    Write-Host "  Pushing branch 'main' to origin..."
    git -c credential.helper= push -u origin main --force
    Write-Host "  Pushing tag '$tag' to origin..."
    git -c credential.helper= push origin $tag --force
    Write-Host "  [SUCCESS] Source code and tag pushed to GitHub repository!" -ForegroundColor Green
} catch {
    Write-Host "  [WARN] Git push notice: $($_.Exception.Message)" -ForegroundColor Yellow
}

# 4. Create GitHub Release via REST API
Write-Host "`n[3/4] Creating official GitHub Release for tag $tag..." -ForegroundColor Yellow

$releaseNotes = @'
# Flinch v1.0.0 - Official Release

Flinch is a privacy-first Windows screen-aware voice companion built with .NET 10, WinUI 3, and Windows App SDK.

### What is New and Included:
- **Synchronized Speech and Animated Screen Drawing:** Borderless direct overlay with dynamic cubic ease-out highlighting, directional arrows, and mathematical equation cards synchronized with real-time speech synthesis playback.
- **Verified Math Engine (AST + SymPy):** Sandbox equation solver supporting linear, quadratic, exact fractions, and residual evaluation with zero dynamic code execution.
- **Real-Time Web Search (Exa.ai):** Semantic live web search integration preventing outdated responses.
- **Voice Transcription (Whisper-1):** Speech-to-text pipeline powered by OpenAI Whisper for high-accuracy voice input.
- **Multi-Turn Conversation Memory (RAG):** Context-aware conversation tracking with intelligent ambiguity handling.
- **Strict Privacy Invariants:** Ephemeral in-memory screen buffers, zero disk dumps, prompt Direct3D surface disposal on cancel, and zero provider secrets in the desktop client binary.

### Installation and Run:
1. Extract `Flinch-Windows-x64-v1.0.0.zip`.
2. Run `Flinch.App.exe`.
3. Press `Escape` at any time to immediately dismiss all active overlays and speech.
'@

$headers = @{
    "Authorization" = "Bearer $token"
    "Accept"        = "application/vnd.github.v3+json"
    "User-Agent"    = "Flinch-Release-Tool"
}

# Check if release already exists
$existingRelease = $null
try {
    $existingRelease = Invoke-RestMethod -Uri "https://api.github.com/repos/$repo/releases/tags/$tag" -Headers $headers -Method Get
    Write-Host "  Release already exists with ID: $($existingRelease.id). Updating..."
} catch {
    # Release does not exist yet
}

$releaseBody = @{
    tag_name         = $tag
    target_commitish = "main"
    name             = $releaseTitle
    body             = $releaseNotes
    draft            = $false
    prerelease       = $false
} | ConvertTo-Json

$releaseObj = $null
if ($existingRelease) {
    $releaseObj = Invoke-RestMethod -Uri "https://api.github.com/repos/$repo/releases/$($existingRelease.id)" `
        -Headers $headers `
        -Method Patch `
        -Body $releaseBody `
        -ContentType "application/json"
} else {
    $releaseObj = Invoke-RestMethod -Uri "https://api.github.com/repos/$repo/releases" `
        -Headers $headers `
        -Method Post `
        -Body $releaseBody `
        -ContentType "application/json"
}

$uploadUrlTemplate = $releaseObj.upload_url
$uploadBase = $uploadUrlTemplate.Substring(0, $uploadUrlTemplate.IndexOf("{"))

Write-Host "  [SUCCESS] GitHub Release created: $($releaseObj.html_url)" -ForegroundColor Green

# 5. Upload Release Asset Zip
Write-Host "`n[4/4] Uploading asset package ($zipSizeMb MB) to GitHub Release..." -ForegroundColor Yellow

if ($releaseObj.assets) {
    foreach ($asset in $releaseObj.assets) {
        if ($asset.name -eq "Flinch-Windows-x64-v1.0.0.zip") {
            Write-Host "  Deleting existing asset $($asset.name)..."
            Invoke-RestMethod -Uri $asset.url -Headers $headers -Method Delete | Out-Null
        }
    }
}

$uploadUri = "https://uploads.github.com/repos/$repo/releases/$($releaseObj.id)/assets?name=Flinch-Windows-x64-v1.0.0.zip"

$uploadHeaders = @{
    "Authorization" = "Bearer $token"
    "Accept"        = "application/vnd.github.v3+json"
    "User-Agent"    = "Flinch-Release-Tool"
}

$uploadedAsset = Invoke-RestMethod -Uri $uploadUri -Headers $uploadHeaders -Method Post -InFile $zipPath -ContentType "application/zip"
Write-Host "  [SUCCESS] Asset uploaded successfully: $($uploadedAsset.browser_download_url)" -ForegroundColor Green

# Cleanup remote URL to avoid storing token in git config
git remote set-url origin "https://github.com/$repo.git"

Write-Host "`n==========================================================" -ForegroundColor Cyan
Write-Host "  RELEASE COMPLETE!" -ForegroundColor Green
Write-Host "  Release URL: $($releaseObj.html_url)" -ForegroundColor Green
Write-Host "  Download:    $($uploadedAsset.browser_download_url)" -ForegroundColor Green
Write-Host "==========================================================" -ForegroundColor Cyan

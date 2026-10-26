# Automated GitHub Release Publisher for FlinchSetup.exe
# Deletes old asset if present and uploads the newly compiled installer
param(
    [string]$Tag = "",
    [string]$InstallerPath = ""
)

$ErrorActionPreference = 'Stop'
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

# 1. Read configuration from .env
$envFile = Join-Path $scriptDir '.env'
if (-not (Test-Path $envFile)) {
    throw "Missing .env file at $envFile"
}

$repo = ""
$token = ""
$defaultTag = "v0.1.0"

foreach ($line in Get-Content $envFile) {
    if ($line -match '^\s*GITHUB_REPO\s*=\s*(.+)$')  { $repo  = $matches[1].Trim() }
    if ($line -match '^\s*GITHUB_TOKEN\s*=\s*(.+)$') { $token = $matches[1].Trim() }
    if ($line -match '^\s*GITHUB_TAG\s*=\s*(.+)$')   { $defaultTag = $matches[1].Trim() }
}

if (-not $repo -or -not $token) {
    throw "GITHUB_REPO or GITHUB_TOKEN not configured in $envFile"
}

if (-not $Tag) {
    $Tag = $defaultTag
}

if (-not $InstallerPath) {
    $candidates = @(
        (Join-Path $scriptDir 'dist\FlinchSetup.exe'),
        (Join-Path (Split-Path -Parent $scriptDir) 'FlinchSetup.exe')
    )
    foreach ($cand in $candidates) {
        if (Test-Path $cand) {
            $InstallerPath = $cand
            break
        }
    }
}

if (-not (Test-Path $InstallerPath)) {
    throw "Installer binary not found at $InstallerPath. Run build.ps1 first."
}

$fileSize = (Get-Item $InstallerPath).Length
Write-Host "Target Repository: $repo" -ForegroundColor Cyan
Write-Host "Target Tag       : $Tag" -ForegroundColor Cyan
Write-Host "Installer Asset  : $InstallerPath ($([math]::Round($fileSize / 1KB, 1)) KB)" -ForegroundColor Cyan

# 2. Setup Headers
$headers = @{
    "Authorization"        = "Bearer $token"
    "Accept"               = "application/vnd.github+json"
    "User-Agent"           = "Flinch-Release-Bot/1.0"
    "X-GitHub-Api-Version" = "2022-11-28"
}

# 3. Retrieve release by tag (or create it if missing)
$releaseUrl = "https://api.github.com/repos/$repo/releases/tags/$Tag"
$release = $null

try {
    $release = Invoke-RestMethod -Uri $releaseUrl -Headers $headers -Method Get
    Write-Host "Found existing release: $($release.name) (ID: $($release.id))" -ForegroundColor Green
}
catch {
    $statusCode = $_.Exception.Response.StatusCode.value__
    if ($statusCode -eq 404) {
        Write-Host "Release for tag '$Tag' not found. Creating new release..." -ForegroundColor Yellow
        $createBody = @{
            tag_name         = $Tag
            name             = "Flinch $Tag"
            body             = "Automated release of Flinch Desktop AI Voice Companion with Screen Awareness & Overlays."
            draft            = $false
            prerelease       = $false
        } | ConvertTo-Json

        $createUrl = "https://api.github.com/repos/$repo/releases"
        $release = Invoke-RestMethod -Uri $createUrl -Headers $headers -Method Post -Body $createBody -ContentType "application/json"
        Write-Host "Created release: $($release.name) (ID: $($release.id))" -ForegroundColor Green
    }
    else {
        throw "Failed to fetch release from GitHub: $_"
    }
}

$releaseId = $release.id

# 4. Check for existing assets named FlinchSetup.exe or Flinch.exe and delete them
if ($release.assets -and $release.assets.Count -gt 0) {
    foreach ($asset in $release.assets) {
        if ($asset.name -eq "FlinchSetup.exe" -or $asset.name -eq "Flinch.exe") {
            Write-Host "Deleting existing asset '$($asset.name)' (ID: $($asset.id))..." -ForegroundColor Yellow
            $deleteUrl = "https://api.github.com/repos/$repo/releases/assets/$($asset.id)"
            Invoke-RestMethod -Uri $deleteUrl -Headers $headers -Method Delete | Out-Null
            Write-Host "Successfully deleted previous $($asset.name) asset." -ForegroundColor Green
        }
    }
}

# 5. Upload new FlinchSetup.exe and Flinch.exe
$uploadHeaders = @{
    "Authorization"        = "Bearer $token"
    "Accept"               = "application/vnd.github+json"
    "User-Agent"           = "Flinch-Release-Bot/1.0"
    "X-GitHub-Api-Version" = "2022-11-28"
    "Content-Type"         = "application/octet-stream"
}

$appExePath = Join-Path (Split-Path $InstallerPath -Parent) 'Flinch.exe'
if (-not (Test-Path $appExePath)) {
    $appExePath = Join-Path (Split-Path -Parent $scriptDir) 'Flinch.exe'
}

$filesToUpload = @{ "FlinchSetup.exe" = $InstallerPath }
if (Test-Path $appExePath) {
    $filesToUpload["Flinch.exe"] = $appExePath
}

foreach ($item in $filesToUpload.GetEnumerator()) {
    $name = $item.Key
    $path = $item.Value
    Write-Host "Uploading $name ($([math]::Round((Get-Item $path).Length / 1KB, 1)) KB) to release..." -ForegroundColor Cyan
    $uploadUrl = "https://uploads.github.com/repos/$repo/releases/$releaseId/assets?name=$name"
    $bytes = [System.IO.File]::ReadAllBytes($path)
    $newAsset = Invoke-RestMethod -Uri $uploadUrl -Headers $uploadHeaders -Method Post -Body $bytes
    Write-Host "  -> Published $name ($($newAsset.browser_download_url))" -ForegroundColor Green
}

Write-Host "====================================================" -ForegroundColor Green
Write-Host "SUCCESSFULLY PUBLISHED TO GITHUB RELEASES!" -ForegroundColor Green
Write-Host "Release Tag   : $Tag" -ForegroundColor White
Write-Host "Release URL   : https://github.com/$repo/releases/tag/$Tag" -ForegroundColor Cyan
Write-Host "====================================================" -ForegroundColor Green

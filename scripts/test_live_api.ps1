# 1. Test /health
Write-Host "--- Testing GET /health ---"
$health = Invoke-RestMethod -Uri "http://127.0.0.1:5055/health" -Method Get
Write-Host "Health Status: $($health.status), Provider: $($health.provider)"

# 2. Test /api/answer without auth header (Expect 401)
Write-Host "`n--- Testing POST /api/answer without Auth Header ---"
try {
    $res = Invoke-RestMethod -Uri "http://127.0.0.1:5055/api/answer" -Method Post -Body '{"query":"hello"}' -ContentType "application/json"
    Write-Host "Unexpected Success: $res"
} catch {
    Write-Host "Correctly rejected with: $($_.Exception.Message)"
}

# 3. Test POST /api/answer with Auth Header and Stream
Write-Host "`n--- Testing POST /api/answer with Auth Header (NDJSON Stream) ---"
$headers = @{
    "X-Flinch-Client-Key" = "flinch-desktop-dev-token"
}
$body = '{"query":"solve 3x + 10 = 25", "stream": true}'
$response = Invoke-WebRequest -Uri "http://127.0.0.1:5055/api/answer" -Method Post -Headers $headers -Body $body -ContentType "application/json"
Write-Host "Response Status: $($response.StatusCode)"
Write-Host "Content-Type: $($response.Headers['Content-Type'])"
Write-Host "`n--- Streamed Chunks ---"
$lines = $response.Content -split "`n"
foreach ($line in $lines) {
    if ($line.Trim()) {
        Write-Host $line
    }
}

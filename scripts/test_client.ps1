$handler = New-Object System.Net.Http.HttpClientHandler
$client = New-Object System.Net.Http.HttpClient($handler)
$client.Timeout = [TimeSpan]::FromSeconds(10)

$msg = New-Object System.Net.Http.HttpRequestMessage([System.Net.Http.HttpMethod]::Post, "http://127.0.0.1:5055/api/answer")
$msg.Headers.Add("X-Flinch-Client-Key", "flinch-desktop-dev-token")
$msg.Content = New-Object System.Net.Http.StringContent('{"query":"solve 3x + 10 = 25", "stream": true}', [System.Text.Encoding]::UTF8, "application/json")

$response = $client.SendAsync($msg, [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
Write-Host "Status: $($response.StatusCode)"
$stream = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
$reader = New-Object System.IO.StreamReader($stream)

Write-Host "--- Stream Output ---"
while (-not $reader.EndOfStream) {
    $line = $reader.ReadLine()
    if ($line) {
        Write-Host $line
    }
}
$reader.Close()
$client.Dispose()

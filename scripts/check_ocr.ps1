$env:DOTNET_ROOT = 'C:\Users\opc\AppData\Local\Microsoft\dotnet'
$env:PATH = "$env:DOTNET_ROOT;C:\Users\opc\AppData\Local\Microsoft\WindowsApps;$env:PATH"

Add-Type -AssemblyName "System.Runtime.WindowsRuntime, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089" -ErrorAction SilentlyContinue

try {
    $langs = [Windows.Media.Ocr.OcrEngine, Windows.Foundation, ContentType=WindowsRuntime]::AvailableRecognizerLanguages
    Write-Host "Available OCR languages: $($langs.Count)"
    foreach ($lang in $langs) {
        Write-Host " - $($lang.LanguageTag) ($($lang.DisplayName))"
    }
} catch {
    Write-Host "Error checking OCR: $_"
}

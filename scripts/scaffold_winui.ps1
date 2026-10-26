$env:PATH = "C:\Users\opc\AppData\Local\Microsoft\dotnet;C:\Users\opc\AppData\Local\Microsoft\WindowsApps;$env:PATH"
$env:DOTNET_ROOT = "C:\Users\opc\AppData\Local\Microsoft\dotnet"

Write-Host "DOTNET ROOT: $env:DOTNET_ROOT"
Write-Host "DOTNET VERSION: $(dotnet --version)"

$winapp = "C:\Users\opc\AppData\Local\Microsoft\WindowsApps\winapp.exe"
& $winapp new --name Flinch.App --template winui --output src\Flinch.App --use-defaults --force

$env:DOTNET_ROOT = 'C:\Users\opc\AppData\Local\Microsoft\dotnet'
$env:PATH = "$env:DOTNET_ROOT;C:\Users\opc\AppData\Local\Microsoft\WindowsApps;$env:PATH"

dotnet run --project src/Flinch.Api/Flinch.Api.csproj --urls "http://127.0.0.1:5055"

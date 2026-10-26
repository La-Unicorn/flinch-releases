$env:DOTNET_ROOT = 'C:\Users\opc\AppData\Local\Microsoft\dotnet'
$env:PATH = "$env:DOTNET_ROOT;C:\Users\opc\AppData\Local\Microsoft\WindowsApps;$env:PATH"

dotnet new web -o src/Flinch.Api

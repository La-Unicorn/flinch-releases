$env:DOTNET_ROOT = 'C:\Users\opc\AppData\Local\Microsoft\dotnet'
$env:PATH = "$env:DOTNET_ROOT;C:\Users\opc\AppData\Local\Microsoft\WindowsApps;$env:PATH"

dotnet test tests\Flinch.Core.Tests\Flinch.Core.Tests.csproj --verbosity normal

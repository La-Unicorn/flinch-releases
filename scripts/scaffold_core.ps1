$env:DOTNET_ROOT = 'C:\Users\opc\AppData\Local\Microsoft\dotnet'
$env:PATH = "$env:DOTNET_ROOT;C:\Users\opc\AppData\Local\Microsoft\WindowsApps;$env:PATH"

dotnet new classlib -o src\Flinch.Core --force
dotnet new mstest -o tests\Flinch.Core.Tests --force
dotnet add tests\Flinch.Core.Tests\Flinch.Core.Tests.csproj reference src\Flinch.Core\Flinch.Core.csproj

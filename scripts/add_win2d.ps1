$env:DOTNET_ROOT = 'C:\Users\opc\AppData\Local\Microsoft\dotnet'
$env:PATH = "$env:DOTNET_ROOT;C:\Users\opc\AppData\Local\Microsoft\WindowsApps;$env:PATH"

dotnet add src\Flinch.App\Flinch.App.csproj package Microsoft.Graphics.Win2D

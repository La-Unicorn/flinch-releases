# Flinch — Packaging and Distribution Strategy

This document outlines the packaging, code signing, update distribution, and enterprise deployment strategies for Flinch on Windows 10/11.

---

## 1. Supported Distribution Models

Flinch supports two primary distribution targets depending on the deployment environment:

| Distribution Target | Tooling | Target Audience | Key Advantage |
| :--- | :--- | :--- | :--- |
| **Packaged MSIX** | Windows App SDK / `winapp` | Consumer / Microsoft Store / Enterprise Intune | Clean uninstall, auto-updates via `.appinstaller`, identity isolation |
| **Unpackaged Self-Contained** | `dotnet publish` | Portable / Developer / Offline Workstations | Zero installation prerequisites, single folder or single executable |

---

## 2. Packaged MSIX Build

The WinUI 3 project in `src/Flinch.App` includes a native `Package.appxmanifest` configured for Windows desktop packaging.

### Build & Package Command
```powershell
# In PowerShell:
dotnet publish src\Flinch.App\Flinch.App.csproj `
    -c Release `
    -r win-x64 `
    -p:GenerateAppxPackageOnBuild=true `
    -p:AppxPackageDir="artifacts\msix" `
    -p:AppxBundle=Never `
    -p:UapAppxPackageBuildMode=SideloadOnly
```

### Required Windows Capabilities
In `Package.appxmanifest`:
- `runFullTrust`: Required for native HWND interop, DWM transparency, and UIA accessibility APIs.
- `graphicsCaptureWithoutBorder`: Allows seamless screen capture without intrusive system yellow borders when supported.
- `microphone`: Required for push-to-talk voice input.

---

## 3. Unpackaged Self-Contained Distribution

For environments where installing MSIX packages is restricted or for simple portable distribution:

### Publish Command
```powershell
# Publish self-contained executable for win-x64:
dotnet publish src\Flinch.App\Flinch.App.csproj `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:WindowsPackageType=None `
    -p:PublishSingleFile=true `
    -o "dist\Flinch-Portable"
```

The resulting `dist\Flinch-Portable` directory contains `Flinch.App.exe` with the .NET 10 runtime embedded, requiring no prior .NET runtime installation on target machines.

---

## 4. Code Signing & Certificates

### Development / Sideloading (Self-Signed Test Certificate)
```powershell
# Create local development certificate
$cert = New-SelfSignedCertificate `
    -Type Custom `
    -Subject "CN=FlinchDevelopment" `
    -KeyUsage DigitalSignature `
    -FriendlyName "Flinch Dev Cert" `
    -CertStoreLocation "Cert:\CurrentUser\My"

# Export public certificate
Export-Certificate -Cert $cert -FilePath "dist\FlinchDev.cer"

# Trust certificate locally (Run as Administrator)
CertUtil -addStore TrustedPeople "dist\FlinchDev.cer"

# Sign MSIX Package
& "signtool.exe" sign /fd SHA256 /a /f "dist\FlinchDev.pfx" "artifacts\msix\Flinch.msix"
```

### Production Authenticode Signing
For official releases, sign with a hardware-backed or Azure Trusted Signing (Code Signing PKI) certificate:
```powershell
& "signtool.exe" sign /v /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 /n "Flinch Publisher Name" "artifacts\msix\Flinch.msix"
```

---

## 5. Auto-Update Mechanisms

### A. Windows App Installer (`.appinstaller`)
For web-based sideloading with automated background updates:
```xml
<?xml version="1.0" encoding="utf-8"?>
<AppInstaller
    xmlns="http://schemas.microsoft.com/appx/appinstaller/2018"
    Version="1.0.0.0"
    Uri="https://releases.flinch.internal/Flinch.appinstaller">
    <MainPackage
        Name="Flinch.App"
        Publisher="CN=FlinchPublisher"
        Version="1.0.0.0"
        ProcessorArchitecture="x64"
        Uri="https://releases.flinch.internal/Flinch-1.0.0.0.msix" />
    <UpdateSettings>
        <OnLaunch HoursBetweenUpdateChecks="12" ShowPrompt="false" UpdateBlocksActivation="false" />
        <AutomaticBackgroundTask />
    </UpdateSettings>
</AppInstaller>
```

### B. Windows Package Manager (`winget`)
Flinch can be published to the community or private winget repository:
```powershell
winget install --id Flinch.Assistant --exact
```

---

## 6. Enterprise Deployment (Intune / Group Policy)

### Silent Installation via PowerShell
```powershell
Add-AppxPackage -Path "\\network-share\Flinch\Flinch.msix" -DeferRegistrationWhenPackagesAreInUse
```

### Silent Uninstallation
```powershell
Get-AppxPackage -Name "Flinch.App" | Remove-AppxPackage
```

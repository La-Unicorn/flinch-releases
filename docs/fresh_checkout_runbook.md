# Flinch — Fresh Checkout Setup & Runbook

This guide provides step-by-step instructions for setting up, building, testing, and running Flinch on a freshly cloned machine with zero secrets and zero external network dependencies.

---

## 1. System Prerequisites

1. **Operating System:** Windows 10 (version 2004+ / build 19041+) or Windows 11 (build 22000+), x64 or ARM64.
2. **Developer Mode:** Must be enabled.
   - Go to **Windows Settings > System > For developers** and toggle **Developer Mode** to **On**.
3. **.NET 10 SDK:** Version `10.0.403` or later.
4. **Windows App Development CLI (`winapp`):** Version `0.7.1` or later.

### Quick Install Command (Admin PowerShell)
```powershell
winget install --id Microsoft.DotNet.SDK.10 --exact --source winget
winget install --id Microsoft.WinAppCli --exact --source winget
```

---

## 2. Clone Repository & First-Time Inspection

```powershell
git clone https://github.com/your-org/flinch.git
cd flinch

# Verify toolchain availability
dotnet --version    # Expected: 10.0.403 or higher
winapp --version    # Expected: 0.7.1 or higher
```

---

## 3. Verify Clean Build & Test (100% Offline with Zero Secrets)

Run the automated verification script:
```powershell
powershell -ExecutionPolicy Bypass -File "scripts\verify_clean_build.ps1"
```

This single command executes:
1. `audit_privacy_and_secrets.ps1` (Ensures zero credentials, zero disk leaks, zero `eval`).
2. `run_core_tests.ps1` (Executes all 36 unit tests with `--no-network` completely offline).
3. `build_api.ps1` (Compiles ASP.NET Core backend).
4. `build_winui.ps1` (Compiles WinUI 3 desktop client).

---

## 4. Running the Application

### Option A: WinUI Desktop Client (Standalone / Offline Demonstration)
```powershell
cd src\Flinch.App
winapp run
```
- In the desktop window:
  - Click **Trigger Stage 5 Native Overlay** to observe synchronized speech synthesis, animated cubic ease-out drawings, and Cambria Math typography cards.
  - Press `Escape` or click **Dismiss Overlay** to immediately cancel audio and clear all visuals.
  - Click **Share Screen / Window** to initiate one-shot graphics capture, inspect bounding boxes and OCR text, or click **Cancel** to immediately purge Direct3D buffers from memory.

### Option B: Running the Backend Streaming Gateway
```powershell
cd src\Flinch.Api
dotnet run
```
- Listens on `http://localhost:5000`.
- Health check: `GET http://localhost:5000/health`.
- Mathematical solving: `POST http://localhost:5000/api/math/solve` with header `X-Flinch-Client-Key: flinch-desktop-dev-token`.
- Streaming answers: `POST http://localhost:5000/api/answer`.

---

## 5. Security & Privacy Guarantees

1. **Ephemeral Memory:** No screen frames, captured windows, or OCR crops are ever written to disk or logged. Memory is zero-filled via `Array.Clear` and Direct3D surfaces are promptly disposed.
2. **Untrusted Data Model:** All screen text and model responses are strictly sanitized and parsed into structured ASTs or schema-validated shapes. No dynamic code (`eval`, `exec`, arbitrary XAML) is ever executed.
3. **Provider Secrets:** Provider master keys live exclusively server-side in backend environment variables (`OPENAI_API_KEY`). The desktop client binary contains zero cloud credentials.

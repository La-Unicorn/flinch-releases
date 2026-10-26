# Flinch — Privacy-First Windows Screen-Aware Voice Companion

Flinch is an independent, privacy-first desktop voice companion built for Windows using .NET 10, WinUI 3, and Windows App SDK. It enables users to ask spoken or typed questions, deliberately share an active window or display, receive verified explanations, and see temporary animated visual overlays synchronized with spoken steps.

---

## Prerequisites & Toolchain

- **Windows 10 / 11** (x64 or ARM64)
- **Developer Mode:** Enabled in **Windows Settings > System > For developers**
- **.NET SDK 10+:** Version `10.0.403` or later
- **Windows App Development CLI (`winapp`):** Version `0.7.1` or later

### Toolchain Setup
```powershell
# In PowerShell:
winget install --id Microsoft.DotNet.SDK.10 --exact --source winget
winget install --id Microsoft.WinAppCli --exact --source winget
```

---

## Repository Structure

```
src/
  Flinch.App/          # WinUI 3 Native Desktop Shell, CaptureService, ContextService, OverlayHost
  Flinch.Core/         # Key contracts (ScreenContext, ExplanationPlan, OverlayScene, MathResult, ToolRequest)
  Flinch.Api/          # (Stage 4) ASP.NET Core provider, policy allowlist, streaming endpoints
  Flinch.MathWorker/   # (Stage 7) Restricted SymPy math solver worker
tests/
  Flinch.Core.Tests/   # Offline unit tests (runnable with 0 credentials/network)
scripts/
  build_winui.ps1      # Compiles Flinch.App WinUI 3 shell
  run_core_tests.ps1   # Runs offline unit test suite
```

---

## Build & Test Instructions

### 1. Full Offline Clean Checkout Verification (Audit, Tests & Builds)
```powershell
powershell -ExecutionPolicy Bypass -File "scripts\verify_clean_build.ps1"
```

### 2. Run Privacy & Secret-Leak Audit
```powershell
powershell -ExecutionPolicy Bypass -File "scripts\audit_privacy_and_secrets.ps1"
```

### 3. Run Offline Unit Tests
```powershell
powershell -ExecutionPolicy Bypass -File "scripts\run_core_tests.ps1"
```

### 4. Build WinUI 3 Desktop App
```powershell
powershell -ExecutionPolicy Bypass -File "scripts\build_winui.ps1"
```

### 5. Launch App with WinApp CLI
```powershell
cd src\Flinch.App
winapp run
```

---

## Security & Privacy Invariants

1. **Zero Secret Retention in Desktop Binaries:** Master AI API keys live strictly on the backend gateway.
2. **Untrusted Screen & Model Data:** Screen text, OCR, and AI outputs are treated as hostile, untrusted data. No `eval`, arbitrary XAML execution, or model-generated scripts.
3. **Explicit One-Shot Screen Capture:** Windows.Graphics.Capture picker opens only upon deliberate user request; frames are ephemeral and discarded on dismiss.
4. **Offline First:** Core contracts and test suites run completely offline without requiring API keys or network access.

---

## Stage Progress & Roadmap

- [x] **Stage 1 (Complete):** Toolchain inspection, .NET 10 SDK & WinApp CLI installation, core contracts (`ScreenContext`, `ExplanationPlan`, `OverlayScene`, `MathResult`, `ToolRequest`), offline unit tests, WinUI 3 shell with transcript panel, capture button, user-visible status, and push-to-talk placeholder.
- [x] **Stage 2 (Complete):** Explicit One-Shot Screen Capture via `Windows.Graphics.Capture.GraphicsCapturePicker` initialized with owner HWND, ephemeral preview card, prompt disposal, cancel discard, and zero disk retention.
- [x] **Stage 3 (Complete):** Bounded UI Automation (UIA) and OCR coordinate normalization. Extracts up to 40 accessible controls and 60 native OCR text blocks, calculates DPI/monitor metadata, normalizes coordinates to `[0.0, 1.0]`, sanitizes adversarial text, and displays an explicit context preview inspector in `MainPage.xaml`.
- [x] **Stage 4 (Complete):** ASP.NET Core streaming backend (`src/Flinch.Api`) with `/api/answer` NDJSON streaming, client authentication, rate limiting, dual providers (`FakeAiProvider` for offline testing and `OpenAiResponsesProvider` for cloud vision), strict schema validation/clamping, and desktop streaming integration.
- [x] **Stage 5 (Complete):** DirectComposition / native transparent click-through overlay host (`OverlayWindow`, `OverlayCanvas`, `OverlayHost`). Implements DWM per-pixel transparency, non-activating focus (`WS_EX_NOACTIVATE`), Alt-Tab suppression (`WS_EX_TOOLWINDOW`), dynamic click-through mode (`WS_EX_TRANSPARENT`), approved shape rendering (Outline, Highlight, Arrow, Callout, Equation Card), and keyboard dismissal (Escape / clear).
- [x] **Stage 6 (Complete):** Synchronized speech and animated drawing pipeline. Implemented `TimelineClock` (10,000 ticks = 1 ms invariant), native `AudioService` using Windows Speech Synthesis and MediaPlayer position tracking (with mute toggle), `CueScheduler` orchestrating visual cues against the playback clock, entrance fade/slide animations aware of Windows reduced-motion preferences, and cancellation on Escape/Dismiss.
- [x] **Stage 7 (Complete):** Verified Math Engine. Bounded AST contract (`MathAstNode`, `MathAstParser`), isolated Python SymPy worker (`src/Flinch.MathWorker/solver.py`) with zero-eval policy and 5s process timeout, high-precision C# algebraic fallback engine, domain constraint checking, exact residual calculation ($|f(x^*)| \le 10^{-6}$), safe LaTeX formatting, and `/api/math/solve` endpoint with authentication.
- [x] **Stage 8 (Complete):** Hardening, accessibility, packaging, and distribution. Implemented screen reader accessibility with `AutomationProperties` on all controls, high-contrast support, secret-leak & privacy audit script (`scripts/audit_privacy_and_secrets.ps1`), clean checkout verification script (`scripts/verify_clean_build.ps1`), packaging and update strategy (`docs/packaging_and_distribution.md`), and fresh-checkout runbook (`docs/fresh_checkout_runbook.md`). All 36 tests pass completely offline.

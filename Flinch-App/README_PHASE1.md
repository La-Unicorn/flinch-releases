# Flinch — Phase 1: Screen Awareness & Coordinate System Architecture

## 1. Overview
Phase 1 establishes the foundational **privacy-first, explicit-consent screen context pipeline** for Flinch.

Unlike background screen recorders or spyware models that silently stream user desktops, Flinch operates on a strict **zero-silent-capture policy**:
1. **Explicit Trigger Only:** Capture occurs only when the user clicks *"📸 Look at Current Window..."* in the tray/notch or asks out loud *"Look at this"*.
2. **Local Preview & User Approval:** A confirmation window (`ScreenPreviewWindow`) presents the exact captured window/bounds, target resolution, and thumbnail preview.
3. **Ephemeral In-Memory Lifecycle:** Captured bitmaps are held strictly in memory. If discarded, they are immediately dereferenced and garbage collected. When sent, they are consumed once and erased from memory with zero disk persistence.
4. **Normalized Coordinate Engine (`ScreenCoordinateMapper`):** Clamps and translates physical display coordinates into normalized $[0.0, 1.0]$ space, enabling cross-DPI and multi-monitor alignment for upcoming Phase 2 overlays.

---

## 2. Architecture & Service Boundaries

```
[ Dynamic Island & Tray UI ]
       │
       ▼
[ IScreenCaptureService ] ◄──── (WindowsScreenCaptureService)
       │
       ├─► EnumerateAvailableTargets(ignoreHwnd) -> List<WindowTarget>
       ├─► GetActiveForegroundTarget(ignoreHwnd)  -> WindowTarget
       └─► CaptureTarget(WindowTarget)           -> CapturedFrame
               │
               ▼
       [ ScreenPreviewWindow ] ◄── (Local Preview & Consent Modal)
               │
         ┌─────┴───────────────────────┐
         ▼                             ▼
   [✓ Confirm]                    [✕ Discard]
         │                             │
         ▼                             ▼
   GeminiClient.SetStagedScreenFrame   frame.Dispose() -> Zero Leak
   (Consumes once on voice turn)
```

### Components:
* **`IScreenCaptureService` / `WindowsScreenCaptureService`:** Uses Win32 `DwmGetWindowAttribute` with `DWMWA_EXTENDED_FRAME_BOUNDS` to capture the true visible client bounds of the foreground window (excluding window drop shadows and taskbar clutter).
* **`ScreenCoordinateMapper`:** Maps physical screen pixels $(x, y)$ to normalized $[0.0, 1.0]$ coordinates and back to screen rectangles, guarding against division-by-zero on minimized windows.
* **`CapturedFrame`:** Encapsulates the compressed JPEG stream, source bounds, timestamp, and raw preview bitmap with an explicit `IDisposable` implementation.
* **`ScreenPreviewWindow`:** Modern dark-themed WPF dialog with live image preview, target process pill, privacy reassurance banner, and keyboard shortcuts (`Enter` to confirm, `Escape` to discard).

---

## 3. Build & Test Instructions

### Requirements
* Windows 10 or 11 (64-bit)
* Built-in .NET Framework 4.8 `csc.exe` (included with Windows; no extra SDK download required)

### How to Compile
From PowerShell in the project directory:
```powershell
powershell -ExecutionPolicy Bypass -File .\Flinch-App\build.ps1
```
This builds both:
* Standalone executable: `Flinch-App\dist\Flinch.exe` (~315 KB)
* Self-contained installer: `Flinch-App\dist\FlinchSetup.exe` (~545 KB)

### How to Run Automated Unit Tests
A dedicated headless test suite verifies coordinate math, edge cases, and disposal semantics:
```powershell
powershell -ExecutionPolicy Bypass -File .\Flinch-App\tests\run_tests.ps1
```
All 8 automated tests run and verify:
* Center & corner coordinate normalization
* Clamping of out-of-bounds negative and oversized coordinates
* Denormalization back to physical monitor pixels
* Zero-dimension protection against `NaN` / division by zero
* Ephemeral memory wipe on `Dispose()`
* Zero-leak single-use consumption in `GeminiClient`

---

## 4. Next 3 Milestones

1. **Milestone 2: UI Automation (UIA) & Accessibility Context**
   - Query foreground UIA element tree for active button labels, focused input names, and text patterns to supplement visual understanding.
2. **Milestone 3: Multimodal Vision & Backend Integration**
   - Pass user-approved screenshot crops to Gemini Flash vision edge function; parse model's structured scene description.
3. **Milestone 4: Validated Transparent Screen Overlay**
   - Borderless transparent topmost canvas rendering model-proposed JSON scene objects (curved arrows, bounding box highlights, step badges, and math equations) directly over the targeted app.

# Flinch Practical Build Guide: Screen Awareness & Dynamic Overlays

## Summary of Implementation

This release fully implements the **Flinch Practical Build Guide** across all 4 production milestones:

---

### Milestone 1: Privacy-First Ephemeral Screen Capture
- **Native DWM Capture**: Uses `DwmGetWindowAttribute(DWMWA_EXTENDED_FRAME_BOUNDS)` to capture only the user's active window (or display) without window shadows or background leakage.
- **Coordinate Normalization**: Normalizes pixel coordinates $[0.0, 1.0]$ via `ScreenCoordinateMapper`. Clamps out-of-bounds coordinates to prevent edge overflow.
- **Ephemeral Lifecycle**: In-memory JPEG encoding with `IDisposable` lifecycle. Zero bytes written to disk; frames are explicitly consumed and wiped after sending.
- **Preview & Consent Modal**: Dark WPF modal (`#1C1F26`) presenting window title, process name, image preview, and explicit **[Attach to Flinch]** or **[Discard]** buttons.

---

### Milestone 2: UI Automation (UIA) Context Extraction
- **Windows UI Automation**: Deep traversal of foreground window controls via `System.Windows.Automation.AutomationElement`.
- **Element Extraction**: Automatically captures interactive elements (`Button`, `Edit`, `Hyperlink`, `Document`, `Text`), their accessible labels, and normalized bounding boxes.
- **Zero Overhead**: Depth-limited BFS queue (max 40 controls, <30ms execution) ensuring responsiveness.
- **Transport**: Embedded as `uia` multipart string alongside image bytes for contextual vision processing.

---

### Milestone 3: Vision Edge Function & Structured Scene Schema
- **Supabase Edge Function (`flinch-voice`)**:
  - Ingests `file` (voice audio), `screen` (JPEG buffer), `uia` (UI automation element tree), and `clipboard` (copied text).
  - Prompts Gemini Vision with prompt engineering requiring:
    1. Spoken answer in 1-2 natural sentences without markdown or filler.
    2. Structured JSON `scene` with normalized $[0.0, 1.0]$ coordinates for visual cues.
  - **`sanitizeScene`**:
    - Validates allowlisted visual types: `arrow`, `outline`, `callout`, `equation`, `spotlight`.
    - Sanitizes text to prevent XSS/injection.
    - Clamps coordinates $x, y, w, h \in [0.0, 1.0]$.
    - Enforces duration limits (2,000ms – 30,000ms, default 8,000ms).
  - Deployed to Supabase: `supabase functions deploy flinch-voice --no-verify-jwt`.

---

### Milestone 4: Transparent Screen Overlay Window (`OverlayWindow`)
- **Borderless Topmost Canvas**: Positioned 1:1 over the target window bounds via `DwmGetWindowAttribute`.
- **Vector & UI Rendering**:
  - **`arrow`**: Neon cyan (`#00E5FF`) line with arrowhead tip, drop-shadow glow, and optional label pill.
  - **`outline`**: Sky blue (`#38BDF8`) rounded rectangle border with glowing drop shadow and top tag pill.
  - **`callout`**: Glassmorphic slate card (`#1E2330` with `#6366F1` border) for rich explanatory notes.
  - **`equation`**: Math card with `📐 FORMULA` badge and formatted formula rendering.
  - **`spotlight`**: Soft highlight focus over screen region.
- **Dismissal & Barge-In**:
  - Top-right floating `[✕ Dismiss Overlay (Esc)]` button.
  - Pressing `Escape` or clicking canvas closes immediately with a smooth fade-out animation.
  - Auto-dismiss timer based on `scene.durationMs`.
  - Automatically cleared on voice activation (`Alt+V`) or new user interaction.

---

### Verification & Test Suite
- Automated test runner: `Flinch-App/tests/run_tests.ps1`
- 12 comprehensive unit tests:
  1. Center coordinate normalization
  2. Corner coordinate normalization
  3. Out-of-bounds coordinate clamping
  4. Screen coordinate denormalization
  5. Normalized rect to screen rect conversion
  6. Zero-dimension protection against division by zero
  7. `CapturedFrame` ephemeral lifecycle & disposal
  8. Staged screen context zero-retention consumer
  9. `NormalizeRect` mapping and precision
  10. UI Automation & extended staged payload consumption
  11. Structured scene object models & property preservation
  12. Scene coordinate clamping & boundary safety
- **Result**: `ALL 12 TESTS PASSED SUCCESSFULLY! (0 failures)`
- Zero AI keys or secrets compiled into client binaries.

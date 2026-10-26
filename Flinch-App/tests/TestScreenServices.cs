using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using FlinchApp;

namespace FlinchApp.Tests
{
    public static class TestScreenServices
    {
        public static int Main()
        {
            int failed = 0;
            Console.WriteLine("====================================================");
            Console.WriteLine("FLINCH PHASE 1: SCREEN SERVICE & COORDINATE TESTS");
            Console.WriteLine("====================================================");

            // Test 1: Coordinate Normalization (Center)
            try
            {
                var mapper = new ScreenCoordinateMapper(100, 200, 1000, 500);
                var norm = mapper.ToNormalized(600, 450);
                Assert(Math.Abs(norm.X - 0.5) < 0.001 && Math.Abs(norm.Y - 0.5) < 0.001,
                    "ToNormalized(600, 450) should be (0.5, 0.5)");
                Console.WriteLine("[PASS] Test 1: Center coordinate normalization");
            }
            catch (Exception ex) { Console.WriteLine("[FAIL] Test 1: " + ex.Message); failed++; }

            // Test 2: Coordinate Normalization (Corners)
            try
            {
                var mapper = new ScreenCoordinateMapper(100, 200, 1000, 500);
                var normTopLeft = mapper.ToNormalized(100, 200);
                var normBottomRight = mapper.ToNormalized(1100, 700);
                Assert(Math.Abs(normTopLeft.X) < 0.001 && Math.Abs(normTopLeft.Y) < 0.001,
                    "Top-left should normalize to (0.0, 0.0)");
                Assert(Math.Abs(normBottomRight.X - 1.0) < 0.001 && Math.Abs(normBottomRight.Y - 1.0) < 0.001,
                    "Bottom-right should normalize to (1.0, 1.0)");
                Console.WriteLine("[PASS] Test 2: Corner coordinate normalization");
            }
            catch (Exception ex) { Console.WriteLine("[FAIL] Test 2: " + ex.Message); failed++; }

            // Test 3: Clamping Out-of-Bounds Coordinates
            try
            {
                var mapper = new ScreenCoordinateMapper(100, 200, 1000, 500);
                var normNegative = mapper.ToNormalized(-500, -200);
                var normOver = mapper.ToNormalized(5000, 5000);
                Assert(normNegative.X == 0.0 && normNegative.Y == 0.0,
                    "Negative coords should clamp to 0.0");
                Assert(normOver.X == 1.0 && normOver.Y == 1.0,
                    "Oversized coords should clamp to 1.0");
                Console.WriteLine("[PASS] Test 3: Out-of-bounds coordinate clamping");
            }
            catch (Exception ex) { Console.WriteLine("[FAIL] Test 3: " + ex.Message); failed++; }

            // Test 4: Denormalization Back to Physical Screen Pixels
            try
            {
                var mapper = new ScreenCoordinateMapper(100, 200, 1000, 500);
                var screenPoint = mapper.ToScreen(0.25, 0.75);
                Assert(Math.Abs(screenPoint.X - 350.0) < 0.001, "X should be 100 + 0.25*1000 = 350");
                Assert(Math.Abs(screenPoint.Y - 575.0) < 0.001, "Y should be 200 + 0.75*500 = 575");
                Console.WriteLine("[PASS] Test 4: Screen coordinate denormalization");
            }
            catch (Exception ex) { Console.WriteLine("[FAIL] Test 4: " + ex.Message); failed++; }

            // Test 5: Rect Denormalization
            try
            {
                var mapper = new ScreenCoordinateMapper(100, 200, 1000, 500);
                var rect = mapper.ToScreenRect(0.1, 0.2, 0.3, 0.4);
                Assert(Math.Abs(rect.X - 200.0) < 0.001, "Rect X should be 100 + 0.1*1000 = 200");
                Assert(Math.Abs(rect.Y - 300.0) < 0.001, "Rect Y should be 200 + 0.2*500 = 300");
                Assert(Math.Abs(rect.Width - 300.0) < 0.001, "Rect Width should be 0.3*1000 = 300");
                Assert(Math.Abs(rect.Height - 200.0) < 0.001, "Rect Height should be 0.4*500 = 200");
                Console.WriteLine("[PASS] Test 5: Normalized rect to screen rect conversion");
            }
            catch (Exception ex) { Console.WriteLine("[FAIL] Test 5: " + ex.Message); failed++; }

            // Test 6: Zero-Dimension / Division-by-Zero Protection
            try
            {
                var mapperZero = new ScreenCoordinateMapper(0, 0, 0, 0);
                var norm = mapperZero.ToNormalized(10, 10);
                Assert(!double.IsNaN(norm.X) && !double.IsInfinity(norm.X), "Zero width must not produce NaN/Infinity");
                Assert(!double.IsNaN(norm.Y) && !double.IsInfinity(norm.Y), "Zero height must not produce NaN/Infinity");
                Console.WriteLine("[PASS] Test 6: Zero-dimension protection against division by zero");
            }
            catch (Exception ex) { Console.WriteLine("[FAIL] Test 6: " + ex.Message); failed++; }

            // Test 7: Ephemeral Disposal of CapturedFrame
            try
            {
                using (var testBmp = new Bitmap(400, 300))
                {
                    using (var g = Graphics.FromImage(testBmp))
                    {
                        g.Clear(Color.CornflowerBlue);
                    }

                    var bounds = new NativeMethods.RECT { Left = 50, Top = 50, Right = 450, Bottom = 350 };
                    var frame = new CapturedFrame(testBmp, bounds, "UnitTestWindow");

                    Assert(frame.JpegBytes != null && frame.JpegBytes.Length > 0, "JpegBytes must be populated");
                    Assert(frame.RawBitmap != null, "RawBitmap must be populated");
                    Assert(frame.SourceWidth == 400 && frame.SourceHeight == 300, "Dimensions must match source");

                    // Explicit disposal
                    frame.Dispose();

                    Assert(frame.RawBitmap == null, "RawBitmap must be null after Dispose");
                    Assert(frame.JpegBytes == null, "JpegBytes must be null after Dispose");
                    Console.WriteLine("[PASS] Test 7: CapturedFrame ephemeral lifecycle and disposal");
                }
            }
            catch (Exception ex) { Console.WriteLine("[FAIL] Test 7: " + ex.Message); failed++; }

            // Test 8: Staged Screen Context Zero-Leak Consumer
            try
            {
                using (var testBmp = new Bitmap(200, 200))
                {
                    var bounds = new NativeMethods.RECT { Left = 0, Top = 0, Right = 200, Bottom = 200 };
                    var frame = new CapturedFrame(testBmp, bounds, "StagedWindow");

                    GeminiClient.SetStagedScreenFrame(frame);
                    Assert(GeminiClient.HasStagedScreenFrame, "GeminiClient must have staged frame");

                    byte[] consumed = GeminiClient.GetAndConsumeStagedScreenBytes();
                    Assert(consumed != null && consumed.Length > 0, "Consumed bytes must be valid");
                    Assert(!GeminiClient.HasStagedScreenFrame, "Staged frame must be cleared after consumption");

                    // Second consumption must be null (no double-send or retention)
                    byte[] second = GeminiClient.GetAndConsumeStagedScreenBytes();
                    Assert(second == null, "Subsequent consumption must be null");
                    Console.WriteLine("[PASS] Test 8: Staged screen context zero-retention consumer");
                }
            }
            catch (Exception ex) { Console.WriteLine("[FAIL] Test 8: " + ex.Message); failed++; }

            // Test 9: NormalizeRect Accuracy and Clamping
            try
            {
                var mapper = new ScreenCoordinateMapper(100, 200, 1000, 500);
                var normRect = mapper.NormalizeRect(350, 325, 500, 250);
                Assert(Math.Abs(normRect.X - 0.25) < 0.001, "NormalizeRect X should be 0.25");
                Assert(Math.Abs(normRect.Y - 0.25) < 0.001, "NormalizeRect Y should be 0.25");
                Assert(Math.Abs(normRect.Width - 0.5) < 0.001, "NormalizeRect Width should be 0.5");
                Assert(Math.Abs(normRect.Height - 0.5) < 0.001, "NormalizeRect Height should be 0.5");
                Console.WriteLine("[PASS] Test 9: NormalizeRect mapping and precision");
            }
            catch (Exception ex) { Console.WriteLine("[FAIL] Test 9: " + ex.Message); failed++; }

            // Test 10: UI Automation & Extended Staged Payload Consumption
            try
            {
                using (var testBmp = new Bitmap(300, 300))
                {
                    var bounds = new NativeMethods.RECT { Left = 50, Top = 50, Right = 350, Bottom = 350 };
                    var frame = new CapturedFrame(testBmp, bounds, "UiaTestWindow");
                    frame.UiaJson = "[{\"type\":\"button\",\"name\":\"Submit\",\"x\":0.5,\"y\":0.8,\"w\":0.2,\"h\":0.05}]";

                    GeminiClient.SetStagedScreenFrame(frame);
                    Assert(GeminiClient.HasStagedScreenFrame, "Should report staged frame present");

                    byte[] outBytes;
                    string outUia;
                    NativeMethods.RECT outBounds;
                    bool ok = GeminiClient.GetAndConsumeStagedScreenPayload(out outBytes, out outUia, out outBounds);

                    Assert(ok, "Payload consumption should succeed");
                    Assert(outBytes != null && outBytes.Length > 0, "Consumed JPEG bytes must be valid");
                    Assert(outUia != null && outUia.Contains("Submit"), "Consumed UiaJson must contain 'Submit'");
                    Assert(outBounds.Left == 50 && outBounds.Width == 300, "Consumed bounds must match source");
                    Assert(!GeminiClient.HasStagedScreenFrame, "Staged frame must be nullified after payload consumption");
                    Console.WriteLine("[PASS] Test 10: UI Automation & extended staged payload consumption");
                }
            }
            catch (Exception ex) { Console.WriteLine("[FAIL] Test 10: " + ex.Message); failed++; }

            // Test 11: Structured Scene Object Models & Deserialization
            try
            {
                var scene = new ScenePayload { durationMs = 6000 };
                scene.objects.Add(new SceneObjectPayload
                {
                    type = "arrow",
                    from = new ScenePoint { x = 0.1, y = 0.2 },
                    to = new ScenePoint { x = 0.4, y = 0.5 },
                    label = "Click Here"
                });
                scene.objects.Add(new SceneObjectPayload
                {
                    type = "outline",
                    rect = new SceneRect { x = 0.38, y = 0.48, w = 0.15, h = 0.08 },
                    label = "Target Button"
                });
                scene.objects.Add(new SceneObjectPayload
                {
                    type = "callout",
                    rect = new SceneRect { x = 0.55, y = 0.48, w = 0.3, h = 0.1 },
                    text = "This explains the setting"
                });
                scene.objects.Add(new SceneObjectPayload
                {
                    type = "equation",
                    rect = new SceneRect { x = 0.2, y = 0.1, w = 0.4, h = 0.1 },
                    latex = "E = mc^2"
                });

                Assert(scene.objects.Count == 4, "Must contain 4 scene objects");
                Assert(scene.durationMs == 6000, "durationMs must match");
                Assert(scene.objects[0].type == "arrow", "First object must be arrow");
                Assert(scene.objects[1].type == "outline", "Second object must be outline");
                Assert(scene.objects[2].type == "callout", "Third object must be callout");
                Assert(scene.objects[3].type == "equation", "Fourth object must be equation");
                Console.WriteLine("[PASS] Test 11: Structured scene object models & property preservation");
            }
            catch (Exception ex) { Console.WriteLine("[FAIL] Test 11: " + ex.Message); failed++; }

            // Test 12: Scene Coordinate Boundaries Clamping
            try
            {
                var mapper = new ScreenCoordinateMapper(0, 0, 1920, 1080);
                var arrowFrom = mapper.ToScreen(-0.5, 1.5);
                Assert(arrowFrom.X == 0.0, "Clamped norm X should map to screen 0");
                Assert(arrowFrom.Y == 1080.0, "Clamped norm Y should map to screen 1080");

                var arrowTo = mapper.ToScreen(0.5, 0.5);
                Assert(arrowTo.X == 960.0 && arrowTo.Y == 540.0, "Center should map to (960, 540)");
                Console.WriteLine("[PASS] Test 12: Scene coordinate clamping & boundary safety");
            }
            catch (Exception ex) { Console.WriteLine("[FAIL] Test 12: " + ex.Message); failed++; }

            // Test 13: Fail-safe screen capture service produces valid JPEG bytes
            try
            {
                var cap = new WindowsScreenCaptureService();
                var s = System.Windows.Forms.Screen.PrimaryScreen;
                var rect = new NativeMethods.RECT { Left = s.Bounds.Left, Top = s.Bounds.Top, Right = s.Bounds.Right, Bottom = s.Bounds.Bottom };
                var frame = cap.CaptureBounds(rect, "TestPrimary");
                Assert(frame != null, "Captured frame must not be null");
                Assert(frame.JpegBytes != null && frame.JpegBytes.Length > 500, "Frame must contain valid non-empty JPEG bytes (>500 bytes)");
                Assert(frame.SourceBounds.Width > 50 && frame.SourceBounds.Height > 50, "Frame bounds must be non-zero");
                frame.Dispose();
                Console.WriteLine("[PASS] Test 13: Live screen capture produces valid JPEG payload");
            }
            catch (Exception ex) { Console.WriteLine("[FAIL] Test 13: " + ex.Message); failed++; }

            // Test 14: GitHub Releases update checker queries & parses asset metadata
            try
            {
                var updateInfo = UpdateChecker.CheckForUpdate();
                Assert(updateInfo != null, "UpdateInfo must not be null");
                if (!string.IsNullOrEmpty(updateInfo.AssetName))
                {
                    Assert(updateInfo.AssetName == "FlinchSetup.exe", "Asset name must be FlinchSetup.exe");
                    Assert(!string.IsNullOrEmpty(updateInfo.AssetDownloadUrl), "AssetDownloadUrl must not be empty");
                }
                Console.WriteLine("[PASS] Test 14: GitHub Releases update checker queries & parses asset metadata");
            }
            catch (Exception ex) { Console.WriteLine("[FAIL] Test 14: " + ex.Message); failed++; }

            Console.WriteLine("====================================================");
            if (failed == 0)
            {
                Console.WriteLine("ALL 14 TESTS PASSED SUCCESSFULLY! (0 failures)");
                return 0;
            }
            else
            {
                Console.WriteLine("TEST SUITE FAILED with " + failed + " errors.");
                return 1;
            }
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Assertion failed: " + message);
        }
    }
}

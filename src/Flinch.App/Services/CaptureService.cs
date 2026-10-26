using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.Capture;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml.Media.Imaging;
using WinRT.Interop;
using Flinch.Core.Contracts;

namespace Flinch_App.Services;

public sealed class CapturedFrame : IDisposable
{
    public string TargetId { get; set; } = Guid.NewGuid().ToString("N");
    public string TargetTitle { get; set; } = "Selected Target";
    public int Width { get; set; }
    public int Height { get; set; }
    public byte[]? JpegBytes { get; private set; }
    public SoftwareBitmapSource? ImageSource { get; private set; }
    public SoftwareBitmap? RawSoftwareBitmap { get; private set; }
    public bool IsDisposed { get; private set; }

    public CapturedFrame(string title, int width, int height, byte[] jpegBytes, SoftwareBitmap softwareBitmap, SoftwareBitmapSource imageSource)
    {
        TargetTitle = title;
        Width = width;
        Height = height;
        JpegBytes = jpegBytes;
        RawSoftwareBitmap = softwareBitmap;
        ImageSource = imageSource;
    }

    public void Dispose()
    {
        if (IsDisposed) return;
        IsDisposed = true;

        JpegBytes = null;

        try
        {
            ImageSource?.Dispose();
        }
        catch { }
        ImageSource = null;

        try
        {
            RawSoftwareBitmap?.Dispose();
        }
        catch { }
        RawSoftwareBitmap = null;
    }
}

public sealed class CaptureService
{
    private static readonly CanvasDevice SharedDevice = CanvasDevice.GetSharedDevice();

    public static bool IsSupported()
    {
        return GraphicsCaptureSession.IsSupported();
    }

    public async Task<CapturedFrame?> PickAndCaptureAsync(IntPtr ownerHwnd)
    {
        if (!IsSupported())
        {
            return null;
        }

        var picker = new GraphicsCapturePicker();
        InitializeWithWindow.Initialize(picker, ownerHwnd);

        // System picker UI: User explicitly chooses a window or display
        GraphicsCaptureItem? item = await picker.PickSingleItemAsync();
        if (item == null)
        {
            // Explicit Cancel: no capture, zero storage
            return null;
        }

        // Process frame acquisition off the UI thread
        return await Task.Run(async () =>
        {
            return await CaptureSingleItemFrameAsync(item);
        });
    }

    private static async Task<CapturedFrame?> CaptureSingleItemFrameAsync(GraphicsCaptureItem item)
    {
        int width = item.Size.Width;
        int height = item.Size.Height;
        if (width <= 0 || height <= 0) return null;

        var tcs = new TaskCompletionSource<CapturedFrame?>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        cts.Token.Register(() => tcs.TrySetResult(null));

        Direct3D11CaptureFramePool? framePool = null;
        GraphicsCaptureSession? session = null;

        try
        {
            framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                SharedDevice,
                Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized,
                1,
                item.Size);

            session = framePool.CreateCaptureSession(item);

            framePool.FrameArrived += async (pool, args) =>
            {
                try
                {
                    using Direct3D11CaptureFrame? frame = pool.TryGetNextFrame();
                    if (frame == null) return;

                    // Direct hardware surface copy to SoftwareBitmap
                    SoftwareBitmap softwareBitmap = await SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface);

                    // Ephemeral In-Memory JPEG encoder (never written to disk)
                    byte[] jpegBytes;
                    using (var ms = new MemoryStream())
                    {
                        var randomAccess = ms.AsRandomAccessStream();
                        BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, randomAccess);
                        encoder.SetSoftwareBitmap(softwareBitmap);
                        encoder.IsThumbnailGenerated = false;
                        await encoder.FlushAsync();
                        jpegBytes = ms.ToArray();
                    }

                    // Create SoftwareBitmapSource for WinUI 3 preview on UI thread
                    var source = new SoftwareBitmapSource();
                    await source.SetBitmapAsync(softwareBitmap);

                    var captured = new CapturedFrame(
                        item.DisplayName ?? "Selected Window",
                        softwareBitmap.PixelWidth,
                        softwareBitmap.PixelHeight,
                        jpegBytes,
                        softwareBitmap,
                        source);

                    tcs.TrySetResult(captured);
                }
                catch
                {
                    tcs.TrySetResult(null);
                }
                finally
                {
                    // Prompt cleanup of session and framePool
                    try { session?.Dispose(); } catch { }
                    try { pool?.Dispose(); } catch { }
                }
            };

            session.StartCapture();
            return await tcs.Task;
        }
        catch
        {
            try { session?.Dispose(); } catch { }
            try { framePool?.Dispose(); } catch { }
            return null;
        }
    }
}

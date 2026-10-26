using System;
using Windows.Graphics;
using Flinch.Core.Contracts;
using Flinch_App.Overlay;

namespace Flinch_App.Services
{
    /// <summary>
    /// Service managing the lifecycle, positioning, and display of the native OverlayWindow.
    /// Invariant: Non-activating, top-most, transparent composition, auto-dismissible.
    /// </summary>
    public sealed class OverlayHost
    {
        private OverlayWindow? _activeOverlay;

        public bool IsOverlayActive => _activeOverlay != null;

        public void ShowOverlay(OverlayScene scene, RectInt32? targetBounds = null)
        {
            Dismiss();

            _activeOverlay = new OverlayWindow();
            _activeOverlay.Closed += (_, _) =>
            {
                _activeOverlay = null;
            };

            _activeOverlay.ShowScene(scene, targetBounds);
        }

        public void OpenSynchronizedSession(RectInt32? targetBounds = null)
        {
            Dismiss();

            _activeOverlay = new OverlayWindow();
            _activeOverlay.Closed += (_, _) =>
            {
                _activeOverlay = null;
            };

            _activeOverlay.OpenEmptySession(targetBounds);
        }

        public void TriggerCue(OverlayShape shape)
        {
            if (_activeOverlay == null)
            {
                OpenSynchronizedSession();
            }

            _activeOverlay?.AddShape(shape);
        }

        public void ClearShapes()
        {
            _activeOverlay?.ClearShapes();
        }

        public void Dismiss()
        {
            try
            {
                _activeOverlay?.Close();
            }
            catch { }
            finally
            {
                _activeOverlay = null;
            }
        }
    }
}

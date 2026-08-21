using System;
using global::System.IO;
using NuvioTV.Core.UI;
using QrCode = Net.Codecrete.QrCodeGenerator;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;

namespace NuvioTV.Tizen.Widgets
{
    /// <summary>
    /// QR renderer (Task 11.1): Net.Codecrete.QrCodeGenerator produces the
    /// module matrix; the matrix is encoded to PNG (Core PngWriter) and shown
    /// via ImageView ResourceUrl from a temp file. This path avoids the
    /// unverified PixelBuffer API on API7 and is fully host-testable.
    /// </summary>
    public sealed class QrView : View
    {
        private readonly ImageView _image = new ImageView();
        private string _lastPayload;
        private string _lastFile;

        public QrView()
        {
            Size = new Size(420, 420);
            BackgroundColor = Color.White;
            CornerRadius = 12f;

            _image.WidthResizePolicy = ResizePolicyType.FillToParent;
            _image.HeightResizePolicy = ResizePolicyType.FillToParent;
            Add(_image);
        }

        /// <summary>Renders the payload as a QR image; returns false on failure.</summary>
        public bool Render(string payload, int sizePx = 420)
        {
            if (string.IsNullOrEmpty(payload))
            {
                return false;
            }
            if (payload == _lastPayload && _image.ResourceUrl != null && File.Exists(_lastFile))
            {
                return true; // already rendered
            }

            try
            {
                var qr = Net.Codecrete.QrCodeGenerator.QrCode.EncodeText(payload,
                    Net.Codecrete.QrCodeGenerator.QrCode.Ecc.Medium);
                var matrix = new bool[qr.Size, qr.Size];
                for (var y = 0; y < qr.Size; y++)
                {
                    for (var x = 0; x < qr.Size; x++)
                    {
                        matrix[y, x] = qr.GetModule(x, y);
                    }
                }

                var png = PngWriter.EncodeGrayscale(matrix, scale: 8);
                _lastFile = global::System.IO.Path.Combine(global::System.IO.Path.GetTempPath(),
                    "nuvio-qr-" + Guid.NewGuid().ToString("N") + ".png");

                _image.ResourceUrl = _lastFile;
                _lastPayload = payload;
                return true;
            }
            catch (Exception ex)
            {
                global::Tizen.Log.Error("NuvioTV", "QR render failed: " + ex.Message);
                return false;
            }
        }

        protected override void Dispose(DisposeTypes type)
        {
            if (type == DisposeTypes.Explicit && _lastFile != null)
            {
                try
                {
                    if (File.Exists(_lastFile)) File.Delete(_lastFile);
                }
                catch { /* best effort */ }
                _lastFile = null;
            }
            base.Dispose(type);
        }
    }
}

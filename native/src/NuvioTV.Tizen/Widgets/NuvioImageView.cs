using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NuvioTV.Tizen.Platform;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;

namespace NuvioTV.Tizen.Widgets
{
    /// <summary>
    /// Async image view: resolves a URL (or fallback chain poster → background →
    /// logo) through ImageCache, showing a placeholder tile while loading and a
    /// colored fallback tile when every candidate fails — the same visual
    /// contract as the webapp's image fallbacks.
    /// </summary>
    public sealed class NuvioImageView : View
    {
        private readonly ImageView _image = new ImageView();
        private readonly View _placeholder = new View();
        private ImageCache _cache;
        private CancellationTokenSource _loadCts;

        public NuvioImageView()
        {
            WidthResizePolicy = ResizePolicyType.FillToParent;
            HeightResizePolicy = ResizePolicyType.FillToParent;

            _placeholder.WidthResizePolicy = ResizePolicyType.FillToParent;
            _placeholder.HeightResizePolicy = ResizePolicyType.FillToParent;
            _placeholder.BackgroundColor = new Color(0.133f, 0.133f, 0.133f, 1f); // #222222 cardBg tile

            _image.WidthResizePolicy = ResizePolicyType.FillToParent;
            _image.HeightResizePolicy = ResizePolicyType.FillToParent;
            _placeholder.Hide();

            Add(_placeholder);
            Add(_image);
        }

        /// <summary>Injected by app boot; tests may substitute.</summary>
        public void Bind(ImageCache cache)
        {
            _cache = cache;
        }

        /// <summary>Fallback color of the tile used while loading and on total failure.</summary>
        public Color FallbackColor
        {
            get { return _placeholder.BackgroundColor; }
            set { _placeholder.BackgroundColor = value; }
        }

        public float CornerRadiusValue
        {
            get { return _image.CornerRadius; }
            set
            {
                _image.CornerRadius = value;
                _placeholder.CornerRadius = value;
            }
        }

        /// <summary>
        /// Loads the first candidate that resolves. Empty/null candidates are skipped.
        /// Safe to call repeatedly: previous loads are cancelled.
        /// </summary>
        public void Load(IEnumerable<string> urls)
        {
            _loadCts?.Cancel();
            _loadCts = new CancellationTokenSource();
            var token = _loadCts.Token;

            _ = LoadCoreAsync(urls, token);
        }

        private async Task LoadCoreAsync(IEnumerable<string> urls, CancellationToken token)
        {
            ShowPlaceholder();

            if (_cache == null || urls == null)
            {
                return;
            }

            foreach (var url in urls)
            {
                if (string.IsNullOrWhiteSpace(url)) continue;
                token.ThrowIfCancellationRequested();

                var localPath = await _cache.GetAsync(url);
                if (token.IsCancellationRequested)
                {
                    return;
                }
                if (localPath != null)
                {
                    SetImage(localPath);
                    return;
                }
            }
            // Total failure: keep the colored tile visible (webapp parity).
        }

        private void SetImage(string localPath)
        {
            _image.ResourceUrl = localPath;
            _image.Show();
            _placeholder.Hide();
        }

        private void ShowPlaceholder()
        {
            _image.Hide();
            _placeholder.Show();
        }
    }
}

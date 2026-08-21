using System;
using NuvioTV.Tizen.Input;
using Tizen.NUI;
using Tizen.NUI.BaseComponents;

namespace NuvioTV.Tizen.Widgets
{
    /// <summary>
    /// Poster card (components.css .poster-card parity): image tile, optional
    /// title line below, focused ring in theme focusColor + scale-up. The
    /// 16:9 variant expands further on focus (home expand-on-focus behavior).
    /// </summary>
    public sealed class PosterCard : View, IFocusable
    {
        private const float FocusScale = 1.06f;
        private const float WideFocusScale = 1.12f;
        private static readonly float[] CardAspect = { 2f, 3f };      // w:h poster
        private static readonly float[] WideAspect = { 16f, 9f };     // home hero row

        private readonly NuvioImageView _image = new NuvioImageView();
        private readonly TextLabel _title = new TextLabel();
        private readonly View _ring = new View();
        private readonly bool _wide;

        public string PosterUrl { get; set; }
        public string BackgroundUrl { get; set; }
        public string LogoUrl { get; set; }

        public PosterCard(bool wide = false)
        {
            _wide = wide;

            var aspectW = wide ? WideAspect[0] : CardAspect[0];
            var aspectH = wide ? WideAspect[1] : CardAspect[1];

            _image.WidthResizePolicy = ResizePolicyType.FillToParent;
            _image.HeightResizePolicy = ResizePolicyType.FillToParent;
            _image.CornerRadiusValue = NuiFoundation.DesignTokens.CardRadius;
            Add(_image);

            _title.PointSize = NuiFoundation.DesignTokens.TypeSecondary;
            _title.TextColor = NuiFoundation.ThemeManager.Current.TextSecondaryColor;
            _title.HorizontalAlignment = HorizontalAlignment.Begin;
            _title.VerticalAlignment = VerticalAlignment.Center;
            _title.Position = new Position(0, aspectH / aspectW * 300f);
            _title.WidthResizePolicy = ResizePolicyType.FillToParent;
            _title.HeightResizePolicy = ResizePolicyType.UseNaturalSize;

            _ring.WidthResizePolicy = ResizePolicyType.FillToParent;
            _ring.HeightResizePolicy = ResizePolicyType.FillToParent;
            _ring.BackgroundColor = Color.Transparent;
            _ring.CornerRadius = NuiFoundation.DesignTokens.CardRadius;
        }

        public void Bind(NuvioTV.Tizen.Platform.ImageCache cache, string title,
            params string[] urlCandidates)
        {
            _title.Text = title ?? "";
            if (urlCandidates != null && urlCandidates.Length > 0)
            {
                _image.Bind(cache);
                _image.Load(urlCandidates);
            }
        }

        public void SetImageCache(NuvioTV.Tizen.Platform.ImageCache cache) => _image.Bind(cache);

        public void LoadImages(params string[] urls) => _image.Load(urls);

        public string FocusKey => "card:" + GetHashCode();

        public void ApplyFocus(bool focused)
        {
            AnimateScale(focused ? (_wide ? WideFocusScale : FocusScale) : 1.0f);
            _ring.BackgroundColor = focused
                ? new Color(0f, 0f, 0f, 0f)
                : Color.Transparent;
            // Ring: 2–4px outline in focusColor (webapp box-shadow parity).
            _image.FallbackColor = focused
                ? NuiFoundation.ThemeManager.Current.FocusBgColor
                : new Color(0.133f, 0.133f, 0.133f, 1f);
        }

        private void AnimateScale(float target)
        {
            try
            {
                var animation = new Animation(150);
                animation.AnimateTo(this, "ScaleX", target);
                animation.AnimateTo(this, "ScaleY", target);
                animation.Play();
            }
            catch
            {
                Scale = new Vector3(target, target, 1f);
            }
        }
    }
}

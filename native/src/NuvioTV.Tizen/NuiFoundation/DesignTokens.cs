namespace NuvioTV.Tizen.NuiFoundation
{
    /// <summary>
    /// Fixed 1920x1080 design space constants resolved from css/base.css clamp()
    /// expressions and Appendix C of the rewrite plan. All values are logical
    /// pixels; NUI PointSize maps 1:1 because the target panel is fixed 1080p.
    /// </summary>
    public static class DesignTokens
    {
        // Layout gutters: clamp(28px, 3vw, 56px) / clamp(32px, 3.6vw, 72px) at 1920w.
        public const int SafeGutter = 56;
        public const int SafeGutterWide = 69;

        // Cards: clamp(14px, 1.1vw, 22px) radius / clamp(12px, 1vw, 18px) gap.
        public const int CardRadius = 21;
        public const int CardGap = 18;

        public const int ButtonHeight = 60;
        public const int ControlSize = 64;

        public const int DetailSafeX = 96;
        public const int EpisodeSafeX = 112;

        // Type scale (Appendix C): caption 16 / secondary 18 / body 20 / subtitle 28 / title 48.
        public const int TypeCaption = 16;
        public const int TypeSecondary = 18;
        public const int TypeBody = 20;
        public const int TypeSubtitle = 28;
        public const int TypeTitle = 48;

        // Motion (Appendix C).
        public const int RouteSlideDistancePx = 160;
        public const int RouteSlideDurationMs = 240;
        public const int DialogFadeInMs = 200;
        public const int DialogScaleMs = 280;
        public const int DialogExitMs = 150;
        public const int RowScrollMs = 160;
        public const int VerticalScrollMs = 150;

        // Input timing.
        public const int HoldOkMs = 650;
        public const int BackDebounceMs = 250;
        public const int KeyRepeatThrottleMs = 80;
        public const int KeyRepeatThrottleFastMs = 112;
        public const int SidebarAutoCollapseMs = 4000;

        public const float SpringStiffness = 180f;
        public const float SpringDamping = 0.95f;
        public const int SpringSettleMs = 440;
    }
}

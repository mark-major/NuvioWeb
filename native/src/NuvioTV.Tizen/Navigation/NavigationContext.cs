namespace NuvioTV.Tizen.Navigation
{
    /// <summary>Context passed to ScreenBase.MountAsync (navigation metadata).</summary>
    public sealed class NavigationContext
    {
        public NavigationContext(Route route, bool isBackNavigation, RouteStateStore stateStore)
        {
            Route = route;
            IsBackNavigation = isBackNavigation;
            StateStore = stateStore;
        }

        public Route Route { get; }

        public bool IsBackNavigation { get; }

        /// <summary>Shared per-app route state store for capture/restore.</summary>
        public RouteStateStore StateStore { get; }
    }
}

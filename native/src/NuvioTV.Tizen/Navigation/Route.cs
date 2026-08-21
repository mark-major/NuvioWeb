using System;
using System.Collections.Generic;

namespace NuvioTV.Tizen.Navigation
{
    /// <summary>The 25 routes of the webapp router (js/ui/navigation/router.js).</summary>
    public enum Route
    {
        Home,
        Player,
        Account,
        AuthQrSignIn,
        AuthSignIn,
        SyncCode,
        ProfileSelection,
        ExperienceModeSelection,
        EssentialAddonSetup,
        Detail,
        Library,
        Search,
        Discover,
        Settings,
        DebugConsole,
        Trakt,
        SupportersContributors,
        LicensesAttributions,
        Plugin,
        Plugins,
        CatalogOrder,
        Stream,
        CastDetail,
        CatalogSeeAll,
        FolderDetail
    }

    /// <summary>Navigation options mirroring router.js navigate() options.</summary>
    public sealed class NavigateOptions
    {
        public bool FromHistory { get; set; }
        public bool SkipStackPush { get; set; }
        public bool ReplaceHistory { get; set; }
        public bool IsBackNavigation { get; set; }
    }

    public sealed class BackOptions
    {
        public bool SkipConsume { get; set; }
    }
}

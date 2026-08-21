using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using NuvioTV.Core.Auth;
using NuvioTV.Core.Media;
using NuvioTV.Core.Storage;
using NuvioTV.Tizen.Platform;
namespace NuvioTV.Tizen.NuiFoundation
{
    /// <summary>
    /// Composition root mirroring js/app.js module singletons. Built once by
    /// NuvioApp during boot; screens and services read from here.
    /// </summary>
    public static class AppServices
    {
        public static JsonFileStore FileStore { get; private set; }
        public static HttpClient Http { get; private set; }
        public static AuthManager Auth { get; private set; }
        public static DeviceSessionRegistration DeviceRegistration { get; private set; }

        /// <summary>Resource dir of the installed package (…/res).</summary>
        public static string ResourceDir { get; private set; }

        /// <summary>Writable app-data dir for the JSON key-value store.</summary>
        public static string DataDir { get; private set; }

        public static ImageCache Images { get; private set; }


        /// <summary>Application router; assigned during NuvioApp boot.</summary>
        public static Navigation.Router Router { get; set; }

        /// <summary>UI-thread marshaler wired by NuvioApp (AddIdle).</summary>
        public static Action<Action> PostToUi { get; set; }

        /// <summary>Lazily created addon repository over the shared file store.</summary>
        private static Core.Addons.AddonRepository _addons;
        public static Core.Addons.AddonRepository Addons
        {
            get
            {
                if (_addons == null)
                {
                    var nuvioHttp = new global::NuvioTV.Core.Networking.NuvioHttpClient(Http, Auth);
                    _addons = new Core.Addons.AddonRepository(FileStore,
                        new Core.Addons.StremioAddonClient(nuvioHttp));
                }
                return _addons;
            }
        }

        /// <summary>Lazily created saved-library service over shared stores.</summary>
        private static Core.Sync.SavedLibrarySyncService _savedLibrary;
        public static Core.Sync.SavedLibrarySyncService SavedLibrary
        {
            get
            {
                if (_savedLibrary == null)
                {
                    _savedLibrary = new Core.Sync.SavedLibrarySyncService(
                        new Core.Auth.SupabaseClient(Http, Auth),
                        Auth,
                        new Core.Sync.ProfileManager(FileStore),
                        FileStore);
                }
                return _savedLibrary;
            }
        }

        public static void Initialize(string resourceDir, string dataDir)
        {
            ResourceDir = resourceDir;
            DataDir = dataDir;

            FileStore = new JsonFileStore(dataDir);
            Http = new HttpClient(new HttpClientHandler
            {
                AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate
            });
            Auth = new AuthManager(Http, FileStore);
            Images = new ImageCache(FileStore, Http, dataDir);
            DeviceRegistration = new DeviceSessionRegistration(
                Http,
                Auth,
                FileStore,
                new TizenDeviceMetadata(),
                logWarning: message => global::Tizen.Log.Warn("NuvioTV", "device-registration: " + message));
        }


        /// <summary>I18n loader: locale name → res/i18n/{locale}.json contents.</summary>
        public static Task<string> LoadLocaleJsonAsync(string locale)
        {
            return File.ReadAllTextAsync(Path.Combine(ResourceDir ?? "", "i18n", locale + ".json"));
        }

        public static Stream OpenConfigStream()
        {
            return File.OpenRead(Path.Combine(ResourceDir ?? "", "config", "nuvio.env.json"));
        }

        /// <summary>Best-effort system language, e.g. "en_US" → passed to I18n resolution.</summary>
        public static string GetSystemLocale()
        {
            try
            {
                return global::Tizen.System.SystemSettings.LocaleLanguage;
            }
            catch (Exception ex)
            {
                global::Tizen.Log.Warn("NuvioTV", "system locale unavailable: " + ex.Message);
                return null;
            }
        }
    }
}

using System;
using System.Text.Json.Serialization;
using NuvioTV.Core.Storage;

namespace NuvioTV.Core.Settings
{
    /// <summary>Envelope of experienceMode (experienceModeStore.js parity).</summary>
    public sealed class ExperienceModeSettings
    {
        [JsonPropertyName("mode")]
        public string Mode { get; set; }

        [JsonPropertyName("addonSetupSkipped")]
        public bool AddonSetupSkipped { get; set; }
    }

    /// <summary>
    /// Port of js/data/local/experienceModeStore.js: profile-scoped
    /// experienceMode store with ESSENTIAL/ADVANCED validation.
    /// </summary>
    public static class ExperienceModeStore
    {
        public const string StorageKey = "experienceMode";

        private static readonly ProfileScopedStore<ExperienceModeSettings> Store =
            ProfileScopedStore<ExperienceModeSettings>.Create(StorageKey, Normalize, ShallowMerge);

        public static ExperienceModeSettings Normalize(ExperienceModeSettings value)
        {
            var source = value ?? new ExperienceModeSettings();
            var mode = (source.Mode ?? "").Trim().ToUpperInvariant();
            return new ExperienceModeSettings
            {
                Mode = (mode == "ESSENTIAL" || mode == "ADVANCED") ? mode : null,
                AddonSetupSkipped = source.AddonSetupSkipped
            };
        }

        private static ExperienceModeSettings ShallowMerge(ExperienceModeSettings current, ExperienceModeSettings partial)
        {
            var baseValue = current ?? new ExperienceModeSettings();
            var overlay = partial ?? baseValue;
            return new ExperienceModeSettings
            {
                Mode = overlay.Mode ?? baseValue.Mode,
                AddonSetupSkipped = overlay.AddonSetupSkipped || baseValue.AddonSetupSkipped
            };
        }

        public static System.Threading.Tasks.Task<ExperienceModeSettings> GetAsync(string profileId) =>
            Store.GetAsync(profileId);

        public static System.Threading.Tasks.Task SetAsync(string profileId, ExperienceModeSettings partial) =>
            Store.SetAsync(profileId, partial);

        public static async System.Threading.Tasks.Task<bool> IsEssentialAsync(string profileId)
        {
            var settings = await Store.GetAsync(profileId);
            return settings?.Mode == "ESSENTIAL";
        }
    }
}

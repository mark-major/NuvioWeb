using System.Text.Json.Serialization;

namespace NuvioTV.Core.Settings
{
    /// <summary>
    /// Port of the themeSettings envelope from js/data/local/themeStore.js.
    /// JSON property names match the webapp byte-for-byte (sync parity).
    /// </summary>
    public sealed class ThemeSettings
    {
        [JsonPropertyName("mode")]
        public string Mode { get; set; } = "dark";

        [JsonPropertyName("themeName")]
        public string ThemeName { get; set; } = "WHITE";

        [JsonPropertyName("accentColor")]
        public string AccentColor { get; set; } = "#ffffff";

        [JsonPropertyName("fontFamily")]
        public string FontFamily { get; set; } = "INTER";

        [JsonPropertyName("language")]
        public string Language { get; set; }

        [JsonPropertyName("amoledMode")]
        public bool AmoledMode { get; set; }

        [JsonPropertyName("amoledSurfacesMode")]
        public bool AmoledSurfacesMode { get; set; }

        [JsonPropertyName("settingsUiStyle")]
        public string SettingsUiStyle { get; set; } = "CLASSIC";
    }
}

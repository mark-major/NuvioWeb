using System;
using NuvioTV.Core.Auth;

namespace NuvioTV.Tizen.Platform
{
    /// <summary>
    /// Tizen-native device metadata for backend device-session registration.
    /// Plan Task 3.3: platform is "tizen-native"; model/firmware come from
    /// global::Tizen.System.Information. All reads are best-effort with safe fallbacks.
    /// </summary>
    public sealed class TizenDeviceMetadata : IDeviceMetadata
    {
        private const string FallbackDeviceName = "Tizen TV";

        public string PlatformName
        {
            get { return "tizen-native"; }
        }

        public string DeviceName
        {
            get
            {
                try
                {
                    // Tizen.System.Information exposes raw key lookups only.
                    global::Tizen.System.Information.TryGetValue(
                        "http://tizen.org/system/model_name", out string model);
                    if (!string.IsNullOrWhiteSpace(model))
                    {
                        return model.Trim();
                    }
                }
                catch
                {
                    // Model access is optional on emulators and older TVs.
                }

                return FallbackDeviceName;
            }
        }

        public string FirmwareVersion
        {
            get
            {
                try
                {
                    if (global::Tizen.System.Information.TryGetValue(
                            "http://tizen.org/feature/platform.version", out string version) &&
                        !string.IsNullOrWhiteSpace(version))
                    {
                        return version.Trim();
                    }
                }
                catch
                {
                    // Version access is optional on emulators and older TVs.
                }

                return "";
            }
        }
    }
}

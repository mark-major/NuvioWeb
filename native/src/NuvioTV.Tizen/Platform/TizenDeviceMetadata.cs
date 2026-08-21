using System;
using NuvioTV.Core.Auth;

namespace NuvioTV.Tizen.Platform
{
    /// <summary>
    /// Tizen-native device metadata for backend device-session registration.
    /// Plan Task 3.3: platform is "tizen-native"; model/firmware come from
    /// Tizen.System.Information. All reads are best-effort with safe fallbacks.
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
                    var model = Tizen.System.Information.Model;
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
                    var version = Tizen.System.Information.PlatformVersion;
                    return string.IsNullOrWhiteSpace(version) ? "" : version.Trim();
                }
                catch
                {
                    return "";
                }
            }
        }
    }
}

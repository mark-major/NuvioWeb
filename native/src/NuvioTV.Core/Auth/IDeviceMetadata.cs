namespace NuvioTV.Core.Auth
{
    /// <summary>
    /// Device metadata used for backend device-session registration.
    /// JS parity: the metadata shape produced by resolveCurrentDeviceMetadata
    /// (js/core/auth/deviceSessionRegistration.js:173-191).
    /// </summary>
    public interface IDeviceMetadata
    {
        /// <summary>Platform identifier sent as p_platform (native: "tizen-native").</summary>
        string PlatformName { get; }

        /// <summary>Human-readable device label sent as p_device_name.</summary>
        string DeviceName { get; }

        /// <summary>Firmware/platform version string (informational).</summary>
        string FirmwareVersion { get; }
    }
}

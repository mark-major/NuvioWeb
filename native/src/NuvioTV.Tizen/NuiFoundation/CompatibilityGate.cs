using System;
using System.Globalization;

namespace NuvioTV.Tizen.NuiFoundation
{
    /// <summary>
    /// Port of boot-guard.js compatibility decision: the app requires a Tizen
    /// 5.5+ platform. Runs before any NUI work so an unsupported device gets a
    /// clean exit instead of a broken render loop.
    /// </summary>
    public static class CompatibilityGate
    {
        public const string PlatformVersionKey = "http://tizen.org/feature/platform.version";

        public sealed class Result
        {
            public bool Supported;
            public string PlatformVersion;
        }

        /// <summary>Queries the device platform version via Tizen.System.Information.</summary>
        public static Result Check(Func<string, string> tryGetPlatformVersion)
        {
            var version = tryGetPlatformVersion?.Invoke(PlatformVersionKey);
            return new Result
            {
                Supported = IsSupported(version),
                PlatformVersion = version
            };
        }

        /// <summary>Parses "5.5" style versions and requires major &gt; 5 or (major == 5 &amp;&amp; minor &gt;= 5).</summary>
        public static bool IsSupported(string platformVersion)
        {
            if (!TryParseVersion(platformVersion, out var major, out var minor))
            {
                return false;
            }
            return major > 5 || (major == 5 && minor >= 5);
        }

        public static bool TryParseVersion(string value, out int major, out int minor)
        {
            major = 0;
            minor = 0;
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }
            var parts = value.Trim().Split('.');
            if (parts.Length == 0 ||
                !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out major))
            {
                return false;
            }
            if (parts.Length > 1)
            {
                // Tizen reports minor like "5.5"; tolerate trailing junk ("5.5.0").
                var minorText = parts[1];
                var end = 0;
                while (end < minorText.Length && char.IsDigit(minorText[end]))
                {
                    end++;
                }
                if (end > 0)
                {
                    int.TryParse(minorText.Substring(0, end), NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out minor);
                }
            }
            return true;
        }
    }
}

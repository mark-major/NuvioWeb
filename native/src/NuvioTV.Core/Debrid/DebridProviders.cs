using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace NuvioTV.Core.Debrid
{
    /// <summary>Debrid provider ids. js/core/debrid/debridProviders.js DEBRID_PROVIDER_IDS.</summary>
    public static class DebridProviderIds
    {
        public const string Torbox = "torbox";
        public const string Premiumize = "premiumize";
        public const string RealDebrid = "realdebrid";
    }

    /// <summary>Provider capabilities. js DEBRID_CAPABILITIES.</summary>
    public static class DebridCapabilities
    {
        public const string ClientResolve = "clientResolve";
        public const string LocalTorrentCacheCheck = "localTorrentCacheCheck";
        public const string LocalTorrentResolve = "localTorrentResolve";
        public const string CloudLibrary = "cloudLibrary";
    }

    /// <summary>Auth methods. js DEBRID_AUTH_METHODS.</summary>
    public static class DebridAuthMethods
    {
        public const string ApiKey = "apiKey";
        public const string DeviceCode = "deviceCode";
    }

    /// <summary>Static provider descriptor. js PROVIDERS entries.</summary>
    public sealed class DebridProvider
    {
        public string Id { get; }
        public string DisplayName { get; }
        public string ShortName { get; }
        public bool VisibleInUi { get; }
        public string AuthMethod { get; }
        public string ApiKeyField { get; }
        public IReadOnlyList<string> Capabilities { get; }

        public DebridProvider(
            string id,
            string displayName,
            string shortName,
            bool visibleInUi,
            string authMethod,
            string apiKeyField,
            IReadOnlyList<string> capabilities)
        {
            Id = id;
            DisplayName = displayName;
            ShortName = shortName;
            VisibleInUi = visibleInUi;
            AuthMethod = authMethod;
            ApiKeyField = apiKeyField;
            Capabilities = capabilities;
        }
    }

    /// <summary>Provider + resolved api key pair. js configuredServices() credential shape.</summary>
    public sealed class DebridCredential
    {
        public DebridProvider Provider { get; }
        public string ApiKey { get; }

        public DebridCredential(DebridProvider provider, string apiKey)
        {
            Provider = provider;
            ApiKey = apiKey;
        }
    }

    /// <summary>
    /// Static debrid provider registry. Verbatim port of
    /// js/core/debrid/debridProviders.js (DebridProviders object).
    /// Settings are a flat string map keyed by provider apiKeyField
    /// ("torboxApiKey" / "premiumizeApiKey" / "realDebridApiKey") plus
    /// optional "preferredResolverProviderId".
    /// </summary>
    public static class DebridProviders
    {
        private static readonly DebridProvider[] Providers =
        {
            new DebridProvider(
                DebridProviderIds.Torbox,
                "Torbox",
                "TB",
                visibleInUi: true,
                authMethod: DebridAuthMethods.DeviceCode,
                apiKeyField: "torboxApiKey",
                capabilities: new[]
                {
                    DebridCapabilities.ClientResolve,
                    DebridCapabilities.LocalTorrentCacheCheck,
                    DebridCapabilities.LocalTorrentResolve,
                    DebridCapabilities.CloudLibrary
                }),
            new DebridProvider(
                DebridProviderIds.Premiumize,
                "Premiumize",
                "PM",
                visibleInUi: true,
                authMethod: DebridAuthMethods.DeviceCode,
                apiKeyField: "premiumizeApiKey",
                capabilities: new[]
                {
                    DebridCapabilities.ClientResolve,
                    DebridCapabilities.LocalTorrentCacheCheck,
                    DebridCapabilities.LocalTorrentResolve,
                    DebridCapabilities.CloudLibrary
                }),
            new DebridProvider(
                DebridProviderIds.RealDebrid,
                "Real-Debrid",
                "RD",
                visibleInUi: false,
                authMethod: DebridAuthMethods.ApiKey,
                apiKeyField: "realDebridApiKey",
                capabilities: new[] { DebridCapabilities.ClientResolve })
        };

        private static readonly Regex DashUnderscoreRun = new Regex("[-_]+", RegexOptions.Compiled);

        public static IReadOnlyList<DebridProvider> All()
        {
            return Providers.ToList().AsReadOnly();
        }

        public static IReadOnlyList<DebridProvider> Visible()
        {
            return All().Where(provider => provider.VisibleInUi).ToList().AsReadOnly();
        }

        public static DebridProvider ById(string providerId)
        {
            var normalized = NormalizeProviderId(providerId);
            return Providers.FirstOrDefault(provider => provider.Id == normalized);
        }

        public static bool IsSupported(string providerId)
        {
            return ById(providerId) != null;
        }

        public static bool Supports(string providerId, string capability)
        {
            var provider = ById(providerId);
            return provider != null && capability != null && provider.Capabilities.Contains(capability);
        }

        public static string DisplayName(string providerId)
        {
            var provider = ById(providerId);
            if (provider != null)
            {
                return provider.DisplayName;
            }
            return FallbackDisplayName(providerId);
        }

        public static string ApiKeyFor(IReadOnlyDictionary<string, string> settings, string providerId)
        {
            var provider = ById(providerId);
            if (provider == null || settings == null)
            {
                return "";
            }
            settings.TryGetValue(provider.ApiKeyField, out var value);
            return (value ?? "").Trim();
        }

        public static IReadOnlyList<DebridCredential> ConfiguredServices(IReadOnlyDictionary<string, string> settings)
        {
            return Providers
                .Where(provider => provider.VisibleInUi)
                .Select(provider => new DebridCredential(provider, ApiKeyFor(settings, provider.Id)))
                .Where(credential => !string.IsNullOrEmpty(credential.ApiKey))
                .ToList()
                .AsReadOnly();
        }

        public static IReadOnlyList<DebridCredential> ConfiguredResolverServices(IReadOnlyDictionary<string, string> settings)
        {
            return ConfiguredServices(settings)
                .Where(credential =>
                    credential.Provider.Capabilities.Contains(DebridCapabilities.ClientResolve) ||
                    credential.Provider.Capabilities.Contains(DebridCapabilities.LocalTorrentResolve))
                .ToList()
                .AsReadOnly();
        }

        public static DebridCredential PreferredResolverService(IReadOnlyDictionary<string, string> settings)
        {
            var services = ConfiguredResolverServices(settings);
            if (services.Count == 0)
            {
                return null;
            }
            var preferredRaw = settings != null && settings.TryGetValue("preferredResolverProviderId", out var preferred)
                ? preferred
                : null;
            var preferredId = ById(preferredRaw)?.Id ?? "";
            return services.FirstOrDefault(credential => credential.Provider.Id == preferredId) ?? services[0];
        }

        /// <summary>js normalizeProviderId: trim + lowercase with alias mapping.</summary>
        public static string NormalizeProviderId(string providerId)
        {
            var normalized = (providerId ?? "").Trim().ToLowerInvariant();
            if (normalized == "real-debrid" || normalized == "real_debrid" || normalized == "rd")
            {
                return DebridProviderIds.RealDebrid;
            }
            if (normalized == "tb")
            {
                return DebridProviderIds.Torbox;
            }
            if (normalized == "pm")
            {
                return DebridProviderIds.Premiumize;
            }
            return normalized;
        }

        /// <summary>js fallbackDisplayName: title-case each dash/underscore separated part.</summary>
        public static string FallbackDisplayName(string providerId)
        {
            var value = (providerId ?? "").Trim();
            if (value.Length == 0)
            {
                return "Debrid";
            }
            var parts = DashUnderscoreRun.Replace(value, " ")
                .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var builder = new StringBuilder();
            foreach (var part in parts)
            {
                if (builder.Length > 0)
                {
                    builder.Append(' ');
                }
                builder.Append(char.ToUpperInvariant(part[0]));
                if (part.Length > 1)
                {
                    builder.Append(part.Substring(1).ToLowerInvariant());
                }
            }
            var joined = builder.ToString();
            return joined.Length > 0 ? joined : "Debrid";
        }
    }
}

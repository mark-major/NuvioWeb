using System;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NuvioTV.Core.Configuration
{
    public static class AppConfig
    {
        public static string SupabaseUrl { get; private set; } = "";
        public static string SupabaseAnonKey { get; private set; } = "";
        public static string SupabaseFallbackUrl { get; private set; } = "";
        public static string TvLoginWebBaseUrl { get; private set; } = "";
        public static string YoutubeProxyUrl { get; private set; } = "youtube-proxy.html";
        public static string ParentalGuideApiUrl { get; private set; } = "https://api.tiffara.com/";
        public static string IntroDbApiUrl { get; private set; } = "https://api.introdb.app/";
        public static string ImdbRatingsApiBaseUrl { get; private set; } = "";
        public static string ImdbTapframeApiBaseUrl { get; private set; } = "";
        public static string MdbListApiBaseUrl { get; private set; } = "https://api.mdblist.com/";
        public static string AvatarPublicBaseUrl { get; private set; } = "";
        public static string UniqueContributionsBaseUrl { get; private set; } = "";
        public static string DonationsBaseUrl { get; private set; } = "";
        public static string DonationsDonateUrl { get; private set; } = "";
        public static string SponsorNames { get; private set; } = "ragmehos.";
        public static string TmdbApiKey { get; private set; } = "";
        public static string TraktClientId { get; private set; } = "";
        public static string TraktClientSecret { get; private set; } = "";
        public static string TraktApiUrl { get; private set; } = "https://api.trakt.tv/";
        public static string TraktRedirectUri { get; private set; } = "urn:ietf:wg:oauth:2.0:oob";
        public static string SimklClientId { get; private set; } = "";
        public static string SimklApiUrl { get; private set; } = "https://api.simkl.com";
        public static string SimklAppName { get; private set; } = "nuvio";
        public static string PremiumizeClientId { get; private set; } = "";
        public static string AppVersion { get; private set; } = "";

        private class AppConfigData
        {
            [JsonPropertyName("SupabaseUrl")]
            public string SupabaseUrl { get; set; }

            [JsonPropertyName("SupabaseAnonKey")]
            public string SupabaseAnonKey { get; set; }

            [JsonPropertyName("SupabaseFallbackUrl")]
            public string SupabaseFallbackUrl { get; set; }

            [JsonPropertyName("TvLoginWebBaseUrl")]
            public string TvLoginWebBaseUrl { get; set; }

            [JsonPropertyName("YoutubeProxyUrl")]
            public string YoutubeProxyUrl { get; set; }

            [JsonPropertyName("ParentalGuideApiUrl")]
            public string ParentalGuideApiUrl { get; set; }

            [JsonPropertyName("IntroDbApiUrl")]
            public string IntroDbApiUrl { get; set; }

            [JsonPropertyName("ImdbRatingsApiBaseUrl")]
            public string ImdbRatingsApiBaseUrl { get; set; }

            [JsonPropertyName("ImdbTapframeApiBaseUrl")]
            public string ImdbTapframeApiBaseUrl { get; set; }

            [JsonPropertyName("MdbListApiBaseUrl")]
            public string MdbListApiBaseUrl { get; set; }

            [JsonPropertyName("AvatarPublicBaseUrl")]
            public string AvatarPublicBaseUrl { get; set; }

            [JsonPropertyName("UniqueContributionsBaseUrl")]
            public string UniqueContributionsBaseUrl { get; set; }

            [JsonPropertyName("DonationsBaseUrl")]
            public string DonationsBaseUrl { get; set; }

            [JsonPropertyName("DonationsDonateUrl")]
            public string DonationsDonateUrl { get; set; }

            [JsonPropertyName("SponsorNames")]
            public string SponsorNames { get; set; }

            [JsonPropertyName("TmdbApiKey")]
            public string TmdbApiKey { get; set; }

            [JsonPropertyName("TraktClientId")]
            public string TraktClientId { get; set; }

            [JsonPropertyName("TraktClientSecret")]
            public string TraktClientSecret { get; set; }

            [JsonPropertyName("TraktApiUrl")]
            public string TraktApiUrl { get; set; }

            [JsonPropertyName("TraktRedirectUri")]
            public string TraktRedirectUri { get; set; }

            [JsonPropertyName("SimklClientId")]
            public string SimklClientId { get; set; }

            [JsonPropertyName("SimklApiUrl")]
            public string SimklApiUrl { get; set; }

            [JsonPropertyName("SimklAppName")]
            public string SimklAppName { get; set; }

            [JsonPropertyName("PremiumizeClientId")]
            public string PremiumizeClientId { get; set; }
        }

        public static void Load(Stream jsonStream)
        {
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = false
            };

            var data = JsonSerializer.Deserialize<AppConfigData>(jsonStream, options);

            if (data != null)
            {
                SupabaseUrl = data.SupabaseUrl ?? SupabaseUrl;
                SupabaseAnonKey = data.SupabaseAnonKey ?? SupabaseAnonKey;
                SupabaseFallbackUrl = data.SupabaseFallbackUrl ?? SupabaseFallbackUrl;
                TvLoginWebBaseUrl = data.TvLoginWebBaseUrl ?? TvLoginWebBaseUrl;
                YoutubeProxyUrl = data.YoutubeProxyUrl ?? YoutubeProxyUrl;
                ParentalGuideApiUrl = data.ParentalGuideApiUrl ?? ParentalGuideApiUrl;
                IntroDbApiUrl = data.IntroDbApiUrl ?? IntroDbApiUrl;
                ImdbRatingsApiBaseUrl = data.ImdbRatingsApiBaseUrl ?? ImdbRatingsApiBaseUrl;
                ImdbTapframeApiBaseUrl = data.ImdbTapframeApiBaseUrl ?? ImdbTapframeApiBaseUrl;
                MdbListApiBaseUrl = data.MdbListApiBaseUrl ?? MdbListApiBaseUrl;
                AvatarPublicBaseUrl = data.AvatarPublicBaseUrl ?? AvatarPublicBaseUrl;
                UniqueContributionsBaseUrl = data.UniqueContributionsBaseUrl ?? UniqueContributionsBaseUrl;
                DonationsBaseUrl = data.DonationsBaseUrl ?? DonationsBaseUrl;
                DonationsDonateUrl = data.DonationsDonateUrl ?? DonationsDonateUrl;
                SponsorNames = data.SponsorNames ?? SponsorNames;
                TmdbApiKey = data.TmdbApiKey ?? TmdbApiKey;
                TraktClientId = data.TraktClientId ?? TraktClientId;
                TraktClientSecret = data.TraktClientSecret ?? TraktClientSecret;
                TraktApiUrl = data.TraktApiUrl ?? TraktApiUrl;
                TraktRedirectUri = data.TraktRedirectUri ?? TraktRedirectUri;
                SimklClientId = data.SimklClientId ?? SimklClientId;
                SimklApiUrl = data.SimklApiUrl ?? SimklApiUrl;
                SimklAppName = data.SimklAppName ?? SimklAppName;
                PremiumizeClientId = data.PremiumizeClientId ?? PremiumizeClientId;
            }

            // Load AppVersion from assembly informational version
            var assembly = Assembly.GetExecutingAssembly();
            var informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
            AppVersion = informationalVersion?.InformationalVersion ?? "";
        }
    }
}

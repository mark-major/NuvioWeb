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
            [JsonPropertyName("supabaseUrl")]
            public string SupabaseUrl { get; set; }

            [JsonPropertyName("supabaseAnonKey")]
            public string SupabaseAnonKey { get; set; }

            [JsonPropertyName("supabaseFallbackUrl")]
            public string SupabaseFallbackUrl { get; set; }

            [JsonPropertyName("tvLoginWebBaseUrl")]
            public string TvLoginWebBaseUrl { get; set; }

            [JsonPropertyName("youtubeProxyUrl")]
            public string YoutubeProxyUrl { get; set; }

            [JsonPropertyName("parentalGuideApiUrl")]
            public string ParentalGuideApiUrl { get; set; }

            [JsonPropertyName("introDbApiUrl")]
            public string IntroDbApiUrl { get; set; }

            [JsonPropertyName("imdbRatingsApiBaseUrl")]
            public string ImdbRatingsApiBaseUrl { get; set; }

            [JsonPropertyName("imdbTapframeApiBaseUrl")]
            public string ImdbTapframeApiBaseUrl { get; set; }

            [JsonPropertyName("mdbListApiBaseUrl")]
            public string MdbListApiBaseUrl { get; set; }

            [JsonPropertyName("avatarPublicBaseUrl")]
            public string AvatarPublicBaseUrl { get; set; }

            [JsonPropertyName("uniqueContributionsBaseUrl")]
            public string UniqueContributionsBaseUrl { get; set; }

            [JsonPropertyName("donationsBaseUrl")]
            public string DonationsBaseUrl { get; set; }

            [JsonPropertyName("donationsDonateUrl")]
            public string DonationsDonateUrl { get; set; }

            [JsonPropertyName("sponsorNames")]
            public string SponsorNames { get; set; }

            [JsonPropertyName("tmdbApiKey")]
            public string TmdbApiKey { get; set; }

            [JsonPropertyName("traktClientId")]
            public string TraktClientId { get; set; }

            [JsonPropertyName("traktClientSecret")]
            public string TraktClientSecret { get; set; }

            [JsonPropertyName("traktApiUrl")]
            public string TraktApiUrl { get; set; }

            [JsonPropertyName("traktRedirectUri")]
            public string TraktRedirectUri { get; set; }

            [JsonPropertyName("simklClientId")]
            public string SimklClientId { get; set; }

            [JsonPropertyName("simklApiUrl")]
            public string SimklApiUrl { get; set; }

            [JsonPropertyName("simklAppName")]
            public string SimklAppName { get; set; }

            [JsonPropertyName("premiumizeClientId")]
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
            // Task 19.1 wires read-version.mjs → AssemblyInformationalVersion in Tizen project
            var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
            var informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
            AppVersion = informationalVersion?.InformationalVersion ?? "";
        }

        // Test isolation hook; also usable by the app to re-load config cleanly.
        public static void ResetForTests()
        {
            SupabaseUrl = "";
            SupabaseAnonKey = "";
            SupabaseFallbackUrl = "";
            TvLoginWebBaseUrl = "";
            YoutubeProxyUrl = "youtube-proxy.html";
            ParentalGuideApiUrl = "https://api.tiffara.com/";
            IntroDbApiUrl = "https://api.introdb.app/";
            ImdbRatingsApiBaseUrl = "";
            ImdbTapframeApiBaseUrl = "";
            MdbListApiBaseUrl = "https://api.mdblist.com/";
            AvatarPublicBaseUrl = "";
            UniqueContributionsBaseUrl = "";
            DonationsBaseUrl = "";
            DonationsDonateUrl = "";
            SponsorNames = "ragmehos.";
            TmdbApiKey = "";
            TraktClientId = "";
            TraktClientSecret = "";
            TraktApiUrl = "https://api.trakt.tv/";
            TraktRedirectUri = "urn:ietf:wg:oauth:2.0:oob";
            SimklClientId = "";
            SimklApiUrl = "https://api.simkl.com";
            SimklAppName = "nuvio";
            PremiumizeClientId = "";
            AppVersion = "";
        }
    }
}

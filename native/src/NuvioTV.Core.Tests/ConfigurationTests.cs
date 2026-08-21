using System;
using System.IO;
using System.Reflection;
using System.Text.Json;
using NuvioTV.Core.Configuration;
using Xunit;

namespace NuvioTV.Core.Tests
{
    public class ConfigurationTests
    {
        private const string FixtureJson = @"
{
  ""supabaseUrl"": ""https://test.supabase.co"",
  ""supabaseAnonKey"": ""test-anon-key"",
  ""supabaseFallbackUrl"": ""https://fallback.supabase.co"",
  ""tvLoginWebBaseUrl"": ""https://login.test.com"",
  ""youtubeProxyUrl"": ""test-proxy.html"",
  ""parentalGuideApiUrl"": ""https://parental.test.com/"",
  ""introDbApiUrl"": ""https://intro.test.com/"",
  ""imdbRatingsApiBaseUrl"": ""https://imdb-ratings.test.com"",
  ""imdbTapframeApiBaseUrl"": ""https://imdb-tapframe.test.com"",
  ""mdbListApiBaseUrl"": ""https://mdblist.test.com/"",
  ""avatarPublicBaseUrl"": ""https://avatar.test.com"",
  ""uniqueContributionsBaseUrl"": ""https://contributions.test.com"",
  ""donationsBaseUrl"": ""https://donations.test.com"",
  ""donationsDonateUrl"": ""https://donations.test.com/donate"",
  ""sponsorNames"": ""test.sponsor"",
  ""tmdbApiKey"": ""test-tmdb-key"",
  ""traktClientId"": ""test-trakt-id"",
  ""traktClientSecret"": ""test-trakt-secret"",
  ""traktApiUrl"": ""https://trakt.test.com/"",
  ""traktRedirectUri"": ""urn:ietf:wg:oauth:2.0:test"",
  ""simklClientId"": ""test-simkl-id"",
  ""simklApiUrl"": ""https://simkl.test.com"",
  ""simklAppName"": ""testapp"",
  ""premiumizeClientId"": ""test-premiumize-id""
}";

        [Fact]
        public void Load_LoadsAllPropertiesFromJson()
        {
            // Arrange
            AppConfig.ResetForTests();
            var jsonBytes = System.Text.Encoding.UTF8.GetBytes(FixtureJson);
            
            // Act
            using (var stream = new MemoryStream(jsonBytes))
            {
                AppConfig.Load(stream);
            }

            // Assert
            Assert.Equal("https://test.supabase.co", AppConfig.SupabaseUrl);
            Assert.Equal("test-anon-key", AppConfig.SupabaseAnonKey);
            Assert.Equal("https://fallback.supabase.co", AppConfig.SupabaseFallbackUrl);
            Assert.Equal("https://login.test.com", AppConfig.TvLoginWebBaseUrl);
            Assert.Equal("test-proxy.html", AppConfig.YoutubeProxyUrl);
            Assert.Equal("https://parental.test.com/", AppConfig.ParentalGuideApiUrl);
            Assert.Equal("https://intro.test.com/", AppConfig.IntroDbApiUrl);
            Assert.Equal("https://imdb-ratings.test.com", AppConfig.ImdbRatingsApiBaseUrl);
            Assert.Equal("https://imdb-tapframe.test.com", AppConfig.ImdbTapframeApiBaseUrl);
            Assert.Equal("https://mdblist.test.com/", AppConfig.MdbListApiBaseUrl);
            Assert.Equal("https://avatar.test.com", AppConfig.AvatarPublicBaseUrl);
            Assert.Equal("https://contributions.test.com", AppConfig.UniqueContributionsBaseUrl);
            Assert.Equal("https://donations.test.com", AppConfig.DonationsBaseUrl);
            Assert.Equal("https://donations.test.com/donate", AppConfig.DonationsDonateUrl);
            Assert.Equal("test.sponsor", AppConfig.SponsorNames);
            Assert.Equal("test-tmdb-key", AppConfig.TmdbApiKey);
            Assert.Equal("test-trakt-id", AppConfig.TraktClientId);
            Assert.Equal("test-trakt-secret", AppConfig.TraktClientSecret);
            Assert.Equal("https://trakt.test.com/", AppConfig.TraktApiUrl);
            Assert.Equal("urn:ietf:wg:oauth:2.0:test", AppConfig.TraktRedirectUri);
            Assert.Equal("test-simkl-id", AppConfig.SimklClientId);
            Assert.Equal("https://simkl.test.com", AppConfig.SimklApiUrl);
            Assert.Equal("testapp", AppConfig.SimklAppName);
            Assert.Equal("test-premiumize-id", AppConfig.PremiumizeClientId);
        }

        [Fact]
        public void Load_MissingKeysFallBackToDefaults()
        {
            // Arrange - minimal JSON with only some keys
            AppConfig.ResetForTests();
            const string minimalJson = @"{
  ""supabaseUrl"": ""https://minimal.supabase.co""
}";
            using (var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(minimalJson)))
            {
                AppConfig.Load(stream);
            }

            // Assert - provided value
            Assert.Equal("https://minimal.supabase.co", AppConfig.SupabaseUrl);
            
            // Assert - defaults from js/config.js
            Assert.Equal("", AppConfig.SupabaseAnonKey);
            Assert.Equal("", AppConfig.SupabaseFallbackUrl);
            Assert.Equal("", AppConfig.TvLoginWebBaseUrl);
            Assert.Equal("youtube-proxy.html", AppConfig.YoutubeProxyUrl);
            Assert.Equal("https://api.tiffara.com/", AppConfig.ParentalGuideApiUrl);
            Assert.Equal("https://api.introdb.app/", AppConfig.IntroDbApiUrl);
            Assert.Equal("", AppConfig.ImdbRatingsApiBaseUrl);
            Assert.Equal("", AppConfig.ImdbTapframeApiBaseUrl);
            Assert.Equal("https://api.mdblist.com/", AppConfig.MdbListApiBaseUrl);
            Assert.Equal("", AppConfig.AvatarPublicBaseUrl);
            Assert.Equal("", AppConfig.UniqueContributionsBaseUrl);
            Assert.Equal("", AppConfig.DonationsBaseUrl);
            Assert.Equal("", AppConfig.DonationsDonateUrl);
            Assert.Equal("ragmehos.", AppConfig.SponsorNames);
            Assert.Equal("", AppConfig.TmdbApiKey);
            Assert.Equal("", AppConfig.TraktClientId);
            Assert.Equal("", AppConfig.TraktClientSecret);
            Assert.Equal("https://api.trakt.tv/", AppConfig.TraktApiUrl);
            Assert.Equal("urn:ietf:wg:oauth:2.0:oob", AppConfig.TraktRedirectUri);
            Assert.Equal("", AppConfig.SimklClientId);
            Assert.Equal("https://api.simkl.com", AppConfig.SimklApiUrl);
            Assert.Equal("nuvio", AppConfig.SimklAppName);
            Assert.Equal("", AppConfig.PremiumizeClientId);
        }

        [Fact]
        public void Load_NullValuesHandledCorrectly()
        {
            // Arrange - JSON with explicit null values
            AppConfig.ResetForTests();
            const string nullJson = @"{
  ""supabaseUrl"": ""https://null.supabase.co"",
  ""supabaseAnonKey"": null,
  ""youtubeProxyUrl"": null
}";
            using (var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(nullJson)))
            {
                AppConfig.Load(stream);
            }

            // Assert - provided value
            Assert.Equal("https://null.supabase.co", AppConfig.SupabaseUrl);
            
            // Assert - null values should fall back to defaults
            Assert.Equal("", AppConfig.SupabaseAnonKey);
            Assert.Equal("youtube-proxy.html", AppConfig.YoutubeProxyUrl);
        }

        [Fact]
        public void Load_UnknownKeysIgnored()
        {
            // Arrange - JSON with unknown keys
            AppConfig.ResetForTests();
            const string unknownKeysJson = @"{
  ""supabaseUrl"": ""https://unknown.supabase.co"",
  ""unknownKey"": ""some-value"",
  ""anotherUnknownKey"": 123
}";
            // Act - should not throw
            using (var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(unknownKeysJson)))
            {
                AppConfig.Load(stream);
            }

            // Assert
            Assert.Equal("https://unknown.supabase.co", AppConfig.SupabaseUrl);
        }

        [Fact]
        public void AppVersion_IsLoadedFromAssembly()
        {
            // The test should verify that AppVersion is loaded
            // This will be set when Load is called with any valid JSON
            AppConfig.ResetForTests();
            const string simpleJson = @"{ ""supabaseUrl"": ""test"" }";
            var jsonBytes = System.Text.Encoding.UTF8.GetBytes(simpleJson);
            using (var stream = new MemoryStream(jsonBytes))
            {
                AppConfig.Load(stream);
            }

            // AppVersion should be set from assembly informational version
            Assert.NotNull(AppConfig.AppVersion);
            Assert.NotEmpty(AppConfig.AppVersion);
        }
    }
}

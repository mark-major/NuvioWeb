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
  ""SupabaseUrl"": ""https://test.supabase.co"",
  ""SupabaseAnonKey"": ""test-anon-key"",
  ""SupabaseFallbackUrl"": ""https://fallback.supabase.co"",
  ""TvLoginWebBaseUrl"": ""https://login.test.com"",
  ""YoutubeProxyUrl"": ""test-proxy.html"",
  ""ParentalGuideApiUrl"": ""https://parental.test.com/"",
  ""IntroDbApiUrl"": ""https://intro.test.com/"",
  ""ImdbRatingsApiBaseUrl"": ""https://imdb-ratings.test.com"",
  ""ImdbTapframeApiBaseUrl"": ""https://imdb-tapframe.test.com"",
  ""MdbListApiBaseUrl"": ""https://mdblist.test.com/"",
  ""AvatarPublicBaseUrl"": ""https://avatar.test.com"",
  ""UniqueContributionsBaseUrl"": ""https://contributions.test.com"",
  ""DonationsBaseUrl"": ""https://donations.test.com"",
  ""DonationsDonateUrl"": ""https://donations.test.com/donate"",
  ""SponsorNames"": ""test.sponsor"",
  ""TmdbApiKey"": ""test-tmdb-key"",
  ""TraktClientId"": ""test-trakt-id"",
  ""TraktClientSecret"": ""test-trakt-secret"",
  ""TraktApiUrl"": ""https://trakt.test.com/"",
  ""TraktRedirectUri"": ""urn:ietf:wg:oauth:2.0:test"",
  ""SimklClientId"": ""test-simkl-id"",
  ""SimklApiUrl"": ""https://simkl.test.com"",
  ""SimklAppName"": ""testapp"",
  ""PremiumizeClientId"": ""test-premiumize-id""
}";

        [Fact]
        public void Load_LoadsAllPropertiesFromJson()
        {
            // Arrange
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
            const string minimalJson = @"{
  ""SupabaseUrl"": ""https://minimal.supabase.co""
}";
            var jsonBytes = System.Text.Encoding.UTF8.GetBytes(minimalJson);
            
            // Act
            using (var stream = new MemoryStream(jsonBytes))
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
            const string nullJson = @"{
  ""SupabaseUrl"": ""https://null.supabase.co"",
  ""SupabaseAnonKey"": null,
  ""YoutubeProxyUrl"": null
}";
            var jsonBytes = System.Text.Encoding.UTF8.GetBytes(nullJson);
            
            // Act
            using (var stream = new MemoryStream(jsonBytes))
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
            const string unknownKeysJson = @"{
  ""SupabaseUrl"": ""https://unknown.supabase.co"",
  ""UnknownKey"": ""some-value"",
  ""AnotherUnknownKey"": 123
}";
            var jsonBytes = System.Text.Encoding.UTF8.GetBytes(unknownKeysJson);
            
            // Act - should not throw
            using (var stream = new MemoryStream(jsonBytes))
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
            const string simpleJson = @"{ ""SupabaseUrl"": ""test"" }";
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

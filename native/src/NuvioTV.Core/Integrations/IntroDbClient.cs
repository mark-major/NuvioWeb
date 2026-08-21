using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NuvioTV.Core.Configuration;
using NuvioTV.Core.Integrations.Ratings;

namespace NuvioTV.Core.Integrations
{
    /// <summary>
    /// Skip-intro/recap/outro segments from IntroDb. Port of
    /// js/data/repository/skipIntroRepository.js: GET segments?imdb_id=&amp;season=&amp;episode=,
    /// interval conversion accepting start_sec/start_ms, sorted by start time.
    /// </summary>
    public sealed class IntroDbClient
    {
        private readonly HttpClient _httpClient;

        public IntroDbClient(HttpClient httpClient)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        }

        public async Task<IReadOnlyList<SkipInterval>> GetSegmentsAsync(
            string imdbId, int season, int episode, CancellationToken ct = default)
        {
            var normalizedImdbId = NormalizeImdbId(imdbId);
            var baseUrl = NormalizeBaseUrl(AppConfig.IntroDbApiUrl);
            if (baseUrl.Length == 0 || normalizedImdbId.Length == 0 || season <= 0 || episode <= 0)
            {
                return Array.Empty<SkipInterval>();
            }

            var url = baseUrl + "segments?imdb_id=" + Uri.EscapeDataString(normalizedImdbId) +
                      "&season=" + season + "&episode=" + episode;
            var data = await FetchJsonAsync(url, ct).ConfigureAwait(false);
            if (data == null || data.Value.ValueKind != JsonValueKind.Object)
            {
                return Array.Empty<SkipInterval>();
            }

            var intervals = new List<SkipInterval>();
            AddInterval(intervals, data.Value, "intro", "intro");
            AddInterval(intervals, data.Value, "recap", "recap");
            AddInterval(intervals, data.Value, "outro", "outro");
            return intervals.OrderBy(i => i.StartTime).ToList();
        }

        // JS parity toSkipInterval: start_sec|start_ms and end_sec|end_ms; end > start.
        public static SkipInterval? ToSkipInterval(JsonElement segment, string type)
        {
            if (segment.ValueKind != JsonValueKind.Object)
            {
                return null;
            }
            var start = ReadSeconds(segment, "start_sec", "start_ms");
            var end = ReadSeconds(segment, "end_sec", "end_ms");
            if (double.IsNaN(start) || double.IsNaN(end) || end <= start)
            {
                return null;
            }
            return new SkipInterval(start, end, type);
        }

        private static void AddInterval(List<SkipInterval> list, JsonElement root, string property, string type)
        {
            if (root.TryGetProperty(property, out var segment))
            {
                var interval = ToSkipInterval(segment, type);
                if (interval.HasValue)
                {
                    list.Add(interval.Value);
                }
            }
        }

        private static double ReadSeconds(JsonElement element, string secondsName, string millisName)
        {
            if (TryGetNumber(element, secondsName, out var seconds))
            {
                return seconds;
            }
            if (TryGetNumber(element, millisName, out var millis))
            {
                return millis / 1000.0;
            }
            return double.NaN;
        }

        private static bool TryGetNumber(JsonElement element, string name, out double value)
        {
            value = double.NaN;
            return element.ValueKind == JsonValueKind.Object &&
                   element.TryGetProperty(name, out var prop) &&
                   prop.ValueKind == JsonValueKind.Number &&
                   (value = prop.GetDouble()) == value;
        }

        private async Task<JsonElement?> FetchJsonAsync(string url, CancellationToken ct)
        {
            try
            {
                using var response = await _httpClient.GetAsync(url, ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }
                var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(text))
                {
                    return null;
                }
                return JsonSerializer.Deserialize<JsonElement>(text);
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static string NormalizeBaseUrl(string value)
        {
            var normalized = (value ?? "").Trim();
            if (normalized.Length == 0)
            {
                return "";
            }
            return normalized.EndsWith("/") ? normalized : normalized + "/";
        }

        public static string NormalizeImdbId(string value)
        {
            var candidate = (value ?? "").Trim().Split(':')[0];
            return candidate.StartsWith("tt") && candidate.Length > 2 &&
                   candidate.Substring(2).All(char.IsDigit)
                ? candidate
                : "";
        }
    }

    public readonly struct SkipInterval
    {
        public double StartTime { get; }
        public double EndTime { get; }
        public string Type { get; }

        public SkipInterval(double startTime, double endTime, string type)
        {
            StartTime = startTime;
            EndTime = endTime;
            Type = type;
        }
    }
}

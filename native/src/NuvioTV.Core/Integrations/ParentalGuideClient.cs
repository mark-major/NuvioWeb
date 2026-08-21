using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NuvioTV.Core.Configuration;

namespace NuvioTV.Core.Integrations
{
    /// <summary>
    /// Parental guide categories from the tiffara API. Port of
    /// js/data/repository/parentalGuideRepository.js: GET titles/{imdbId}/parentsGuide,
    /// dominant-severity resolution per category (highest voteCount, "none" excluded).
    /// </summary>
    public sealed class ParentalGuideClient
    {
        private readonly HttpClient _httpClient;

        public ParentalGuideClient(HttpClient httpClient)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        }

        public async Task<IReadOnlyList<ParentalCategory>> GetParentalGuideAsync(
            string imdbId, CancellationToken ct = default)
        {
            var normalizedImdbId = NormalizeImdbId(imdbId);
            var baseUrl = NormalizeBaseUrl(AppConfig.ParentalGuideApiUrl);
            if (baseUrl.Length == 0 || normalizedImdbId.Length == 0)
            {
                return Array.Empty<ParentalCategory>();
            }

            var url = baseUrl + "titles/" + Uri.EscapeDataString(normalizedImdbId) + "/parentsGuide";
            JsonElement? data;
            try
            {
                using var response = await _httpClient.GetAsync(url, ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    return Array.Empty<ParentalCategory>();
                }
                var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                data = string.IsNullOrWhiteSpace(text)
                    ? (JsonElement?)null
                    : JsonSerializer.Deserialize<JsonElement>(text);
            }
            catch (Exception)
            {
                return Array.Empty<ParentalCategory>();
            }

            if (data == null || data.Value.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<ParentalCategory>();
            }

            var categories = new List<ParentalCategory>();
            foreach (var category in data.Value.EnumerateArray())
            {
                var resolved = ResolveSeverity(category);
                if (resolved != null)
                {
                    categories.Add(resolved);
                }
            }
            return categories;
        }

        // JS parity resolveSeverity: pick the non-"none" breakdown with the highest
        // voteCount; fall back to "none" when every breakdown is none/zero-vote.
        public static ParentalCategory ResolveSeverity(JsonElement category)
        {
            if (category.ValueKind != JsonValueKind.Object ||
                !category.TryGetProperty("severityBreakdowns", out var breakdowns) ||
                breakdowns.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            string name = null;
            if (category.TryGetProperty("name", out var nameProp) && nameProp.ValueKind == JsonValueKind.String)
            {
                name = nameProp.GetString();
            }

            var candidates = new List<(string Severity, double Votes)>();
            foreach (var entry in breakdowns.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object ||
                    !entry.TryGetProperty("severityLevel", out var level) ||
                    level.ValueKind != JsonValueKind.String)
                {
                    continue;
                }
                var severity = level.GetString().Trim().ToLowerInvariant();
                var votes = 0d;
                if (entry.TryGetProperty("voteCount", out var votesProp) && votesProp.ValueKind == JsonValueKind.Number)
                {
                    votes = votesProp.GetDouble();
                }
                if (severity.Length > 0)
                {
                    candidates.Add((severity, votes));
                }
            }

            var dominant = candidates
                .Where(c => c.Severity != "none")
                .OrderByDescending(c => c.Votes)
                .FirstOrDefault();
            if (dominant.Severity == null)
            {
                var noneEntry = candidates.FirstOrDefault(c => c.Severity == "none");
                if (noneEntry.Severity == null)
                {
                    return null;
                }
                dominant = noneEntry;
            }

            return new ParentalCategory(name, dominant.Severity);
        }

        public static string NormalizeImdbId(string value)
        {
            var candidate = (value ?? "").Trim().Split(':')[0];
            return candidate.StartsWith("tt") && candidate.Length > 2 &&
                   candidate.Substring(2).All(char.IsDigit)
                ? candidate
                : "";
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
    }

    public sealed class ParentalCategory
    {
        public string Name { get; }
        public string Severity { get; }

        public ParentalCategory(string name, string severity)
        {
            Name = name;
            Severity = severity;
        }
    }
}

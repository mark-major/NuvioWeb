using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace NuvioTV.Core.Debrid
{
    /// <summary>
    /// Provider response envelope mirroring js requestJson():
    /// { ok, status, data, text } where data is parsed JSON when possible and
    /// the raw text otherwise. Non-2xx responses are captured, not thrown —
    /// the resolver maps statuses (409 → not_cached, 5xx → service_degraded).
    /// </summary>
    public sealed class DebridHttpResponse
    {
        public bool Ok { get; set; }
        public int Status { get; set; }
        public string Text { get; set; }
        public JsonElement? Data { get; set; }

        /// <summary>data is a JSON object.</summary>
        public bool DataIsObject => Data.HasValue && Data.Value.ValueKind == JsonValueKind.Object;

        /// <summary>data is a JSON array.</summary>
        public bool DataIsArray => Data.HasValue && Data.Value.ValueKind == JsonValueKind.Array;

        /// <summary>js data?.&lt;prop&gt; — property value or null element.</summary>
        public JsonElement? Prop(string name)
        {
            if (!DataIsObject)
            {
                return null;
            }
            if (Data.Value.TryGetProperty(name, out var value))
            {
                return value;
            }
            return null;
        }

        /// <summary>String coercion of a JSON element (numbers keep their raw text).</summary>
        public static string JsonToString(JsonElement? element)
        {
            if (!element.HasValue)
            {
                return null;
            }
            var value = element.Value;
            switch (value.ValueKind)
            {
                case JsonValueKind.String:
                    return value.GetString();
                case JsonValueKind.Number:
                    return value.GetRawText();
                case JsonValueKind.True:
                    return "true";
                case JsonValueKind.False:
                    return "false";
                default:
                    return null;
            }
        }

        /// <summary>Numeric coercion (JS Number()); null when absent/non-numeric.</summary>
        public static double? JsonToNumber(JsonElement? element)
        {
            if (!element.HasValue)
            {
                return null;
            }
            var value = element.Value;
            if (value.ValueKind == JsonValueKind.Number)
            {
                return value.GetDouble();
            }
            if (value.ValueKind == JsonValueKind.String &&
                double.TryParse(value.GetString(), out var parsed))
            {
                return parsed;
            }
            return null;
        }

        /// <summary>Boolean coercion (JS Boolean()/truthiness of explicit values).</summary>
        public static bool? JsonToBool(JsonElement? element)
        {
            if (!element.HasValue)
            {
                return null;
            }
            var value = element.Value;
            switch (value.ValueKind)
            {
                case JsonValueKind.True:
                    return true;
                case JsonValueKind.False:
                    return false;
                default:
                    return null;
            }
        }
    }

    /// <summary>
    /// Unauthenticated provider HTTP sender. Debrid endpoints are authorized
    /// per-request with a Bearer api key (never session tokens), so this runs
    /// on the injected HttpClient/message-handler seam that tests back with
    /// RecordingHandler.
    /// </summary>
    public sealed class DebridHttp
    {
        private readonly HttpClient _httpClient;

        public DebridHttp(HttpClient httpClient)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        }

        public async Task<DebridHttpResponse> SendAsync(
            HttpMethod method,
            string url,
            HttpContent content,
            string apiKey)
        {
            DebridHttpResponse response;
            try
            {
                using (var request = new HttpRequestMessage(method, url))
                {
                    if (!string.IsNullOrEmpty(apiKey))
                    {
                        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
                    }
                    if (content != null && method != HttpMethod.Get && method != HttpMethod.Head)
                    {
                        request.Content = content;
                    }
                    using (var httpResponse = await _httpClient.SendAsync(request).ConfigureAwait(false))
                    {
                        response = await ReadResponseAsync(httpResponse).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception)
            {
                // js requestJson catch: network failure → { ok:false, status:0 }.
                return new DebridHttpResponse { Ok = false, Status = 0 };
            }
            return response;
        }

        public Task<DebridHttpResponse> GetAsync(string url, string apiKey)
        {
            return SendAsync(HttpMethod.Get, url, null, apiKey);
        }

        public Task<DebridHttpResponse> PostFormAsync(string url, string formBody, string apiKey)
        {
            var content = new StringContent(formBody ?? "", Encoding.UTF8,
                "application/x-www-form-urlencoded");
            return SendAsync(HttpMethod.Post, url, content, apiKey);
        }

        public Task<DebridHttpResponse> PostJsonAsync(string url, string jsonBody, string apiKey)
        {
            var content = new StringContent(jsonBody ?? "", Encoding.UTF8, "application/json");
            return SendAsync(HttpMethod.Post, url, content, apiKey);
        }

        /// <summary>Multipart form upload (js FormData body for TorBox createtorrent).</summary>
        public Task<DebridHttpResponse> PostMultipartAsync(
            string url,
            System.Collections.Generic.IReadOnlyDictionary<string, string> fields,
            string apiKey)
        {
            var content = new MultipartFormDataContent();
            foreach (var field in fields)
            {
                content.Add(new StringContent(field.Value ?? ""), field.Key);
            }
            return SendAsync(HttpMethod.Post, url, content, apiKey);
        }

        public Task<DebridHttpResponse> DeleteAsync(string url, string apiKey)
        {
            return SendAsync(HttpMethod.Delete, url, null, apiKey);
        }

        private static async Task<DebridHttpResponse> ReadResponseAsync(HttpResponseMessage httpResponse)
        {
            var text = await httpResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
            JsonElement? data = null;
            if (!string.IsNullOrWhiteSpace(text))
            {
                try
                {
                    using (var doc = JsonDocument.Parse(text))
                    {
                        data = doc.RootElement.Clone();
                    }
                }
                catch (JsonException)
                {
                    // js: non-JSON body stays raw text.
                }
            }
            return new DebridHttpResponse
            {
                Ok = httpResponse.IsSuccessStatusCode,
                Status = (int)httpResponse.StatusCode,
                Text = text,
                Data = data
            };
        }
    }
}

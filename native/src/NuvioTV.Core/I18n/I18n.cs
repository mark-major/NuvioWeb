using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace NuvioTV.Core.Localization
{
    /// <summary>
    /// Port of js/i18n/index.js. Locale resolution, fallback chain, and interpolation
    /// semantics match the webapp loader exactly.
    /// </summary>
    public static class I18n
    {
        private const string DefaultLocale = "en";

        private static readonly string[] SupportedLocaleList =
        {
            "en", "ar", "bs", "cs", "de", "el", "es", "es-419", "fr", "he", "hi",
            "hu", "id", "it", "ja", "lt", "nl", "no", "pl", "pt-br", "pt-pt",
            "ro", "ru", "sk", "sl", "sv", "ta", "tr", "vi", "zh-cn"
        };

        private static readonly Dictionary<string, Dictionary<string, string>> LocaleMessagesCache =
            new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);

        private static readonly HashSet<string> WarnedKeys = new HashSet<string>(StringComparer.Ordinal);

        private static Dictionary<string, string> _activeMessages = new Dictionary<string, string>(StringComparer.Ordinal);
        private static string _currentLocale;
        private static bool _initialized;
        private static Action<string> _warn = _ => { };

        /// <summary>Injected JSON loader: locale name → flat key/string map JSON. App wires embedded resources; tests wire files.</summary>
        private static Func<string, Task<string>> _jsonLoader;

        /// <summary>System locale provider (app wires Tizen.SystemSettings; tests inject fixed values).</summary>
        private static Func<IReadOnlyList<string>> _systemLocales = () => new[] { DefaultLocale };

        public static string Locale => _initialized ? _currentLocale : ResolvePreferredLocale(null);

        public static IReadOnlyList<string> SupportedLocales => SupportedLocaleList;

        public static void Configure(Func<string, Task<string>> jsonLoader, Func<IReadOnlyList<string>> systemLocales = null, Action<string> warn = null)
        {
            _jsonLoader = jsonLoader ?? throw new ArgumentNullException(nameof(jsonLoader));
            if (systemLocales != null) _systemLocales = systemLocales;
            if (warn != null) _warn = warn;
        }

        public static async Task InitAsync(string preferredLocale)
        {
            var locale = ResolvePreferredLocale(preferredLocale);
            if (_initialized && locale == _currentLocale && _activeMessages.Count > 0)
            {
                return;
            }

            _currentLocale = locale;
            _activeMessages = await LoadLocaleMessagesAsync(locale);
            _initialized = true;
        }

        public static string ResolveLocale(string preferred = null)
        {
            return ResolvePreferredLocale(preferred);
        }

        public static string T(
            string key,
            IReadOnlyDictionary<string, object> args = null,
            string fallback = null,
            string locale = null)
        {
            var effectiveLocale = NormalizeLocale(locale ?? _currentLocale) ?? ResolvePreferredLocale(locale);

            if (_activeMessages.TryGetValue(key, out var value))
            {
                return Interpolate(value, args);
            }

            if (KeyAliases.Map.TryGetValue(key, out var aliasedKey) && _activeMessages.TryGetValue(aliasedKey, out var aliasedValue))
            {
                return Interpolate(aliasedValue, args);
            }

            WarnMissingKey(effectiveLocale, key);
            return Interpolate(fallback ?? key, args);
        }

        // ---- locale resolution (js/i18n/index.js normalizeLocale / resolvePreferredLocale) ----

        internal static string NormalizeLocale(string raw)
        {
            raw = (raw ?? "").Trim().ToLowerInvariant().Replace('_', '-');
            if (raw.Length == 0 || raw == "system")
            {
                return "";
            }
            if (raw == "iw") return "he";
            if (raw == "in") return "id";
            if (raw == "pt") return "pt-br";
            if (raw == "zh") return "zh-cn";
            if (raw == "es-419" || raw.StartsWith("es-419-", StringComparison.Ordinal)) return "es-419";
            if (raw == "zh-cn" || raw.StartsWith("zh-cn-", StringComparison.Ordinal)) return "zh-cn";
            if (raw == "pt-br" || raw.StartsWith("pt-br-", StringComparison.Ordinal)) return "pt-br";
            if (raw == "pt-pt" || raw.StartsWith("pt-pt-", StringComparison.Ordinal)) return "pt-pt";
            if (Array.IndexOf(SupportedLocaleList, raw) >= 0) return raw;

            var language = raw.Split('-')[0];
            return language.Length > 0 ? language : raw;
        }

        private static string DetectSystemLocale()
        {
            foreach (var candidate in _systemLocales())
            {
                if (!string.IsNullOrEmpty(candidate))
                {
                    return candidate;
                }
            }
            return DefaultLocale;
        }

        private static string ResolvePreferredLocale(string preferred)
        {
            var requested = preferred ?? DetectSystemLocale();
            var normalized = NormalizeLocale(requested);
            if (string.IsNullOrEmpty(normalized) || normalized == "system")
            {
                return NormalizeLocale(DetectSystemLocale()) ?? DefaultLocale;
            }
            if (Array.IndexOf(SupportedLocaleList, normalized) >= 0)
            {
                return normalized;
            }
            return DefaultLocale;
        }

        // ---- message loading (locale file merged over en base, matching loadLocaleMessages) ----

        private static async Task<Dictionary<string, string>> LoadLocaleMessagesAsync(string locale)
        {
            if (LocaleMessagesCache.TryGetValue(locale, out var cached))
            {
                return cached;
            }

            var baseMessages = await LoadSingleLocaleAsync(DefaultLocale);
            Dictionary<string, string> merged;
            if (locale == DefaultLocale)
            {
                merged = new Dictionary<string, string>(baseMessages, StringComparer.Ordinal);
            }
            else
            {
                merged = new Dictionary<string, string>(baseMessages, StringComparer.Ordinal);
                try
                {
                    var localized = await LoadSingleLocaleAsync(locale);
                    foreach (var pair in localized)
                    {
                        merged[pair.Key] = pair.Value;
                    }
                }
                catch
                {
                    // localized file missing → base only (matches webapp try/catch)
                }
            }

            LocaleMessagesCache[locale] = merged;
            return merged;
        }

        private static async Task<Dictionary<string, string>> LoadSingleLocaleAsync(string locale)
        {
            var json = await _jsonLoader(locale);
            using var doc = JsonDocument.Parse(json);
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.String)
                {
                    result[prop.Name] = prop.Value.GetString();
                }
            }
            return result;
        }

        // ---- interpolation (js/i18n/index.js interpolate, incl. \' and \" decode) ----

        internal static string Interpolate(string template, IReadOnlyDictionary<string, object> args)
        {
            var values = args != null ? args.Values.ToArray() : Array.Empty<object>();
            var sequentialIndex = 0;

            var text = template ?? "";
            text = RegexReplace(text, @"\{\{(\w+)\}\}", m =>
            {
                var key = m.Groups[1].Value;
                return args != null && args.TryGetValue(key, out var v) ? ToDisplayString(v) : "";
            });
            text = RegexReplace(text, @"%(\d+)\$[a-z]", m =>
            {
                var index = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                return index >= 1 && index <= values.Length ? ToDisplayString(values[index - 1]) : "";
            });
            text = RegexReplace(text, @"%[a-z]", _ =>
            {
                var value = sequentialIndex < values.Length ? ToDisplayString(values[sequentialIndex]) : "";
                sequentialIndex++;
                return value;
            });
            text = text.Replace("\\'", "'").Replace("\\\"", "\"");
            return text;
        }

        private static string RegexReplace(string input, string pattern, Func<System.Text.RegularExpressions.Match, string> evaluator)
        {
            return System.Text.RegularExpressions.Regex.Replace(input, pattern, m => evaluator(m));
        }

        private static string ToDisplayString(object value)
        {
            if (value == null) return "";
            if (value is IFormattable formattable) return formattable.ToString(null, CultureInfo.InvariantCulture);
            return value.ToString();
        }

        private static void WarnMissingKey(string locale, string key)
        {
            var warningKey = locale + ":" + key;
            if (WarnedKeys.Add(warningKey))
            {
                _warn($"Missing translation for \"{key}\" in locale \"{locale}\"");
            }
        }
    }
}

using System;
using System.Text;
using System.Text.RegularExpressions;

namespace NuvioTV.Core.Streams
{
    /// <summary>
    /// Release-token detection. Port of js/core/streams/releaseToken.js hasReleaseToken:
    /// token must appear as a whole word (non-alphanumeric boundaries), case-insensitive,
    /// with regex metacharacters escaped.
    /// </summary>
    public static class ReleaseToken
    {
        public static bool HasReleaseToken(string text, string token)
        {
            var escaped = Regex.Escape((token ?? "").ToLowerInvariant());
            if (escaped.Length == 0)
            {
                return false;
            }
            return Regex.IsMatch(
                text ?? "",
                "(^|[^a-z0-9])" + escaped + "([^a-z0-9]|$)",
                RegexOptions.IgnoreCase);
        }
    }

    /// <summary>
    /// Display-text cleanup. Port of js/core/streams/streamDisplayText.js:
    /// addon formatters use styled mathematical letters ("4K", "FullHD") that legacy
    /// TV fonts render as tofu — every code point in U+1D400..U+1D7FF is NFKC-normalized
    /// back to plain ASCII.
    /// </summary>
    public static class StreamDisplayText
    {
        private const int MathematicalAlphanumericStart = 0x1d400;
        private const int MathematicalAlphanumericEnd = 0x1d7ff;

        public static string NormalizeMathematicalAlphanumericSymbols(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return value ?? "";
            }

            var sb = new StringBuilder(value.Length);
            for (var i = 0; i < value.Length; )
            {
                var codePoint = char.ConvertToUtf32(value, i);
                var length = char.IsSurrogatePair(value, i) ? 2 : 1;
                if (codePoint >= MathematicalAlphanumericStart && codePoint <= MathematicalAlphanumericEnd)
                {
                    sb.Append(char.ConvertFromUtf32(codePoint).Normalize(NormalizationForm.FormKC));
                }
                else
                {
                    sb.Append(value, i, length);
                }
                i += length;
            }
            return sb.ToString();
        }
    }
}

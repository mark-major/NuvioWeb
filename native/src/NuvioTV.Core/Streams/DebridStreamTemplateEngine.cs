using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace NuvioTV.Core.Streams
{
    /// <summary>
    /// Debrid stream name/description template DSL. Verbatim port of
    /// js/core/debrid/debridStreamTemplateEngine.js: {stream.x} placeholders,
    /// [condition ? "then" : "else"] branches, and :: chained transforms
    /// (title/lower/upper/bytes/time/join(sep)/replace(a,b)) with condition
    /// operators exists/istrue/isfalse/=~/~/=/&gt;=/&lt;=/&gt;/&lt; combined by
    /// and/or. Values are plain objects: string, bool, double, long,
    /// IDictionary&lt;string,object&gt; or IList&lt;object&gt;.
    /// </summary>
    public static class DebridStreamTemplateEngine
    {
        public static string Render(string template, IDictionary<string, object> values)
        {
            if (string.IsNullOrEmpty(template))
            {
                return "";
            }
            var map = values ?? new Dictionary<string, object>();
            var output = "";
            var index = 0;
            while (index < template.Length)
            {
                var start = template.IndexOf('{', index);
                if (start < 0)
                {
                    output += template.Substring(index);
                    break;
                }
                output += template.Substring(index, start - index);
                var end = FindPlaceholderEnd(template, start + 1);
                if (end < 0)
                {
                    output += template.Substring(start);
                    break;
                }
                output += RenderExpression(template.Substring(start + 1, end - start - 1), map);
                index = end + 1;
            }
            return output;
        }

        // ------------------------------------------------------------------
        // Scanner helpers
        // ------------------------------------------------------------------

        private static int FindPlaceholderEnd(string text, int start)
        {
            char? quote = null;
            for (var i = start; i < text.Length; i++)
            {
                var ch = text[i];
                if (quote.HasValue)
                {
                    if (ch == quote.Value && (i == 0 || text[i - 1] != '\\'))
                    {
                        quote = null;
                    }
                }
                else if (ch == '\'' || ch == '"')
                {
                    quote = ch;
                }
                else if (ch == '}')
                {
                    return i;
                }
            }
            return -1;
        }

        private static int FindTopLevelChar(string text, char target)
        {
            char? quote = null;
            var parenDepth = 0;
            for (var i = 0; i < text.Length; i++)
            {
                var ch = text[i];
                if (quote.HasValue)
                {
                    if (ch == quote.Value && (i == 0 || text[i - 1] != '\\'))
                    {
                        quote = null;
                    }
                    continue;
                }
                if (ch == '\'' || ch == '"')
                {
                    quote = ch;
                }
                else if (ch == '(')
                {
                    parenDepth++;
                }
                else if (ch == ')')
                {
                    parenDepth = Math.Max(0, parenDepth - 1);
                }
                else if (ch == target && parenDepth == 0)
                {
                    return i;
                }
            }
            return -1;
        }

        private static List<string> SplitOps(string text)
        {
            var tokens = new List<string>();
            char? quote = null;
            var parenDepth = 0;
            var start = 0;
            var index = 0;
            while (index < text.Length)
            {
                var ch = text[index];
                if (quote.HasValue)
                {
                    if (ch == quote.Value && text[index - 1] != '\\')
                    {
                        quote = null;
                    }
                    index++;
                    continue;
                }
                if (ch == '\'' || ch == '"')
                {
                    quote = ch;
                }
                else if (ch == '(')
                {
                    parenDepth++;
                }
                else if (ch == ')')
                {
                    parenDepth = Math.Max(0, parenDepth - 1);
                }
                else if (ch == ':' && index + 1 < text.Length && text[index + 1] == ':' && parenDepth == 0)
                {
                    tokens.Add(text.Substring(start, index - start).Trim());
                    index += 2;
                    start = index;
                    continue;
                }
                index++;
            }
            tokens.Add(text.Substring(start).Trim());
            return tokens.Where(t => t.Length > 0).ToList();
        }

        private static int FindBranchSeparator(string text)
        {
            char? quote = null;
            for (var i = 0; i < text.Length; i++)
            {
                var ch = text[i];
                if (quote.HasValue)
                {
                    if (ch == quote.Value && text[i - 1] != '\\')
                    {
                        quote = null;
                    }
                    continue;
                }
                if (ch == '\'' || ch == '"')
                {
                    quote = ch;
                }
                else if (ch == '|' && i + 1 < text.Length && text[i + 1] == '|')
                {
                    return i;
                }
            }
            return -1;
        }

        private static string ParseQuoted(string raw)
        {
            var trimmed = (raw ?? "").Trim();
            var isQuoted = trimmed.Length >= 2 &&
                           ((trimmed[0] == '"' && trimmed[trimmed.Length - 1] == '"') ||
                            (trimmed[0] == '\'' && trimmed[trimmed.Length - 1] == '\''));
            var unquoted = isQuoted ? trimmed.Substring(1, trimmed.Length - 2) : trimmed;
            return unquoted
                .Replace("\\n", "\n")
                .Replace("\\\"", "\"")
                .Replace("\\'", "'")
                .Replace("\\\\", "\\");
        }

        private static string[] ParseBranches(string text)
        {
            var split = FindBranchSeparator(text);
            if (split < 0)
            {
                return new[] { ParseQuoted(text), "" };
            }
            return new[] { ParseQuoted(text.Substring(0, split)), ParseQuoted(text.Substring(split + 2)) };
        }

        private static string[] ParseArgs(string op)
        {
            var start = op.IndexOf('(');
            var end = op.LastIndexOf(')');
            if (start < 0 || end <= start)
            {
                return Array.Empty<string>();
            }
            var body = op.Substring(start + 1, end - start - 1);
            var args = new List<string>();
            char? quote = null;
            var argStart = 0;
            for (var i = 0; i < body.Length; i++)
            {
                var ch = body[i];
                if (quote.HasValue)
                {
                    if (ch == quote.Value && body[i - 1] != '\\')
                    {
                        quote = null;
                    }
                    continue;
                }
                if (ch == '\'' || ch == '"')
                {
                    quote = ch;
                }
                else if (ch == ',')
                {
                    args.Add(ParseQuoted(body.Substring(argStart, i - argStart)));
                    argStart = i + 1;
                }
            }
            args.Add(ParseQuoted(body.Substring(argStart)));
            return args.ToArray();
        }

        // ------------------------------------------------------------------
        // Value semantics
        // ------------------------------------------------------------------

        private static bool IsFieldPath(string value)
        {
            return value.StartsWith("stream.") || value.StartsWith("service.") || value.StartsWith("addon.");
        }

        // netstandard2.0 has no double.IsInteger (C# 8 target).
        private static bool IsWholeNumber(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value) && value == Math.Truncate(value);
        }

        private static bool Exists(object value)
        {
            if (value == null)
            {
                return false;
            }
            if (value is string s)
            {
                return s.Trim().Length > 0;
            }
            if (value is System.Collections.ICollection collection)
            {
                return collection.Count > 0;
            }
            return true;
        }

        private static bool IsTruthy(object value)
        {
            if (value is bool b)
            {
                return b;
            }
            if (value is double d)
            {
                return d != 0;
            }
            if (value is long l)
            {
                return l != 0;
            }
            return Exists(value);
        }

        private static bool? AsBoolean(object value)
        {
            if (value is bool b)
            {
                return b;
            }
            if (value is string s)
            {
                if (s == "true")
                {
                    return true;
                }
                if (s == "false")
                {
                    return false;
                }
            }
            return null;
        }

        private static double? AsNumber(object value)
        {
            if (value is double d && !double.IsNaN(d) && !double.IsInfinity(d))
            {
                return d;
            }
            if (value is long l)
            {
                return l;
            }
            if (value is string s && s.Trim().Length > 0)
            {
                if (double.TryParse(s.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
                {
                    return parsed;
                }
            }
            return null;
        }

        private static string ValueToText(object value)
        {
            if (value == null)
            {
                return "";
            }
            if (value is System.Collections.IEnumerable enumerable && !(value is string))
            {
                return string.Join(", ", enumerable.Cast<object>()
                    .Select(ValueToText)
                    .Where(t => t.Trim().Length > 0));
            }
            if (value is double d)
            {
                return IsWholeNumber(d) ? Math.Truncate(d).ToString(CultureInfo.InvariantCulture) : d.ToString(CultureInfo.InvariantCulture);
            }
            if (value is long l)
            {
                return l.ToString(CultureInfo.InvariantCulture);
            }
            if (value is bool b)
            {
                return b ? "true" : "false";
            }
            return value.ToString();
        }

        private static bool CompareNumber(object value, string rawTarget, Func<double, double, bool> compare)
        {
            var left = AsNumber(value);
            if (!left.HasValue ||
                !double.TryParse((rawTarget ?? "").Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var right))
            {
                return false;
            }
            return compare(left.Value, right);
        }

        private static bool EqualsText(object value, string target)
        {
            var normalized = (target ?? "").Trim().ToLowerInvariant();
            if (value is System.Collections.IEnumerable enumerable && !(value is string))
            {
                return enumerable.Cast<object>().Any(entry => ValueToText(entry).Trim().ToLowerInvariant() == normalized);
            }
            return ValueToText(value).Trim().ToLowerInvariant() == normalized;
        }

        private static bool ContainsText(object value, string target)
        {
            var normalized = (target ?? "").Trim().ToLowerInvariant();
            if (value is System.Collections.IEnumerable enumerable && !(value is string))
            {
                return enumerable.Cast<object>().Any(entry => ValueToText(entry).ToLowerInvariant().Contains(normalized));
            }
            return ValueToText(value).ToLowerInvariant().Contains(normalized);
        }

        private static string TitleCased(object value)
        {
            var words = ValueToText(value).Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            return string.Join(" ", words.Select(word =>
            {
                var lowered = word.ToLowerInvariant();
                return char.ToUpperInvariant(lowered[0]) + lowered.Substring(1);
            }));
        }

        private static string FormatBytes(object value)
        {
            var source = AsNumber(value) ?? 0;
            var bytes = Math.Abs(source);
            if (bytes < 1024)
            {
                return Math.Truncate(source).ToString(CultureInfo.InvariantCulture) + " B";
            }
            string[] units = { "KB", "MB", "GB", "TB" };
            var current = bytes;
            var unitIndex = -1;
            while (current >= 1024 && unitIndex < units.Length - 1)
            {
                current /= 1024;
                unitIndex++;
            }
            var signed = source < 0 ? -current : current;
            return IsWholeNumber(signed)
                ? Math.Truncate(signed).ToString(CultureInfo.InvariantCulture) + " " + units[unitIndex]
                : signed.ToString("0.0", CultureInfo.InvariantCulture) + " " + units[unitIndex];
        }

        private static string FormatTime(object value)
        {
            var seconds = (long)Math.Truncate(AsNumber(value) ?? 0);
            var hours = seconds / 3600;
            var minutes = (seconds % 3600) / 60;
            var remainingSeconds = seconds % 60;
            if (hours > 0)
            {
                return $"{hours}h {minutes}m";
            }
            if (minutes > 0)
            {
                return $"{minutes}m {remainingSeconds}s";
            }
            return $"{remainingSeconds}s";
        }

        // ------------------------------------------------------------------
        // Transforms and conditions
        // ------------------------------------------------------------------

        private static object ApplyTransform(object value, string op)
        {
            if (op == "title")
            {
                return TitleCased(value);
            }
            if (op == "lower")
            {
                return ValueToText(value).ToLowerInvariant();
            }
            if (op == "upper")
            {
                return ValueToText(value).ToUpperInvariant();
            }
            if (op == "bytes")
            {
                return AsNumber(value) == null ? "" : FormatBytes(value);
            }
            if (op == "time")
            {
                return AsNumber(value) == null ? "" : FormatTime(value);
            }
            if (op.StartsWith("join("))
            {
                var separator = ParseArgs(op)[0] ?? ", ";
                if (value is System.Collections.IEnumerable enumerable && !(value is string))
                {
                    return string.Join(separator, enumerable.Cast<object>()
                        .Select(ValueToText)
                        .Where(t => t.Trim().Length > 0));
                }
                return ValueToText(value);
            }
            if (op.StartsWith("replace("))
            {
                var args = ParseArgs(op);
                if (args.Length < 2)
                {
                    return ValueToText(value);
                }
                var text = ValueToText(value);
                // JS parity: "".split("") explodes into single characters.
                if (args[0].Length == 0)
                {
                    return string.Join(args[1], text.Select(ch => ch.ToString()));
                }
                return text.Replace(args[0], args[1]);
            }
            return value;
        }

        private static bool EvaluateSingleCondition(object value, IReadOnlyList<string> ops)
        {
            if (ops.Count == 0)
            {
                return IsTruthy(value);
            }
            var result = false;
            var hasResult = false;
            foreach (var op in ops)
            {
                if (op == "exists")
                {
                    result = Exists(value);
                    hasResult = true;
                }
                else if (op == "istrue")
                {
                    result = hasResult ? result : AsBoolean(value) == true;
                    hasResult = true;
                }
                else if (op == "isfalse")
                {
                    result = hasResult ? !result : AsBoolean(value) == false;
                    hasResult = true;
                }
                else if (op.StartsWith("~="))
                {
                    result = ContainsText(value, op.Substring(2).Trim());
                    hasResult = true;
                }
                else if (op.StartsWith("~"))
                {
                    result = ContainsText(value, op.Substring(1).Trim());
                    hasResult = true;
                }
                else if (op.StartsWith("="))
                {
                    result = EqualsText(value, op.Substring(1).Trim());
                    hasResult = true;
                }
                else if (op.StartsWith(">="))
                {
                    result = CompareNumber(value, op.Substring(2), (l, r) => l >= r);
                    hasResult = true;
                }
                else if (op.StartsWith("<="))
                {
                    result = CompareNumber(value, op.Substring(2), (l, r) => l <= r);
                    hasResult = true;
                }
                else if (op.StartsWith(">"))
                {
                    result = CompareNumber(value, op.Substring(1), (l, r) => l > r);
                    hasResult = true;
                }
                else if (op.StartsWith("<"))
                {
                    result = CompareNumber(value, op.Substring(1), (l, r) => l < r);
                    hasResult = true;
                }
            }
            return result;
        }

        private static bool EvaluateCondition(string expression, IDictionary<string, object> values)
        {
            var tokens = SplitOps(expression).Where(Boolean => Boolean.Length > 0).ToList();
            if (tokens.Count == 0)
            {
                return false;
            }
            var groups = new List<List<bool>>();
            var currentGroup = new List<bool>();
            var index = 0;
            while (index < tokens.Count)
            {
                if (tokens[index] == "or")
                {
                    groups.Add(currentGroup);
                    currentGroup = new List<bool>();
                    index++;
                }
                else if (tokens[index] == "and")
                {
                    index++;
                }
                else
                {
                    var field = tokens[index];
                    index++;
                    var ops = new List<string>();
                    while (index < tokens.Count &&
                           tokens[index] != "and" &&
                           tokens[index] != "or" &&
                           !IsFieldPath(tokens[index]))
                    {
                        ops.Add(tokens[index]);
                        index++;
                    }
                    values.TryGetValue(field, out var fieldValue);
                    currentGroup.Add(EvaluateSingleCondition(fieldValue, ops));
                }
            }
            groups.Add(currentGroup);
            return groups.Any(group => group.Count > 0 && group.All(Boolean2 => Boolean2));
        }

        private static string RenderExpression(string expression, IDictionary<string, object> values)
        {
            var bracket = FindTopLevelChar(expression, '[');
            if (bracket >= 0 && expression.EndsWith("]"))
            {
                var condition = expression.Substring(0, bracket);
                var branches = ParseBranches(expression.Substring(bracket + 1, expression.Length - bracket - 2));
                return Render(EvaluateCondition(condition, values) ? branches[0] : branches[1], values);
            }
            var tokens = SplitOps(expression);
            if (tokens.Count == 0)
            {
                return "";
            }
            values.TryGetValue(tokens[0], out var value);
            foreach (var op in tokens.Skip(1))
            {
                value = ApplyTransform(value, op);
            }
            return ValueToText(value);
        }
    }
}

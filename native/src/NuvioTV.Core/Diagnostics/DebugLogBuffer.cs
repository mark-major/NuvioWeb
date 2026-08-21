using System;
using System.Collections.Generic;
using System.Text.Json;
using NuvioTV.Core.Storage;

namespace NuvioTV.Core.Diagnostics
{
    /// <summary>
    /// Ring-buffer port of js/core/diagnostics/consoleDebugBuffer.js: capped at
    /// 300 events, message truncation at 16k, argument truncation at 6k.
    /// </summary>
    public sealed class DebugLogBuffer
    {
        public const int MaxEvents = 300;
        public const int MaxArgumentLength = 6000;
        public const int MaxMessageLength = 16000;

        private readonly object _gate = new object();
        private readonly List<string> _lines = new List<string>(MaxEvents);
        private long _nextEventId = 1;

        /// <summary>Appends one formatted event; returns the assigned id.</summary>
        public long Append(string level, string message)
        {
            var text = Truncate(message ?? "", MaxMessageLength);
            lock (_gate)
            {
                var id = _nextEventId++;
                _lines.Add($"#{id} [{level}] {text}");
                if (_lines.Count > MaxEvents)
                {
                    _lines.RemoveAt(0);
                }
                return id;
            }
        }

        public long Info(string message) => Append("INFO", message);
        public long Warn(string message) => Append("WARN", message);
        public long Error(string message) => Append("ERROR", message);

        /// <summary>Snapshot of buffered lines oldest-first.</summary>
        public List<string> Snapshot()
        {
            lock (_gate)
            {
                return new List<string>(_lines);
            }
        }

        public int Count
        {
            get
            {
                lock (_gate)
                {
                    return _lines.Count;
                }
            }
        }

        public void Clear()
        {
            lock (_gate)
            {
                _lines.Clear();
            }
        }

        internal static string Truncate(string text, int maxLength)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
            {
                return text ?? "";
            }
            return text.Substring(0, Math.Max(0, maxLength - 1)) + "…";
        }
    }
}

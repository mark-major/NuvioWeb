using System;

namespace NuvioTV.Core.Player
{
    /// <summary>
    /// Port of next-episode rules from playerScreen.js: percentage or
    /// minutes-before-end trigger modes with outro-segment support.
    /// </summary>
    public sealed class NextEpisodeRules
    {
        public enum TriggerMode
        {
            /// <summary>Fires when remaining time ≤ ThresholdPercent of duration.</summary>
            Percentage,
            /// <summary>Fires when remaining time ≤ MinutesBeforeEnd minutes.</summary>
            MinutesBeforeEnd
        }

        public TriggerMode Mode { get; set; } = TriggerMode.Percentage;
        /// <summary>Percentage 1..99 when Mode == Percentage.</summary>
        public int ThresholdPercent { get; set; } = 90;
        /// <summary>Minutes before end when Mode == MinutesBeforeEnd.</summary>
        public int MinutesBeforeEnd { get; set; } = 2;

        /// <summary>Outro start offset ms (animeSkip/outro segment); null if unknown.</summary>
        public long? OutroStartMs { get; set; }

        /// <summary>True when the next-episode overlay should be visible.</summary>
        public bool ShouldShowOverlay(long positionMs, long durationMs)
        {
            if (durationMs <= 0 || positionMs <= 0) return false;
            if (positionMs >= durationMs) return true;
            if (OutroStartMs.HasValue && positionMs >= OutroStartMs.Value) return true;

            switch (Mode)
            {
                case TriggerMode.MinutesBeforeEnd:
                    return durationMs - positionMs <= (long)MinutesBeforeEnd * 60000;
                default:
                {
                    var percent = (double)positionMs / durationMs * 100.0;
                    var threshold = Math.Max(1, Math.Min(99, ThresholdPercent));
                    return percent >= threshold;
                }
            }
        }

        /// <summary>Skip-intro visibility: inside [introStart, introEnd).</summary>
        public static bool ShouldShowSkipIntro(
            long? introStartMs, long? introEndMs, long positionMs) =>
            introStartMs.HasValue && introEndMs.HasValue &&
            positionMs >= introStartMs.Value && positionMs < introEndMs.Value;
    }

    /// <summary>
    /// Port of WatchProgressRecorder thresholds: save ticks every 30s while
    /// playing (5s near the end), completion at ≥90%, resume eligibility ≥2%.
    /// </summary>
    public sealed class ProgressRecorderRules
    {
        public const int SaveIntervalPlayingMs = 30000;
        public const int SaveIntervalNearEndMs = 5000;
        public const double StartedRatio = 0.02;
        public const double CompletedRatio = 0.90;

        private long _lastSavedAtMs;
        private bool _initialized;

        /// <summary>True when a save tick is due.</summary>
        public bool ShouldSaveNow(long positionMs, long durationMs, long nowMs)
        {
            if (!_initialized)
            {
                _initialized = true;
                _lastSavedAtMs = nowMs;
                return true;
            }
            var interval = IsNearEnd(positionMs, durationMs) ? SaveIntervalNearEndMs : SaveIntervalPlayingMs;
            if (nowMs - _lastSavedAtMs >= interval)
            {
                _lastSavedAtMs = nowMs;
                return true;
            }
            return false;
        }

        private static bool IsNearEnd(long positionMs, long durationMs) =>
            durationMs > 0 && durationMs - positionMs <= 120000; // last 2 min

        /// <summary>Resume eligible: ratio within [started, completed).</summary>
        public static bool IsResumable(long positionMs, long durationMs)
        {
            if (durationMs <= 0) return false;
            var ratio = (double)positionMs / durationMs;
            return ratio >= StartedRatio && ratio < CompletedRatio;
        }

        /// <summary>Mark watched: ratio ≥ completed threshold.</summary>
        public static bool IsWatched(long positionMs, long durationMs) =>
            durationMs > 0 && (double)positionMs / durationMs >= CompletedRatio;

        public void Reset()
        {
            _lastSavedAtMs = 0;
            _initialized = false;
        }
    }
}

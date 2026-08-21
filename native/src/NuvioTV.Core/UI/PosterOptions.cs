using System;
using System.Collections.Generic;

namespace NuvioTV.Core.UI
{
    /// <summary>
    /// Pure port of js/ui/components/posterOptionsMenu.js state machine:
    /// createPosterOptionsState/getPosterOptions/activatePosterOption decision
    /// logic. Data effects are injected as delegates so the machine stays
    /// host-testable.
    /// </summary>
    public static class PosterOptions
    {
        public sealed class PosterItem
        {
            public string Id;
            public string Type;
            public string Title;
            public string Poster;
            public string Background;
        }

        public sealed class OptionsState
        {
            public PosterItem Item;
            /// <summary>"LOCAL" | "TRAKT" | "SIMKL" (LibrarySourceMode parity).</summary>
            public string SourceMode;
            public bool IsSaved;
            public bool IsWatched;
            public int OptionIndex;
            public string FocusKey;
            public int ItemIndex = -1;
        }

        public sealed class Option
        {
            public string Action;
            public string Label;
        }

        // ---- getPosterOptions ----

        public static List<Option> Get(OptionsState state)
        {
            var result = new List<Option>();
            if (state?.Item == null || string.IsNullOrEmpty(state.Item.Id))
            {
                return result;
            }

            result.Add(new Option { Action = "details", Label = "Go to details" });

            var includeLibrary = true;
            if (includeLibrary)
            {
                string label;
                if (state.SourceMode == "TRAKT")
                {
                    label = "Manage Lists";
                }
                else if (state.SourceMode == "SIMKL")
                {
                    label = "Manage Simkl Status";
                }
                else
                {
                    label = state.IsSaved ? "Remove from Library" : "Add to Library";
                }
                result.Add(new Option { Action = "toggleLibrary", Label = label });
            }

            if (IsMovieType(state.Item.Type) || IsSeriesType(state.Item.Type))
            {
                result.Add(new Option
                {
                    Action = "toggleWatched",
                    Label = state.IsWatched ? "Mark as unwatched" : "Mark as watched"
                });
            }
            return result;
        }

        /// <summary>Vertical wrap-around within the option list.</summary>
        public static int Move(int index, int count, int delta)
        {
            if (count <= 0) return 0;
            return ((index + delta) % count + count) % count;
        }

        // ---- activatePosterOption decision layer ----

        public sealed class Effects
        {
            public Func<string, bool> ToggleSaved = _ => false;
            public Action<string> MarkWatched = _ => { };
            public Action<string> UnmarkWatched = _ => { };
            /// <summary>Series-level watched toggle; returns new watched state.</summary>
            public Func<PosterItem, bool, bool> ToggleSeriesWatched =
                (item, watched) => !watched;
            /// <summary>Non-local source modes open a list picker instead of toggling.</summary>
            public bool NeedsListPicker(string sourceMode) => sourceMode != "LOCAL";
        }

        public sealed class ActivationResult
        {
            public string Type;          // noop | details | updated | listPicker
            public OptionsState State;
        }

        public static ActivationResult Activate(OptionsState state, string action, Effects fx = null)
        {
            fx = fx ?? new Effects();
            if (state?.Item == null || string.IsNullOrEmpty(state.Item.Id) || action == null)
            {
                return new ActivationResult { Type = "noop" };
            }

            switch (action)
            {
                case "details":
                    return new ActivationResult { Type = "details" };

                case "toggleLibrary":
                    if (fx.NeedsListPicker(state.SourceMode))
                    {
                        return new ActivationResult { Type = "listPicker" };
                    }
                    state.IsSaved = fx.ToggleSaved(state.Item.Id);
                    return new ActivationResult { Type = "updated", State = state };

                case "toggleWatched":
                    if (IsSeriesType(state.Item.Type))
                    {
                        state.IsWatched = fx.ToggleSeriesWatched(state.Item, state.IsWatched);
                        return new ActivationResult { Type = "updated", State = state };
                    }
                    if (state.IsWatched)
                    {
                        fx.UnmarkWatched(state.Item.Id);
                        state.IsWatched = false;
                    }
                    else
                    {
                        fx.MarkWatched(state.Item.Id);
                        state.IsWatched = true;
                    }
                    return new ActivationResult { Type = "updated", State = state };

                default:
                    return new ActivationResult { Type = "noop" };
            }
        }

        private static bool IsSeriesType(string type)
        {
            var normalized = (type ?? "").ToLowerInvariant();
            return normalized == "series" || normalized == "tv" || normalized == "anime";
        }

        private static bool IsMovieType(string type) =>
            (type ?? "").ToLowerInvariant() == "movie";
    }
}

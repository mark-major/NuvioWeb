using System;
using System.Collections.Generic;
using NuvioTV.Core.UI;
using Xunit;

namespace NuvioTV.Core.Tests
{
    /// <summary>Ports the behavioral contract of posterOptionsMenu.js.</summary>
    public class PosterOptionsTests
    {
        private static PosterOptions.OptionsState State(string type = "movie", string mode = "LOCAL",
            bool saved = false, bool watched = false) =>
            new PosterOptions.OptionsState
            {
                Item = new PosterOptions.PosterItem { Id = "tt123", Type = type, Title = "Batman" },
                SourceMode = mode,
                IsSaved = saved,
                IsWatched = watched
            };

        [Fact]
        public void Get_MovieLocal_ShowsThreeActions()
        {
            var options = PosterOptions.Get(State());
            Assert.Equal(new[] { "details", "toggleLibrary", "toggleWatched" },
                options.ConvertAll(o => o.Action));
            Assert.Equal("Add to Library", options[1].Label);
            Assert.Equal("Mark as watched", options[2].Label);
        }

        [Fact]
        public void Get_SavedMovie_LabelFlipsToRemove()
        {
            var options = PosterOptions.Get(State(saved: true));
            Assert.Equal("Remove from Library", options[1].Label);
            Assert.Equal("Mark as watched", options[2].Label); // not yet watched
        }

        [Fact]
        public void Get_TraktSource_ManageListsLabel()
        {
            var options = PosterOptions.Get(State(mode: "TRAKT"));
            Assert.Equal("Manage Lists", options[1].Label);
            Assert.Equal("Mark as watched", options[2].Label); // state not yet watched
        }

        [Fact]
        public void Get_ChannelType_OmitsWatchedAction()
        {
            var options = PosterOptions.Get(State(type: "channel"));
            Assert.Equal(new[] { "details", "toggleLibrary" },
                options.ConvertAll(o => o.Action));
        }

        [Fact]
        public void Move_WrapsBothDirections()
        {
            Assert.Equal(2, PosterOptions.Move(0, 3, -1));
            Assert.Equal(0, PosterOptions.Move(2, 3, 1));
            Assert.Equal(0, PosterOptions.Move(0, 0, 1)); // empty list safe
        }

        [Fact]
        public void Activate_ToggleLibrary_LocalTogglesSaved()
        {
            var state = State();
            var fx = new PosterOptions.Effects
            {
                ToggleSaved = id => id == "tt123"
            };
            var result = PosterOptions.Activate(state, "toggleLibrary", fx);
            Assert.Equal("updated", result.Type);
            Assert.True(result.State.IsSaved);
        }

        [Fact]
        public void Activate_ToggleLibrary_NonLocalOpensListPicker()
        {
            var result = PosterOptions.Activate(State(mode: "TRAKT"), "toggleLibrary");
            Assert.Equal("listPicker", result.Type);
        }

        [Fact]
        public void Activate_ToggleWatched_MovieMarksAndUnmarks()
        {
            var marked = new List<string>();
            var state = State();
            var fx = new PosterOptions.Effects
            {
                MarkWatched = id => marked.Add(id),
                UnmarkWatched = id => marked.Remove(id)
            };
            var afterMark = PosterOptions.Activate(state, "toggleWatched", fx);
            Assert.True(afterMark.State.IsWatched);
            var afterUnmark = PosterOptions.Activate(afterMark.State, "toggleWatched", fx);
            Assert.False(afterUnmark.State.IsWatched);
            Assert.Empty(marked);
        }

        [Fact]
        public void Activate_ToggleWatched_SeriesUsesReconciliation()
        {
            var state = State(type: "series", watched: true);
            var fx = new PosterOptions.Effects
            {
                ToggleSeriesWatched = (item, watched) => !watched
            };
            var result = PosterOptions.Activate(state, "toggleWatched", fx);
            Assert.False(result.State.IsWatched);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void Activate_BadInput_Noop(string action)
        {
            Assert.Equal("noop", PosterOptions.Activate(State(), action).Type);
        }
    }
}

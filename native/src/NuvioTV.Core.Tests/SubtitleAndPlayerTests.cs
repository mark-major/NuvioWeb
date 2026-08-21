using System;
using System.Collections.Generic;
using System.Linq;
using NuvioTV.Core.Media;
using NuvioTV.Core.Player;
using Xunit;

namespace NuvioTV.Core.Tests
{
    /// <summary>Host tests for the subtitle pipeline and player rules (Task 15.x).</summary>
    public class SubtitleAndPlayerRulesTests
    {
        private const string SrtSample =
            "1\n00:00:01,000 --> 00:00:03,500\nHello world\n\n" +
            "2\n00:00:04,000 --> 00:00:06,000\nSecond line one\nSecond line two";

        [Fact]
        public void SrtParser_ParsesTimingAndMultiline()
        {
            var cues = SrtParser.Parse(SrtSample);
            Assert.Equal(2, cues.Count);
            Assert.Equal(1000, cues[0].StartMs);
            Assert.Equal(3500, cues[0].EndMs);
            Assert.Equal("Hello world", cues[0].Text);
            Assert.Contains("Second line two", cues[1].Text);
        }

        [Fact]
        public void VttParser_SkipsHeaderAndStripsMarkup()
        {
            var vtt = "WEBVTT\n\n00:01.000 --> 00:03.000\n<b>Bold</b> text";
            var cues = VttParser.Parse(vtt);
            Assert.Single(cues);
            Assert.Equal(1000, cues[0].StartMs);
            Assert.Equal("Bold text", cues[0].Text);
        }

        [Fact]
        public void AssParser_KeepsAlignmentAndStripsTags()
        {
            var ass = "[Events]\nDialogue: 0,0:00:05.00,0:00:07.50,Default,,0,0,0,,{\\an8}Top center\\Nline two";
            var cues = AssParser.Parse(ass);
            Assert.Single(cues);
            Assert.Equal(8, cues[0].Alignment);
            Assert.Equal(5000, cues[0].StartMs);
            Assert.Equal(7500, cues[0].EndMs);
            Assert.Contains("Top center", cues[0].Text);
            Assert.DoesNotContain("{", cues[0].Text);
        }

        [Fact]
        public void CueLayout_MapsAssAlignments()
        {
            Assert.Equal(("top", "center"), ("top", "center") == (CueLayout.FromAlignment(8).Vertical, CueLayout.FromAlignment(8).Align) ? ("top", "center") : ("x", "x"));
            Assert.Equal("bottom", CueLayout.FromAlignment(2).Vertical);
            Assert.Equal("bottom", CueLayout.FromAlignment(0).Vertical); // default
            Assert.Equal("left", CueLayout.FromAlignment(7).Align);
        }

        [Fact]
        public void CueLayout_ActiveAt_FiltersWindow()
        {
            var cue = new SubtitleCue { StartMs = 1000, EndMs = 2000 };
            Assert.Empty(CueLayout.ActiveAt(new[] { cue }, 999));
            Assert.Single(CueLayout.ActiveAt(new[] { cue }, 1500));
            Assert.Empty(CueLayout.ActiveAt(new[] { cue }, 2000));
        }

        [Fact]
        public void VerticalOffset_ClampsToSteps()
        {
            Assert.Equal(-20, CueLayout.ClampVerticalOffset(-100));
            Assert.Equal(50, CueLayout.ClampVerticalOffset(100));
            Assert.Equal(15, CueLayout.ClampVerticalOffset(17));
            Assert.Equal(20, CueLayout.ClampVerticalOffset(18));
        }

        // ---- NextEpisodeRules ----

        [Fact]
        public void NextEpisode_PercentageMode_TriggersAtThreshold()
        {
            var rules = new NextEpisodeRules { Mode = NextEpisodeRules.TriggerMode.Percentage, ThresholdPercent = 90 };
            Assert.False(rules.ShouldShowOverlay(1000, 100000));   // 1%
            Assert.True(rules.ShouldShowOverlay(91000, 100000));   // 91%
        }

        [Fact]
        public void NextEpisode_MinutesMode_TriggersBeforeEnd()
        {
            var rules = new NextEpisodeRules { Mode = NextEpisodeRules.TriggerMode.MinutesBeforeEnd, MinutesBeforeEnd = 2 };
            Assert.False(rules.ShouldShowOverlay(60000, 300000));  // 4 min left
            Assert.True(rules.ShouldShowOverlay(110000, 120000));  // 10s left
        }

        [Fact]
        public void NextEpisode_OutroSegment_Overrides()
        {
            var rules = new NextEpisodeRules { OutroStartMs = 80000 };
            Assert.True(rules.ShouldShowOverlay(85000, 100000));
            Assert.False(rules.ShouldShowOverlay(70000, 100000));
        }

        [Fact]
        public void SkipIntro_VisibleOnlyInsideSegment()
        {
            Assert.True(NextEpisodeRules.ShouldShowSkipIntro(10000, 90000, 50000));
            Assert.False(NextEpisodeRules.ShouldShowSkipIntro(10000, 90000, 5000));
            Assert.False(NextEpisodeRules.ShouldShowSkipIntro(null, null, 50000));
        }

        // ---- ProgressRecorderRules ----

        [Fact]
        public void ProgressRecorder_ResumableAndWatchedThresholds()
        {
            Assert.True(ProgressRecorderRules.IsResumable(5000, 100000));
            Assert.False(ProgressRecorderRules.IsResumable(100, 100000));
            Assert.False(ProgressRecorderRules.IsResumable(99000, 100000)); // watched now

            Assert.True(ProgressRecorderRules.IsWatched(95000, 100000));
            Assert.False(ProgressRecorderRules.IsWatched(50000, 100000));
        }

        [Fact]
        public void ProgressRecorder_SaveTicksSlowNearEnd()
        {
            var rules = new ProgressRecorderRules();
            Assert.True(rules.ShouldSaveNow(0, 600000, 0));
            Assert.False(rules.ShouldSaveNow(1000, 600000, 10000));    // 10s < 30s interval
            Assert.True(rules.ShouldSaveNow(590000, 600000, 40000));   // near end: 5s ticks ok
        }
    }
}

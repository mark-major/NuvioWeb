using System;
using System.Collections.Generic;
using NuvioTV.Core.Input;

namespace NuvioTV.Tizen.Input
{
    /// <summary>
    /// Port of js/platform/sharedKeys.js + the UIScreenMap key table: maps raw
    /// Tizen key events to normalized NuvioKey values (Appendix A).
    /// </summary>
    public static class KeyMap
    {
        // Back keycodes: 8 (Backspace) / 27 (Escape) / 461 / 10009 (Tizen back).
        private static readonly HashSet<int> BackCodes = new HashSet<int> { 8, 27, 461, 10009 };

        private static readonly Dictionary<int, NuvioKey> CodeMap = new Dictionary<int, NuvioKey>
        {
            { 13, NuvioKey.Ok },      // Enter / OK
            { 23, NuvioKey.Ok },      // KP_Enter style select
            { 37, NuvioKey.Left },
            { 38, NuvioKey.Up },
            { 39, NuvioKey.Right },
            { 40, NuvioKey.Down },
            // Media matrix.
            { 179, NuvioKey.PlayPause },
            { 10252, NuvioKey.PlayPause },
            { 415, NuvioKey.Play },
            { 19, NuvioKey.Pause },
            { 413, NuvioKey.Stop },
            { 178, NuvioKey.Stop },
            { 417, NuvioKey.Ff },
            { 412, NuvioKey.Rw },
            { 176, NuvioKey.Next },
            { 177, NuvioKey.Prev },
            // Colored / channel keys stay unbound (parity), so they are absent here.
        };

        private static readonly Dictionary<string, NuvioKey> NameMap =
            new Dictionary<string, NuvioKey>(StringComparer.OrdinalIgnoreCase)
            {
                ["Return"] = NuvioKey.Ok,
                ["Enter"] = NuvioKey.Ok,
                ["Select"] = NuvioKey.Ok,
                ["DpadCenter"] = NuvioKey.Ok,
                ["XF86Back"] = NuvioKey.Back,
                ["GoBack"] = NuvioKey.Back,
                ["MediaPlayPause"] = NuvioKey.PlayPause,
                ["MediaPlay"] = NuvioKey.Play,
                ["MediaPause"] = NuvioKey.Pause,
                ["MediaStop"] = NuvioKey.Stop,
                ["MediaFastForward"] = NuvioKey.Ff,
                ["MediaRewind"] = NuvioKey.Rw,
                ["MediaTrackNext"] = NuvioKey.Next,
                ["MediaTrackPrevious"] = NuvioKey.Prev,
                ["ChannelUp"] = NuvioKey.ChannelUp,
                ["ChannelDown"] = NuvioKey.ChannelDown,
            };

        /// <summary>
        /// Normalizes a NUI key event. Returns null for unbound keys (colored,
        /// channel codes, modifiers, unknown letters).
        /// </summary>
        public static NuvioKey? Normalize(global::Tizen.NUI.Key key)
        {
            if (key == null) return null;
            var code = 0;
            try
            {
                code = key.KeyCode;
            }
            catch
            {
                // Some platforms expose only the name.
            }

            var name = key.KeyPressedName ?? "";

            // Letters via KeyString ("s", "t", "c", "e", "p", "b", "l").
            var letter = MapLetter(key.KeyString);
            if (letter.HasValue)
            {
                return letter;
            }

            if (BackCodes.Contains(code))
            {
                return NuvioKey.Back;
            }
            if (NameMap.TryGetValue(name, out var byName))
            {
                return byName;
            }
            if (code != 0 && CodeMap.TryGetValue(code, out var byCode))
            {
                return byCode;
            }
            if (name.Equals("Back", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Escape", StringComparison.OrdinalIgnoreCase))
            {
                return NuvioKey.Back;
            }
            if (name.Equals("Left", StringComparison.OrdinalIgnoreCase)) return NuvioKey.Left;
            if (name.Equals("Right", StringComparison.OrdinalIgnoreCase)) return NuvioKey.Right;
            if (name.Equals("Up", StringComparison.OrdinalIgnoreCase)) return NuvioKey.Up;
            if (name.Equals("Down", StringComparison.OrdinalIgnoreCase)) return NuvioKey.Down;

            return null;
        }

        private static NuvioKey? MapLetter(string keyPressed)
        {
            if (string.IsNullOrEmpty(keyPressed) || keyPressed.Length != 1)
            {
                return null;
            }
            switch (char.ToUpperInvariant(keyPressed[0]))
            {
                case 'S': return NuvioKey.LetterS;
                case 'T': return NuvioKey.LetterT;
                case 'C': return NuvioKey.LetterC;
                case 'E': return NuvioKey.LetterE;
                case 'P': return NuvioKey.LetterP;
                case 'B': return NuvioKey.LetterB;
                case 'L': return NuvioKey.LetterL;
                default: return null;
            }
        }

        public static FocusDirection? ToDirection(NuvioKey key)
        {
            switch (key)
            {
                case NuvioKey.Up: return FocusDirection.Up;
                case NuvioKey.Down: return FocusDirection.Down;
                case NuvioKey.Left: return FocusDirection.Left;
                case NuvioKey.Right: return FocusDirection.Right;
                default: return null;
            }
        }
    }
}

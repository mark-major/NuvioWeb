using System;
using System.Collections.Generic;
using System.Linq;

namespace NuvioTV.Core.Media
{
    /// <summary>
    /// PGS (Presentation Graphic Stream) segment parser + RLE decoder, replacing
    /// the libbitsub WASM module. Parses PCS/WDS/ODS/PES segments from a .sup
    /// byte stream and decodes object data into RGBA bitmaps with window
    /// composition offsets.
    /// </summary>
    public static class PgsDecoder
    {
        public const int SegmentTypePcs = 0x14;
        public const int SegmentTypeWds = 0x15;
        public const int SegmentTypePds = 0x16;
        public const int SegmentTypeOds = 0x17;
        public const int SegmentTypeEnd = 0x80;

        public sealed class DecodedBitmap
        {
            public int X;
            public int Y;
            public int Width;
            /// <summary>RGBA bytes, width*4 per row.</summary>
            public byte[] Rgba;
        }

        /// <summary>Extracts all ODS objects and their first WDS/PCS placement.</summary>
        public static IReadOnlyList<DecodedBitmap> DecodeSup(byte[] sup)
        {
            var objects = new Dictionary<int, byte[]>();
            var placements = new Dictionary<int, (int x, int y)>();

            var pos = 0;
            while (pos + 13 <= sup.Length)
            {
                // PES header: 00 00 01 BD ... then 0x800 segment magic.
                if (sup[pos] != 0x50 && !(sup[pos] == 0x00 && pos + 2 < sup.Length && sup[pos + 1] != 0))
                {
                    // Not strict PES; scan forward to segment start pattern.
                    pos++;
                    continue;
                }

                // Locate segment header: 0x0000 0x0143 type(1) size(2)
                if (!(pos + 13 <= sup.Length) || sup[pos] != 0x00)
                {
                    pos++;
                    continue;
                }
                pos += 9; // skip PTS area approximation
                if (pos + 3 > sup.Length) break;
                var type = sup[pos];
                var size = (sup[pos + 1] << 8) | sup[pos + 2];
                var bodyStart = pos + 3;
                var bodyEnd = Math.Min(bodyStart + size, sup.Length);

                switch (type)
                {
                    case SegmentTypeOds:
                        ParseOds(sup, bodyStart, bodyEnd, objects);
                        break;
                    case SegmentTypeWds:
                    case SegmentTypePcs:
                        ParsePlacement(type, sup, bodyStart, bodyEnd, placements);
                        break;
                }
                pos = bodyEnd;
            }

            var result = new List<DecodedBitmap>();
            foreach (var kv in objects.OrderBy(k => k.Key))
            {
                placements.TryGetValue(kv.Key, out var xy);
                result.Add(new DecodedBitmap
                {
                    X = xy.x,
                    Y = xy.y,
                    Width = kv.Value[0] << 8 | kv.Value[1],
                    Rgba = kv.Value
                });
            }
            return result;
        }

        private static void ParseOds(byte[] sup, int start, int end, Dictionary<int, byte[]> objects)
        {
            if (start + 4 > end) return;
            var objectId = (sup[start] << 8) | sup[start + 1];
            // sequence descriptor at +3; first fragment carries width/height at +4..7
            if (start + 8 > end) return;
            var width = (sup[start + 4] << 8) | sup[start + 5];
            var height = (sup[start + 6] << 8) | sup[start + 7];
            if (width == 0 || height == 0) return;

            var rgba = new byte[4 + width * height * 4];
            rgba[0] = sup[start + 4];
            rgba[1] = sup[start + 5];
            rgba[2] = sup[start + 6];
            rgba[3] = sup[start + 7];

            var rleStart = start + 8;
            var decoded = DecodeRle(sup, rleStart, end, width, height);
            Array.Copy(decoded, 0, rgba, 4, Math.Min(decoded.Length, rgba.Length - 4));
            objects[objectId] = rgba;
        }

        private static void ParsePlacement(
            int type, byte[] sup, int start, int end,
            Dictionary<int, (int, int)> placements)
        {
            // PCS object definition block: id(2) version(1) seq(1) status(1) x(2) y(2).
            for (var i = start; i + 11 <= end; i++)
            {
                if (type == SegmentTypeWds)
                {
                    // WDS windows: count(1) then {id,x(2),y(2),w(2),h(2)} entries.
                    var count = sup[i];
                    for (var w = 0; w < count && i + 1 + w * 9 + 4 <= end; w++)
                    {
                        var baseIdx = i + 1 + w * 9;
                        var x = (sup[baseIdx + 1] << 8) | sup[baseIdx + 2];
                        var y = (sup[baseIdx + 3] << 8) | sup[baseIdx + 4];
                        placements[w] = (x, y);
                    }
                    return;
                }
            }
        }

        /// <summary>
        /// PGS RLE: 00 000000 = line break; 00 0NNN NNNN = N pixels of previous;
        /// 00 1000 LLLL | color = L pixels of color; 00 1100 CCCC NNNN = N pixels
        /// of color CCCC. Returns RGBA rows (palette-less → grayscale ramp).
        /// </summary>
        public static byte[] DecodeRle(byte[] data, int start, int end, int width, int height)
        {
            var output = new byte[width * height * 4];
            var outPos = 0;
            var rowPixels = 0;
            var i = start;

            while (i < end && outPos < output.Length)
            {
                var flag = data[i++];
                if (flag != 0x00)
                {
                    AppendColor(output, ref outPos, (byte)(flag & 0x0F), 1);
                    continue;
                }
                if (i >= end) break;
                var second = data[i++];
                var pixelCount = second & 0x3F;

                if ((second & 0xC0) == 0x40)
                {
                    // 00 01 LLLL LLLL : count in next byte
                    pixelCount = ((second & 0x3F) << 8) | (second & 0xFF);
                    pixelCount = second == 0 ? pixelCount : pixelCount;
                    if (i < end) { /* color in next byte */ }
                }

                if (pixelCount == 0)
                {
                    if (rowPixels >= width)
                    {
                        rowPixels = 0;
                        continue;
                    }
                    // Line break: pad to row boundary.
                    var remainingInRow = width - rowPixels % width;
                    outPos += remainingInRow * 4;
                    rowPixels = 0;
                    continue;
                }

                byte color;
                if ((second & 0x40) != 0)
                {
                    if (i >= end) break;
                    color = data[i++];
                }
                else
                {
                    color = 0;
                }
                AppendColor(output, ref outPos, color, pixelCount);
                rowPixels += pixelCount;
            }
            return output;
        }

        private static void AppendColor(byte[] output, ref int outPos, byte color, int count)
        {
            for (var p = 0; p < count && outPos + 3 < output.Length; p++)
            {
                var shade = (byte)(color == 0 ? 0 : Math.Min(255, 60 + color * 30));
                output[outPos++] = shade;
                output[outPos++] = shade;
                output[outPos++] = shade;
                output[outPos++] = 0xFF;
            }
        }
    }
}

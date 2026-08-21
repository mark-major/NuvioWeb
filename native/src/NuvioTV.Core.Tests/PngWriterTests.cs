using System;
using System.IO;
using System.Linq;
using NuvioTV.Core.UI;
using Xunit;

namespace NuvioTV.Core.Tests
{
    /// <summary>Structural tests for the minimal PNG writer (QR fallback path).</summary>
    public class PngWriterTests
    {
        private static bool[,] Matrix(int w, int h, bool fill = true)
        {
            var m = new bool[h, w];
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    m[y, x] = fill;
                }
            }
            return m;
        }

        [Fact]
        public void Encode_ProducesValidPngSignature()
        {
            var png = PngWriter.EncodeGrayscale(Matrix(21, 21));
            var expected = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
            Assert.Equal(expected, png.Take(8));
            // IEND chunk type at the tail.
            Assert.Equal(new[] { (byte)'I', (byte)'E', (byte)'N', (byte)'D' }, png.Skip(png.Length - 8).Take(4));
        }

        [Fact]
        public void Encode_IhdrDimensions_IncludeScaleAndQuietZone()
        {
            var png = PngWriter.EncodeGrayscale(Matrix(21, 21), scale: 4);
            // IHDR payload: bytes 16..23 hold width/height big-endian.
            int width = ReadInt32(png, 16), height = ReadInt32(png, 20);
            Assert.Equal(21 * 4 + 4 * 4 * 2, width);   // modules + both quiet zones
            Assert.Equal(width, height);
            // Grayscale 8-bit.
            Assert.Equal(8, png[24]);
            Assert.Equal(0, png[25]);
        }

        [Fact]
        public void Encode_ChunkCrcs_AreConsistent()
        {
            var png = PngWriter.EncodeGrayscale(Matrix(25, 25), scale: 2);
            // Walk chunks and verify each CRC.
            var pos = 8;
            while (pos < png.Length - 8)
            {
                int length = ReadInt32(png, pos);
                var crcActual = (uint)ReadUInt32(png, pos + 8 + length);
                uint crcExpected = PngWriter.Crc32(png, pos + 4, length + 4);
                Assert.Equal(crcExpected, crcActual);
                pos += 12 + length;
                if (pos == png.Length) break;
            }
        }

        [Fact]
        public void Crc32_MatchesKnownVector()
        {
            // CRC32("123456789") = 0xCBF43926
            var data = System.Text.Encoding.ASCII.GetBytes("123456789");
            Assert.Equal(0xCBF43926u, PngWriter.Crc32(data, 0, data.Length));
        }

        [Fact]
        public void Adler32_MatchesKnownVector()
        {
            // Adler-32("Wikipedia") = 0x11E60398
            var data = System.Text.Encoding.ASCII.GetBytes("Wikipedia");
            Assert.Equal(0x11E60398u, PngWriter.Adler32(data));
        }

        private static int ReadInt32(byte[] b, int o) =>
            (b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3];

        private static uint ReadUInt32(byte[] b, int o) => unchecked((uint)ReadInt32(b, o));
    }
}

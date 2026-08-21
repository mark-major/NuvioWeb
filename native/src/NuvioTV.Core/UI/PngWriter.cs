using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace NuvioTV.Core.UI
{
    /// <summary>
    /// Minimal PNG encoder (8-bit grayscale, no filtering) used to render QR
    /// matrices to files NUI can display via ImageUrl. Pure BCL so it runs on
    /// host and device; CRC32/Adler-32 implemented locally.
    /// </summary>
    public static class PngWriter
    {
        /// <summary>Encodes a boolean matrix (true = dark module) into PNG bytes.</summary>
        public static byte[] EncodeGrayscale(bool[,] matrix, int scale = 4, int quietZoneModules = 4)
        {
            if (matrix == null) throw new ArgumentNullException(nameof(matrix));
            if (scale < 1) throw new ArgumentOutOfRangeException(nameof(scale));
            var modulesY = matrix.GetLength(0);
            var modulesX = matrix.GetLength(1);

            var margin = quietZoneModules * scale;
            var width = modulesX * scale + margin * 2;
            var height = modulesY * scale + margin * 2;

            // Raw scanlines: filter byte 0 + one grayscale byte per pixel.
            var stride = width + 1;
            var raw = new byte[stride * height];
            for (var y = 0; y < height; y++)
            {
                raw[y * stride] = 0; // filter type none
                // White background by default (255); dark modules painted below.
                var rowOffset = y * stride + 1;
                for (var x = 0; x < width; x++)
                {
                    raw[rowOffset + x] = 255;
                }
            }
            for (var my = 0; my < modulesY; my++)
            {
                for (var mx = 0; mx < modulesX; mx++)
                {
                    if (!matrix[my, mx]) continue;
                    for (var dy = 0; dy < scale; dy++)
                    {
                        var py = margin + my * scale + dy;
                        var rowOffset = py * stride + 1;
                        for (var dx = 0; dx < scale; dx++)
                        {
                            var px = margin + mx * scale + dx;
                            raw[rowOffset + px] = 0;
                        }
                    }
                }
            }

            using (var output = new MemoryStream())
            {
                // Signature
                output.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, 0, 8);

                // IHDR
                var ihdr = new byte[13];
                WriteBigEndian(ihdr, 0, width);
                WriteBigEndian(ihdr, 4, height);
                ihdr[8] = 8;  // bit depth
                ihdr[9] = 0;  // color type: grayscale
                ihdr[10] = 0; // compression
                ihdr[11] = 0; // filter
                ihdr[12] = 0; // interlace
                WriteChunk(output, "IHDR", ihdr);

                // IDAT: zlib wrapper around deflated scanlines.
                var compressed = Compress(raw);
                var idat = new byte[compressed.Length + 6];
                idat[0] = 0x78; // zlib CMF
                idat[1] = 0x01; // zlib FLG
                Array.Copy(compressed, 0, idat, 2, compressed.Length);
                WriteBigEndian(idat, idat.Length - 4, unchecked((int)Adler32(raw)));
                WriteChunk(output, "IDAT", idat);

                WriteChunk(output, "IEND", new byte[0]);

                return output.ToArray();
            }
        }

        private static void WriteChunk(MemoryStream output, string type, byte[] data)
        {
            var chunk = new byte[data.Length + 12];
            WriteBigEndian(chunk, 0, data.Length);
            for (var i = 0; i < 4; i++)
            {
                chunk[4 + i] = (byte)type[i];
            }
            Array.Copy(data, 0, chunk, 8, data.Length);
            WriteBigEndian(chunk, 8 + data.Length, unchecked((int)Crc32(chunk, 4, data.Length + 4)));
            output.Write(chunk, 0, chunk.Length);
        }

        private static void WriteBigEndian(byte[] buffer, int offset, int value)
        {
            buffer[offset] = (byte)(value >> 24 & 0xFF);
            buffer[offset + 1] = (byte)(value >> 16 & 0xFF);
            buffer[offset + 2] = (byte)(value >> 8 & 0xFF);
            buffer[offset + 3] = (byte)(value & 0xFF);
        }

        internal static byte[] Compress(byte[] data)
        {
            using (var compressedStream = new MemoryStream())
            {
                using (var deflate = new DeflateStream(compressedStream, CompressionLevel.Optimal, true))
                {
                    deflate.Write(data, 0, data.Length);
                }
                return compressedStream.ToArray();
            }
        }

        private static readonly uint[] CrcTable = BuildCrcTable();

        private static uint[] BuildCrcTable()
        {
            var table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                var c = n;
                for (var k = 0; k < 8; k++)
                {
                    c = ((c & 1) != 0) ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                }
                table[n] = c;
            }
            return table;
        }

        /// <summary>Standard PNG CRC32 over a byte range.</summary>
        public static uint Crc32(byte[] data, int offset, int length)
        {
            uint crc = 0xFFFFFFFFu;
            for (var i = offset; i < offset + length; i++)
            {
                crc = CrcTable[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
            }
            return crc ^ 0xFFFFFFFFu;
        }

        public static uint Adler32(byte[] data)
        {
            uint a = 1, b = 0;
            foreach (var byteValue in data)
            {
                a = (a + byteValue) % 65521;
                b = (b + a) % 65521;
            }
            return (b << 16) | a;
        }
    }
}

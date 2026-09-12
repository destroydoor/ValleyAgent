using System;
using System.IO;
using System.IO.Compression;
using System.Threading;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewValley;

namespace ValleyAgent.Autopilot.Capture
{
    /// <summary>
    /// 截取当前游戏画面，编码为 PNG base64 返回。
    /// 在 UpdateTicked 中缓存帧数据，避免跨线程访问 GraphicsDevice。
    /// </summary>
    public sealed class ScreenCapture
    {
        private readonly IMonitor _monitor;
        private byte[]? _cachedPng;
        private int _cachedWidth;
        private int _cachedHeight;
        private volatile bool _captureRequested;
        private readonly ManualResetEventSlim _captureReady = new(false);

        public ScreenCapture(IMonitor monitor)
        {
            _monitor = monitor;
        }

        public void OnUpdateTicked()
        {
            if (!_captureRequested) return;
            _captureRequested = false;

            try
            {
                var gd = Game1.graphics?.GraphicsDevice;
                if (gd == null) return;

                var pp = gd.PresentationParameters;
                int w = pp.BackBufferWidth;
                int h = pp.BackBufferHeight;

                var data = new Microsoft.Xna.Framework.Color[w * h];
                gd.GetBackBufferData(data);

                int targetW = 640;
                int targetH = 360;
                var resized = ResizeNearestNeighbor(data, w, h, targetW, targetH);

                _cachedPng = EncodePng(resized, targetW, targetH);
                _cachedWidth = targetW;
                _cachedHeight = targetH;
                _captureReady.Set();
            }
            catch (InvalidOperationException ex)
            {
                _monitor.Log($"ScreenCapture failed: {ex.Message}", LogLevel.Warn);
                _cachedPng = null;
            }
            catch (ArgumentException ex)
            {
                _monitor.Log($"ScreenCapture failed: {ex.Message}", LogLevel.Warn);
                _cachedPng = null;
            }
            catch (OutOfMemoryException ex)
            {
                _monitor.Log($"ScreenCapture failed: {ex.Message}", LogLevel.Warn);
                _cachedPng = null;
            }
        }

        public void RequestCapture()
        {
            _captureReady.Reset();
            _captureRequested = true;
        }

        public (byte[] png, int width, int height)? WaitForScreenshot(int timeoutMs = 2000)
        {
            if (!_captureReady.Wait(timeoutMs)) return null;
            return GetCachedScreenshot();
        }

        public (byte[] png, int width, int height)? GetCachedScreenshot()
        {
            if (_cachedPng == null) return null;
            return (_cachedPng, _cachedWidth, _cachedHeight);
        }

        private static Microsoft.Xna.Framework.Color[] ResizeNearestNeighbor(
            Microsoft.Xna.Framework.Color[] src, int srcW, int srcH, int dstW, int dstH)
        {
            var dst = new Microsoft.Xna.Framework.Color[dstW * dstH];
            float xRatio = (float)srcW / dstW;
            float yRatio = (float)srcH / dstH;

            for (int y = 0; y < dstH; y++)
            {
                for (int x = 0; x < dstW; x++)
                {
                    int sx = (int)(x * xRatio);
                    int sy = (int)(y * yRatio);
                    dst[y * dstW + x] = src[sy * srcW + sx];
                }
            }
            return dst;
        }

        private static byte[] EncodePng(Microsoft.Xna.Framework.Color[] pixels, int width, int height)
        {
            using var ms = new MemoryStream();
            ms.WriteByte(0x89);
            ms.Write(new byte[] { 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

            WriteChunk(ms, "IHDR", ihdr =>
            {
                WriteBigEndian32(ihdr, (uint)width);
                WriteBigEndian32(ihdr, (uint)height);
                ihdr.WriteByte(8);
                ihdr.WriteByte(2);
                ihdr.WriteByte(0);
                ihdr.WriteByte(0);
                ihdr.WriteByte(0);
            });

            using var rawMs = new MemoryStream();
            for (int y = 0; y < height; y++)
            {
                rawMs.WriteByte(0);
                for (int x = 0; x < width; x++)
                {
                    var c = pixels[y * width + x];
                    rawMs.WriteByte(c.R);
                    rawMs.WriteByte(c.G);
                    rawMs.WriteByte(c.B);
                }
            }

            var compressed = DeflateCompress(rawMs.ToArray());
            WriteChunk(ms, "IDAT", idat => idat.Write(compressed));
            WriteChunk(ms, "IEND", _ => { });

            return ms.ToArray();
        }

        private static void WriteChunk(Stream ms, string type, Action<Stream> writeData)
        {
            using var dataMs = new MemoryStream();
            writeData(dataMs);
            var data = dataMs.ToArray();

            WriteBigEndian32(ms, (uint)data.Length);
            var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
            ms.Write(typeBytes);
            ms.Write(data);

            using var crcMs = new MemoryStream();
            crcMs.Write(typeBytes);
            crcMs.Write(data);
            uint crc = Crc32(crcMs.ToArray());
            WriteBigEndian32(ms, crc);
        }

        private static byte[] DeflateCompress(byte[] data)
        {
            using var output = new MemoryStream();
            output.WriteByte(0x78);
            output.WriteByte(0x01);
            using (var ds = new DeflateStream(output, CompressionMode.Compress, leaveOpen: true))
            {
                ds.Write(data);
            }
            uint adler = Adler32(data);
            WriteBigEndian32(output, adler);
            return output.ToArray();
        }

        private static uint Adler32(byte[] data)
        {
            uint a = 1, b = 0;
            foreach (byte bt in data)
            {
                a = (a + bt) % 65521;
                b = (b + a) % 65521;
            }
            return (b << 16) | a;
        }

        private static uint Crc32(byte[] data)
        {
            uint crc = 0xFFFFFFFF;
            foreach (byte b in data)
            {
                crc ^= b;
                for (int i = 0; i < 8; i++)
                {
                    crc = (crc >> 1) ^ (crc & 1) * 0xEDB88320;
                }
            }
            return ~crc;
        }

        private static void WriteBigEndian32(Stream s, uint value)
        {
            s.WriteByte((byte)((value >> 24) & 0xFF));
            s.WriteByte((byte)((value >> 16) & 0xFF));
            s.WriteByte((byte)((value >> 8) & 0xFF));
            s.WriteByte((byte)(value & 0xFF));
        }
    }
}

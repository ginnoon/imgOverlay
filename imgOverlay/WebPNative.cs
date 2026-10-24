using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace imgOverlay
{
    internal static class WebPNative
    {
        private const string Dll = "libwebpdemux.dll";
        private const int Abi = 0x0107;

        [StructLayout(LayoutKind.Sequential)]
        private struct WebPData
        {
            public IntPtr bytes;
            public nuint size;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DecoderOptions
        {
            public int colorMode;
            public int useThreads;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 7)]
            public uint[] padding;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct AnimInfo
        {
            public uint width;
            public uint height;
            public uint loopCount;
            public uint backgroundColor;
            public uint frameCount;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
            public uint[] padding;
        }

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern int WebPAnimDecoderOptionsInitInternal(
            ref DecoderOptions options, int abiVersion);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr WebPAnimDecoderNewInternal(
            ref WebPData data, ref DecoderOptions options, int abiVersion);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern int WebPAnimDecoderGetInfo(
            IntPtr decoder, out AnimInfo info);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern int WebPAnimDecoderHasMoreFrames(IntPtr decoder);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern int WebPAnimDecoderGetNext(
            IntPtr decoder, out IntPtr buffer, out int timestamp);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern void WebPAnimDecoderDelete(IntPtr decoder);

        internal sealed class Frame
        {
            public Bitmap Bitmap { get; }
            public int Duration { get; }

            public Frame(Bitmap bitmap, int duration)
            {
                Bitmap = bitmap;
                Duration = Math.Max(1, duration);
            }
        }

        internal sealed class Decoded : IDisposable
        {
            public List<Frame> Frames { get; } = new();

            public void Dispose()
            {
                foreach (var frame in Frames)
                    frame.Bitmap.Dispose();

                Frames.Clear();
            }
        }

        public static bool IsWebP(byte[] data)
        {
            // RIFF....WEBP
            return data.Length >= 12 &&
                data[0] == (byte)'R' &&
                data[1] == (byte)'I' &&
                data[2] == (byte)'F' &&
                data[3] == (byte)'F' &&
                data[8] == (byte)'W' &&
                data[9] == (byte)'E' &&
                data[10] == (byte)'B' &&
                data[11] == (byte)'P';
        }

        public static Decoded Decode(byte[] bytes)
        {
            var decoded = new Decoded();
            var pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            IntPtr decoder = IntPtr.Zero;

            try
            {
                var data = new WebPData
                {
                    bytes = pin.AddrOfPinnedObject(),
                    size = (nuint)bytes.Length
                };

                var options = new DecoderOptions
                {
                    // MODE_BGRA
                    colorMode = 1,
                    useThreads = 1,
                    padding = new uint[7]
                };

                if (WebPAnimDecoderOptionsInitInternal(
                        ref options, Abi) == 0)
                    throw new InvalidOperationException(
                        "libwebpdemux ABI 버전이 맞지 않습니다.");

                decoder = WebPAnimDecoderNewInternal(
                    ref data, ref options, Abi);

                if (decoder == IntPtr.Zero)
                    throw new InvalidOperationException(
                        "WebP 디코더 생성 실패");

                if (WebPAnimDecoderGetInfo(decoder, out var info) == 0)
                    throw new InvalidOperationException(
                        "WebP 정보 읽기 실패");

                int width = checked((int)info.width);
                int height = checked((int)info.height);

                int previousTimestamp = 0;

                while (WebPAnimDecoderHasMoreFrames(decoder) != 0)
                {
                    if (WebPAnimDecoderGetNext(
                            decoder, out IntPtr pixels,
                            out int timestamp) == 0)
                        break;

                    int duration = timestamp - previousTimestamp;
                    previousTimestamp = timestamp;

                    var bitmap = new Bitmap(
                        width, height, PixelFormat.Format32bppArgb);

                    var rect = new Rectangle(0, 0, width, height);
                    var bits = bitmap.LockBits(
                        rect, ImageLockMode.WriteOnly,
                        PixelFormat.Format32bppArgb);

                    try
                    {
                        int rowBytes = checked(width * 4);
                        byte[] row = new byte[rowBytes];

                        for (int y = 0; y < height; y++)
                        {
                            Marshal.Copy(
                                IntPtr.Add(pixels, y * rowBytes),
                                row, 0, rowBytes);

                            for (int x = 0; x < width; x++)
                            {
                                int i = x * 4;
                                (row[i], row[i + 2]) = (row[i + 2], row[i]);
                            }

                            Marshal.Copy(
                                row, 0,
                                IntPtr.Add(bits.Scan0, y * bits.Stride),
                                rowBytes);
                        }
                    }
                    finally
                    {
                        bitmap.UnlockBits(bits);
                    }

                    decoded.Frames.Add(new Frame(bitmap, duration));
                }

                if (decoded.Frames.Count == 0)
                    throw new InvalidOperationException(
                        "WebP 프레임이 없습니다.");

                return decoded;
            }
            catch
            {
                decoded.Dispose();
                throw;
            }
            finally
            {
                if (decoder != IntPtr.Zero)
                    WebPAnimDecoderDelete(decoder);

                pin.Free();
            }
        }
    }
}

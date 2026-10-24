using System.Drawing.Imaging;

namespace imgOverlay
{
    public sealed class AlphaHitTest : IDisposable
    {
        private byte[]? alphaMask;

        private int imageWidth;
        private int imageHeight;

        private bool lastResult;
        private bool cacheValid;

        public bool LastResult { get { return lastResult; } }

        // 이미지가 바뀔 때 딱 한 번 호출
        public unsafe void SetImage(Bitmap source)
        {
            DisposeMask();

            imageWidth = source.Width;
            imageHeight = source.Height;

            alphaMask = new byte[checked(imageWidth * imageHeight)];

            Bitmap? converted = null;
            BitmapData? data = null;

            try
            {
                Bitmap bmp = source;

                if (source.PixelFormat != PixelFormat.Format32bppArgb)
                {
                    converted = new Bitmap(
                        imageWidth,
                        imageHeight,
                        PixelFormat.Format32bppArgb);

                    using (Graphics g = Graphics.FromImage(converted))
                        g.DrawImageUnscaled(source, 0, 0);

                    bmp = converted;
                }

                data = bmp.LockBits(
                    new Rectangle(0, 0, imageWidth, imageHeight),
                    ImageLockMode.ReadOnly,
                    PixelFormat.Format32bppArgb);

                byte* basePtr = (byte*)data.Scan0;

                for (int y = 0; y < imageHeight; y++)
                {
                    byte* row = basePtr + y * data.Stride;
                    int maskRow = y * imageWidth;

                    for (int x = 0; x < imageWidth; x++)
                        alphaMask[maskRow + x] = row[x * 4 + 3];
                }
            }
            catch
            {
                DisposeMask();
                throw;
            }
            finally
            {
                if (data != null)
                {
                    if (converted != null)
                        converted.UnlockBits(data);
                    else
                        source.UnlockBits(data);
                }

                converted?.Dispose();
            }

            cacheValid = false;
        }

        // 매 프레임 호출해도 되는 함수
        public bool IsMouseOverOpaquePixel(Rectangle drawRect)
        {
            if (alphaMask == null ||
                drawRect.Width <= 0 ||
                drawRect.Height <= 0)
                return lastResult = false;

            Point mouse = Cursor.Position;
            return lastResult = drawRect.Contains(mouse);
            //if (!drawRect.Contains(mouse))
            //    return lastResult = false;

            //int x = mouse.X - drawRect.X;
            //int y = mouse.Y - drawRect.Y;

            //int px = (int)((long)x * imageWidth / drawRect.Width);
            //int py = (int)((long)y * imageHeight / drawRect.Height);

            //return lastResult = alphaMask[py * imageWidth + px] > 0;
        }

        private void DisposeMask()
        {
            alphaMask = null;
            cacheValid = false;
        }

        public void Dispose()
        {
            DisposeMask();
        }
    }
}

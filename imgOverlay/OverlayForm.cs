using System.ComponentModel;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace imgOverlay
{
    public sealed class OverlayForm : Form
    {
        private const int WS_EX_TRANSPARENT = 0x20;
        private const int WS_EX_TOOLWINDOW = 0x80;
        private const int WS_EX_LAYERED = 0x80000;
        private const int WS_EX_NOACTIVATE = 0x08000000;

        private const int WM_HOTKEY = 0x0312;
        private const int INTERACTIVE_ID = 1;

        private const uint MOD_CONTROL = 0x0002;
        private const uint MOD_SHIFT = 0x0004;
        private const uint MOD_NOREPEAT = 0x4000;
        private const uint VK_F10 = 0x79;

        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_SHOWWINDOW = 0x0040;

        private const uint WDA_EXCLUDEFROMCAPTURE = 0x11;

        private const int ULW_ALPHA = 0x2;
        private const byte AC_SRC_OVER = 0x00;
        private const byte AC_SRC_ALPHA = 0x01;
        private const uint DIB_RGB_COLORS = 0;
        private const int BI_RGB = 0;

        private static readonly IntPtr HWND_TOPMOST = new(-1);

        private readonly PeriodicTimer frameTimer = new(TimeSpan.FromMilliseconds(16.67f * 2f));
        private CancellationTokenSource? _cts;
        private readonly Stopwatch clock = Stopwatch.StartNew();

        // 한 번만 만들어 재사용하는 렌더 대상 (DIB 섹션 + GDI+ 래퍼)
        private IntPtr memDc;
        private IntPtr hDib;
        private IntPtr oldBitmap;
        private Bitmap? frameBmp;
        private Graphics? g;
        private readonly int width;
        private readonly int height;

        private static readonly HttpClient httpClient = new();
        private Image? image;
        private float imageScale = 1.0f;
        private int xOffset = 450;
        private bool invertX = false;
        private bool invertY = false;
        private bool imageRequested;

        private bool interactive;
        private bool hotkeyRegistered;

        public bool IsInteractive => interactive;


        private WebPNative.Decoded? webp;
        private int webpFrameIndex;

        private readonly System.Windows.Forms.Timer webpTimer = new();


        [DefaultValue(1.0f)]
        public float ImageScale
        {
            get { return imageScale; }
            set
            {
                imageScale = MathF.Max(.1f, value);
                if (image != null)
                    mainForm?.SetXOffsetMaximum(Screen.PrimaryScreen!.Bounds.Width - (int)(image.Width * imageScale));
            }
        }

        [DefaultValue(450)]
        public int XOffset
        {
            get { return xOffset; }
            set { xOffset = value; }
        }

        [DefaultValue(false)]
        public bool InvertX
        {
            get { return invertX; }
            set { invertX = value; }
        }

        [DefaultValue(false)]
        public bool InvertY
        {
            get { return invertY; }
            set { invertY = value; }
        }

        private readonly Form1? mainForm;

        public OverlayForm(Form1 mainForm)
        {
            this.mainForm = mainForm;

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            TopMost = true;

            Bounds = SystemInformation.VirtualScreen;
            width = Width;
            height = Height;

            httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
                "AppleWebKit/537.36 (KHTML, like Gecko) " +
                "Chrome/150.0.0.0 Safari/537.36");

            httpClient.DefaultRequestHeaders.Referrer = new Uri("https://minecraft.wiki/");

            CreateRenderTarget();

            webpTimer.Tick += (_, _) =>
            {
                if (webp == null || webp.Frames.Count <= 1)
                {
                    webpTimer.Stop();
                    return;
                }

                webpFrameIndex =
                    (webpFrameIndex + 1) % webp.Frames.Count;

                var frame = webp.Frames[webpFrameIndex];

                image = frame.Bitmap;
                hitTest.SetImage(frame.Bitmap);

                webpTimer.Interval = frame.Duration;
            };

            //frameTimer.Tick += (_, _) => Render();
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        private async Task RunFrameLoopAsync(CancellationToken token)
        {
            try
            {
                while (await frameTimer.WaitForNextTickAsync(token))
                {
                    Render();
                }
            }
            catch (OperationCanceledException) { }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= WS_EX_LAYERED
                            | WS_EX_TOOLWINDOW
                            | WS_EX_TRANSPARENT
                            | WS_EX_NOACTIVATE;
                return cp;
            }
        }

        protected override async void OnShown(EventArgs e)
        {
            base.OnShown(e);

            //SetWindowDisplayAffinity(Handle, WDA_EXCLUDEFROMCAPTURE);

            hotkeyRegistered = RegisterHotKey(
                Handle, INTERACTIVE_ID, MOD_CONTROL | MOD_SHIFT | MOD_NOREPEAT, VK_F10);
            RegisterHotKey(Handle, 2, MOD_NOREPEAT, 0x77);

            KeepOnTop();

            _cts = new CancellationTokenSource();
            await RunFrameLoopAsync(_cts.Token);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _cts?.Cancel();
            frameTimer.Dispose();

            if (hotkeyRegistered)
            {
                UnregisterHotKey(Handle, INTERACTIVE_ID);
                UnregisterHotKey(Handle, 2);
            }

            g?.Dispose();
            frameBmp?.Dispose();
            SelectObject(memDc, oldBitmap);
            DeleteObject(hDib);
            DeleteDC(memDc);

            base.OnFormClosed(e);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == INTERACTIVE_ID)
                SetInteractive(!interactive);
            if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == 2) { }

            base.WndProc(ref m);
        }

        private void CreateRenderTarget()
        {
            IntPtr screenDc = GetDC(IntPtr.Zero);
            memDc = CreateCompatibleDC(screenDc);
            ReleaseDC(IntPtr.Zero, screenDc);

            var bmi = new BITMAPINFO
            {
                bmiHeader = new BITMAPINFOHEADER
                {
                    biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
                    biWidth = width,
                    biHeight = -height,
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = BI_RGB
                }
            };

            hDib = CreateDIBSection(memDc, ref bmi, DIB_RGB_COLORS, out IntPtr bits, IntPtr.Zero, 0);
            oldBitmap = SelectObject(memDc, hDib);

            frameBmp = new Bitmap(width, height, width * 4, PixelFormat.Format32bppPArgb, bits);
            g = Graphics.FromImage(frameBmp);
            g.SmoothingMode = SmoothingMode.HighQuality;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
        }

        private void SetInteractive(bool enabled)
        {
            if (interactive == enabled)
                return;

            interactive = enabled;

            long style = GetWindowLongPtr(Handle, -20).ToInt64();

            if (enabled)
            {
                style &= ~(long)WS_EX_TRANSPARENT;
                style &= ~WS_EX_NOACTIVATE;
            }
            else
            {
                style |= WS_EX_TRANSPARENT;
                style |= WS_EX_NOACTIVATE;
            }

            SetWindowLongPtr(Handle, -20, new IntPtr(style));
            KeepOnTop();
        }

        private void Render()
        {
            if (!IsHandleCreated || g == null)
                return;

            RequestImageOnce();
            DrawFrame(g, (float)clock.Elapsed.TotalSeconds);
            Present();
        }

        private readonly AlphaHitTest hitTest = new();
        private bool a = true;

        private void DrawFrame(Graphics g, float t)
        {
            var sc = Screen.PrimaryScreen!.Bounds;
            g.Clear(Color.Transparent);

            if (image != null)
            {
                lock (image)
                {
                    if (ImageAnimator.CanAnimate(image))
                    {
                        ImageAnimator.UpdateFrames(image);
                    }

                    var opacity = 1f;

                    int scaledWidth = (int)(image.Width * ImageScale);
                    int scaledHeight = (int)(image.Height * ImageScale);
                    int finalX = invertX ? xOffset + scaledWidth : xOffset;
                    Rectangle rect = new(finalX, sc.Height - scaledHeight, (1 - (Unsafe.As<bool, byte>(ref invertX) << 1)) * scaledWidth, scaledHeight);

                    //Rectangle rect = new(450, sc.Height - image.Height, image.Width, image.Height);
                    if ((a && hitTest.IsMouseOverOpaquePixel(rect)) || hitTest.LastResult)
                    {
                        opacity = .5f;
                    }
                    a = !a;

                    var matrix = new ColorMatrix { Matrix33 = opacity };
                    var attr = new ImageAttributes();
                    attr.SetColorMatrix(matrix);

                    //int newHeight = (int)((double)image.Height * sc.Width / image.Width);
                    //g.DrawImage(image, new Rectangle(0, sc.Height - newHeight, sc.Width, newHeight), 0, 0, image.Width, image.Height, GraphicsUnit.Pixel, attr);
                    g.DrawImage(image, rect, 0, 0, image.Width, image.Height, GraphicsUnit.Pixel, attr);
                }
            }
        }

        private void RequestImageOnce()
        {
            if (imageRequested) return;

            imageRequested = true;
            _ = LoadImageAsync("https://deltarune.wiki/images/Pink_face_shop.gif?cb=t2zz0g&h=thumb.php&f=Pink_face_shop.gif");
        }

        public void SetImage(string url)
        {
            _ = LoadImageAsync(url);
        }

        private async Task LoadImageAsync(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)) return;
            if (uri.IsFile)
            {
                var loaded = Image.FromFile(uri.LocalPath);

                var oldImage = image;
                image = loaded;
                hitTest.SetImage((Bitmap)loaded);
                mainForm?.SetXOffsetMaximum(Screen.PrimaryScreen!.Bounds.Width - (int)(loaded.Width * imageScale));

                if (oldImage != null && !ReferenceEquals(oldImage, loaded))
                    oldImage.Dispose();

                if (webp != null && webp.Frames.Count > 1)
                {
                    webpTimer.Interval = webp.Frames[0].Duration;
                    webpTimer.Start();
                }
                else if (ImageAnimator.CanAnimate(loaded))
                {
                    ImageAnimator.Animate(loaded, OnFrameChanged);
                }
                return;
            }

            try
            {
                using var response = await httpClient.GetAsync(uri);
                response.EnsureSuccessStatusCode();

                byte[] bytes = await response.Content.ReadAsByteArrayAsync();

                webpTimer.Stop();

                if (image != null)
                    ImageAnimator.StopAnimate(image, OnFrameChanged);

                webp?.Dispose();
                webp = null;

                Image loaded;

                if (WebPNative.IsWebP(bytes))
                {
                    webp = WebPNative.Decode(bytes);
                    webpFrameIndex = 0;

                    loaded = webp.Frames[0].Bitmap;
                }
                else
                {
                    var ms = new MemoryStream(bytes);
                    loaded = Image.FromStream(ms);
                }

                var oldImage = image;

                image = loaded;
                hitTest.SetImage((Bitmap)loaded);
                mainForm?.SetXOffsetMaximum(Screen.PrimaryScreen!.Bounds.Width - (int)(loaded.Width * imageScale));

                if (oldImage != null && !ReferenceEquals(oldImage, loaded))
                    oldImage.Dispose();

                if (webp != null && webp.Frames.Count > 1)
                {
                    webpTimer.Interval = webp.Frames[0].Duration;
                    webpTimer.Start();
                }
                else if (ImageAnimator.CanAnimate(loaded))
                {
                    ImageAnimator.Animate(loaded, OnFrameChanged);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                imageRequested = false;
            }
        }

        public async void SetImageFromClipboard()
        {
            var loaded = await TryGetClipboardBitmap();
            if (loaded == null) return;

            var oldImage = image;
            image = loaded;
            hitTest.SetImage((Bitmap)loaded);
            mainForm?.SetXOffsetMaximum(Screen.PrimaryScreen!.Bounds.Width - (int)(loaded.Width * imageScale));

            if (oldImage != null && !ReferenceEquals(oldImage, loaded))
                oldImage.Dispose();

            if (webp != null && webp.Frames.Count > 1)
            {
                webpTimer.Interval = webp.Frames[0].Duration;
                webpTimer.Start();
            }
            else if (ImageAnimator.CanAnimate(loaded))
            {
                ImageAnimator.Animate(loaded, OnFrameChanged);
            }
        }

        private async Task<Image?> TryGetClipboardBitmap()
        {
            try
            {
                if (Clipboard.ContainsImage())
                    return Clipboard.GetImage();

                var data = Clipboard.GetDataObject();
                if (data == null)
                    return null;

                if (data.GetDataPresent(typeof(Bitmap)))
                {
                    using var bmp = (Bitmap?)data.GetData(typeof(Bitmap));
                    IntPtr hBitmap = bmp.GetHbitmap();

                    try
                    {
                        return Image.FromHbitmap(hBitmap);
                    }
                    finally
                    {
                        DeleteObject(hBitmap);
                    }
                }

                if (data.GetDataPresent(DataFormats.Dib))
                {
                    if (data.GetData(DataFormats.Dib) is MemoryStream stream)
                    {
                        Image.FromStream(stream);
                        return null;
                    }
                }
            }
            catch { }

            return null;
        }

        private void OnFrameChanged(object? sender, EventArgs e)
        {
            if (InvokeRequired)
            {
                BeginInvoke(Invalidate);
            }
            else
            {
                Invalidate();
            }
        }

        // 이미 premultiplied 포맷에 그렸으므로 변환 없이 바로 올린다
        private void Present()
        {
            IntPtr screenDc = GetDC(IntPtr.Zero);

            var dst = new POINT { x = Left, y = Top };
            var size = new SIZE { cx = width, cy = height };
            var src = new POINT { x = 0, y = 0 };
            var blend = new BLENDFUNCTION
            {
                BlendOp = AC_SRC_OVER,
                BlendFlags = 0,
                SourceConstantAlpha = 255,
                AlphaFormat = AC_SRC_ALPHA
            };

            UpdateLayeredWindow(Handle, screenDc, ref dst, ref size,
                memDc, ref src, 0, ref blend, ULW_ALPHA);

            ReleaseDC(IntPtr.Zero, screenDc);
        }

        private void KeepOnTop()
        {
            SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int x; public int y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct SIZE { public int cx; public int cy; }

        [StructLayout(LayoutKind.Sequential)]
        private struct BLENDFUNCTION
        {
            public byte BlendOp;
            public byte BlendFlags;
            public byte SourceConstantAlpha;
            public byte AlphaFormat;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFOHEADER
        {
            public int biSize;
            public int biWidth;
            public int biHeight;
            public short biPlanes;
            public short biBitCount;
            public int biCompression;
            public int biSizeImage;
            public int biXPelsPerMeter;
            public int biYPelsPerMeter;
            public int biClrUsed;
            public int biClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFO
        {
            public BITMAPINFOHEADER bmiHeader;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UpdateLayeredWindow(
            IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize,
            IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);

        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr h);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateDIBSection(
            IntPtr hdc, ref BITMAPINFO pbmi, uint usage,
            out IntPtr ppvBits, IntPtr hSection, uint offset);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(
            IntPtr hWnd, IntPtr hWndInsertAfter,
            int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);
    }
}
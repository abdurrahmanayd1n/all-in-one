// Tools that work on a frozen picture of the screen. Nothing of it is written to disk.
// Here: the shared overlay, and text from any part of the screen (drag a rectangle, its text goes to the clipboard).
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PixelFormat = System.Drawing.Imaging.PixelFormat;

// A window over all screens that shows them as they were a moment ago. Esc or switching away gives up.
// It is created per-monitor DPI aware, so its pixels are the real pixels of every screen.
abstract class ScreenOverlay : Form
{
    static bool open; // one at a time
    protected readonly Bitmap Frozen;
    bool closing;

    // Waits a moment so that menus and our own buttons are gone from the picture. finished runs when
    // the overlay is gone; false means another overlay is already there and nothing will happen.
    public static bool Open(Func<Rectangle, ScreenOverlay> make, Action finished)
    {
        if (open) return false;
        open = true;
        var wait = new Timer { Interval = 280 };
        wait.Tick += (s, e) =>
        {
            wait.Dispose();
            IntPtr awareness = Native.SetThreadDpiAwarenessContext(new IntPtr(-4)); // per monitor, version 2
            try
            {
                var overlay = make(SystemInformation.VirtualScreen);
                if (finished != null) overlay.FormClosed += (a, b) => finished();
                overlay.Show();
                overlay.Activate();
            }
            catch (Exception x) // a locked or secure desktop cannot be captured
            {
                open = false;
                if (finished != null) finished();
                Log.Error("ekran alınamadı", x);
                MessageBox.Show(Lang.T("Ekran alınamadı: ") + x.Message, "All In One");
            }
            finally { Native.SetThreadDpiAwarenessContext(awareness); }
        };
        wait.Start();
        return true;
    }

    protected ScreenOverlay(Rectangle screen)
    {
        Frozen = new Bitmap(screen.Width, screen.Height, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(Frozen)) g.CopyFromScreen(screen.Location, Point.Empty, screen.Size);
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = screen;
        ShowInTaskbar = false;
        TopMost = true;
        DoubleBuffered = true;
        KeyPreview = true;
        Cursor = Cursors.Cross;
    }

    // The hint at the top of the main screen, whose upper left corner is the desktop's zero point.
    protected void Hint(Graphics g, string hint)
    {
        using (var font = new Font("Segoe UI", 15, GraphicsUnit.Pixel))
        using (var back = new SolidBrush(System.Drawing.Color.FromArgb(220, 27, 28, 35)))
        {
            var size = g.MeasureString(hint, font);
            float x = -Bounds.X + (SystemInformation.PrimaryMonitorSize.Width - size.Width) / 2, y = -Bounds.Y + 24;
            g.FillRectangle(back, x - 14, y - 8, size.Width + 28, size.Height + 16);
            g.DrawString(hint, font, System.Drawing.Brushes.White, x, y);
        }
    }

    // Letting go of the Alt that opened the overlay must not start a menu loop that eats the first click.
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x112 /* WM_SYSCOMMAND */ && (m.WParam.ToInt64() & 0xFFF0) == 0xF100 /* SC_KEYMENU */) return;
        base.WndProc(ref m);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == System.Windows.Forms.Keys.Escape) Close();
    }

    // Switching to another program gives up.
    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        if (!closing) BeginInvoke((Action)Close);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        closing = true;
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        open = false; // before the event, so that it may open the next one
        base.OnFormClosed(e);
        Frozen.Dispose();
    }
}

class ScreenText : ScreenOverlay
{
    Point from;
    Rectangle area;
    bool dragging;
    readonly bool table; // the selection holds a table: keep its columns

    public static void Start()
    {
        Open(screen => new ScreenText(screen, false), null);
    }

    public static void StartTable()
    {
        Open(screen => new ScreenText(screen, true), null);
    }

    ScreenText(Rectangle screen, bool table) : base(screen)
    {
        this.table = table;
        Text = table ? Lang.T("All In One - ekrandan tablo") : Lang.T("All In One - ekrandan metin");
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var whole = new Rectangle(Point.Empty, Frozen.Size);
        g.DrawImage(Frozen, whole, whole, GraphicsUnit.Pixel);
        using (var dim = new SolidBrush(System.Drawing.Color.FromArgb(110, 0, 0, 0))) g.FillRectangle(dim, whole);
        if (area.Width > 0 && area.Height > 0)
        {
            g.DrawImage(Frozen, area, area, GraphicsUnit.Pixel);
            g.DrawRectangle(Pens.White, area.X, area.Y, area.Width - 1, area.Height - 1);
        }
        Hint(g, (table ? Lang.T("Tabloyu sürükleyerek seç") : Lang.T("Metni alınacak alanı sürükle")) + Lang.T("  ·  Esc: vazgeç"));
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) { Close(); return; }
        from = e.Location;
        area = Rectangle.Empty;
        dragging = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!dragging) return;
        area = Between(from, e.Location, Frozen.Size);
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (!dragging || e.Button != MouseButtons.Left) return;
        dragging = false;
        if (area.Width < 8 || area.Height < 8) return; // a click: wait for a real selection
        var crop = Frozen.Clone(area, PixelFormat.Format32bppArgb);
        Close();
        Images.Start(() => { using (crop) return table ? RecognizeTable(crop) : Recognize(crop); });
    }

    // A table is shown for checking before it is copied.
    static Outcome RecognizeTable(Bitmap crop)
    {
        var picture = AsPicture(crop);
        var rows = TableText.Read(picture);
        if (rows.Length == 0) return new Outcome { Text = Lang.T("Seçilen alanda metin bulunamadı.") };
        App.Ui.BeginInvoke((Action)(() => TableWindow.Open(rows, picture.Pixels)));
        return null;
    }

    // The rectangle between two points, kept on the picture.
    public static Rectangle Between(Point a, Point b, Size size)
    {
        int left = Math.Max(0, Math.Min(a.X, b.X)), top = Math.Max(0, Math.Min(a.Y, b.Y));
        int right = Math.Min(size.Width, Math.Max(a.X, b.X)), bottom = Math.Min(size.Height, Math.Max(a.Y, b.Y));
        return new Rectangle(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }

    // Needs a single-threaded apartment for the clipboard.
    public static Outcome Recognize(Bitmap crop)
    {
        string text = Ocr.Read(AsPicture(crop), null);
        if (string.IsNullOrWhiteSpace(text)) return new Outcome { Text = Lang.T("Seçilen alanda metin bulunamadı.") };
        Ocr.ToClipboard(text);
        return Ocr.Copied(text.Split('\n')[0]);
    }

    // The selection with a margin of its own corner colour: the engine misses text that touches the edge.
    public static Picture AsPicture(Bitmap crop)
    {
        const int Margin = 32;
        using (var padded = new Bitmap(crop.Width + 2 * Margin, crop.Height + 2 * Margin, PixelFormat.Format32bppArgb))
        {
            using (var g = Graphics.FromImage(padded))
            {
                g.Clear(crop.GetPixel(0, 0));
                g.DrawImage(crop, new Rectangle(Margin, Margin, crop.Width, crop.Height),
                    new Rectangle(0, 0, crop.Width, crop.Height), GraphicsUnit.Pixel);
            }
            var locked = padded.LockBits(new Rectangle(Point.Empty, padded.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                var data = new byte[padded.Width * padded.Height * 4];
                Marshal.Copy(locked.Scan0, data, 0, data.Length);
                var pixels = BitmapSource.Create(padded.Width, padded.Height, 96, 96, PixelFormats.Bgra32, null, data, padded.Width * 4);
                pixels.Freeze();
                return new Picture { Pixels = pixels };
            }
            finally { padded.UnlockBits(locked); }
        }
    }
}

// A piece of the screen kept on top as a small window, to look at while working elsewhere.
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

class PinShot : Form
{
    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr window, int message, IntPtr w, IntPtr l);

    readonly Bitmap picture;
    double zoom = 1;

    PinShot(Bitmap picture, Rectangle at)
    {
        this.picture = picture;
        Text = Lang.T("All In One - sabit görüntü");
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        ShowInTaskbar = false;
        TopMost = true;
        DoubleBuffered = true;
        KeyPreview = true;
        Cursor = Cursors.SizeAll;
        MinimumSize = new Size(1, 1);
        Bounds = at;
        var menu = new ContextMenu(new[]
        {
            new MenuItem(Lang.T("Kopyala") + "\tCtrl+C", (s, e) => Copy()),
            new MenuItem(Lang.T("Kaydet") + "\tCtrl+S", (s, e) => Keep()),
            new MenuItem(Lang.T("Gerçek boyut"), (s, e) => Resize(1)),
            new MenuItem("-"),
            new MenuItem(Lang.T("Kapat") + "\tEsc", (s, e) => Close())
        });
        ContextMenu = menu;
    }

    // Shows it where it was taken from. screen: in real pixels.
    public static PinShot Show(Bitmap picture, Rectangle screen)
    {
        IntPtr awareness = Native.SetThreadDpiAwarenessContext(new IntPtr(-4));
        try
        {
            var shot = new PinShot(picture, screen);
            shot.Show();
            shot.Activate();
            return shot;
        }
        finally { Native.SetThreadDpiAwarenessContext(awareness); }
    }

    // The window's place after zooming: its middle stays, and it never gets tiny or huge.
    public static Rectangle Zoomed(Rectangle now, Size picture, double zoom)
    {
        var size = new Size(Math.Max(40, (int)Math.Round(picture.Width * zoom)), Math.Max(30, (int)Math.Round(picture.Height * zoom)));
        return new Rectangle(now.X + (now.Width - size.Width) / 2, now.Y + (now.Height - size.Height) / 2, size.Width, size.Height);
    }

    new void Resize(double wanted)
    {
        zoom = Math.Max(0.25, Math.Min(4, wanted));
        Bounds = Zoomed(Bounds, picture.Size, zoom);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        e.Graphics.PixelOffsetMode = PixelOffsetMode.Half;
        e.Graphics.DrawImage(picture, ClientRectangle);
        using (var pen = new Pen(Color.FromArgb(10, 132, 255), 2)) e.Graphics.DrawRectangle(pen, 1, 1, Width - 2, Height - 2);
    }

    // A drag moves the window, as if it were held by its title.
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        if (e.Clicks > 1) { Close(); return; }
        ReleaseCapture();
        SendMessage(Handle, 0xA1 /* WM_NCLBUTTONDOWN */, (IntPtr)2 /* HTCAPTION */, IntPtr.Zero);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        Resize(zoom * (e.Delta > 0 ? 1.1 : 1 / 1.1));
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == System.Windows.Forms.Keys.Escape) Close();
        else if (e.Control && e.KeyCode == System.Windows.Forms.Keys.C) Copy();
        else if (e.Control && e.KeyCode == System.Windows.Forms.Keys.S) Keep();
    }

    void Copy()
    {
        try { Clipboard.SetDataObject(picture, true, 10, 100); }
        catch (ExternalException e)
        {
            Log.Error("sabit görüntü panoya kopyalanamadı", e);
            Images.Notify(new Outcome { Text = Lang.T("Panoya kopyalanamadı") });
        }
    }

    void Keep()
    {
        var copy = (Bitmap)picture.Clone();
        Images.Start(() => { using (copy) return Snip.Save(copy, Captures.Pictures, false); });
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        base.OnFormClosed(e);
        picture.Dispose();
    }
}

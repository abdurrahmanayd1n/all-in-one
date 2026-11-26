// Alt+S: a picture of any part of the screen; or, chosen at the top, a video of it, a small window that stays on
// top, or a long picture of a page that scrolls.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Path = System.IO.Path;

class Snip : ScreenOverlay
{
    public enum Part { None, Picture, Film, Pin, Scroll, Whole, Close, Sound, Mic }

    [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(Point point, uint flags);
    [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint x, out uint y);

    Part mode = Part.Picture; // what a chosen area becomes
    bool dragging;
    Point from;
    Rectangle area;
    Part hover;
    readonly Rectangle monitor; // the one under the pointer, in the overlay's own coordinates
    readonly double scale;
    readonly Dictionary<Part, Rectangle> buttons, toggles; // the toggles show only for a video
    readonly Rectangle bar, soundBar;

    // The shortcut: opens the overlay, or ends the recording that is running.
    public static void Toggle()
    {
        if (Recorder.Running) Recorder.Stop();
        else if (Scroller.Running) Scroller.Stop();
        else Open(screen => new Snip(screen), null);
    }

    Snip(Rectangle screen) : base(screen)
    {
        Text = Lang.T("All In One - ekran görüntüsü");
        var pointer = Cursor.Position;
        var own = Screen.FromPoint(pointer).Bounds;
        scale = ScaleAt(pointer);
        monitor = new Rectangle(own.X - screen.X, own.Y - screen.Y, own.Width, own.Height);
        buttons = Buttons(monitor, scale, out bar);
        toggles = Toggles(monitor, bar, scale, out soundBar);
    }

    // How much Windows enlarges things on the monitor at this point.
    public static double ScaleAt(Point point)
    {
        uint x, y;
        return GetDpiForMonitor(MonitorFromPoint(point, 2 /* nearest */), 0, out x, out y) == 0 ? x / 96.0 : 1;
    }

    // The buttons in a bar at the top middle of the monitor.
    public static Dictionary<Part, Rectangle> Buttons(Rectangle monitor, double scale, out Rectangle bar)
    {
        return Row(monitor, scale, monitor.Y + (int)Math.Round(16 * scale), out bar, Tuple.Create(Part.Picture, 112),
            Tuple.Create(Part.Film, 94), Tuple.Create(Part.Pin, 108), Tuple.Create(Part.Scroll, 100), Tuple.Create(Part.Whole, 124),
            Tuple.Create(Part.Close, 44));
    }

    // What is recorded with a video, in a second bar under the first.
    public static Dictionary<Part, Rectangle> Toggles(Rectangle monitor, Rectangle above, double scale, out Rectangle bar)
    {
        return Row(monitor, scale, above.Bottom + (int)Math.Round(8 * scale), out bar, Tuple.Create(Part.Sound, 170),
            Tuple.Create(Part.Mic, 170));
    }

    static Dictionary<Part, Rectangle> Row(Rectangle monitor, double scale, int top, out Rectangle bar,
        params Tuple<Part, int>[] widths)
    {
        Func<double, int> s = v => (int)Math.Round(v * scale);
        int gap = s(6), height = s(40), total = widths.Sum(w => s(w.Item2)) + gap * (widths.Length + 1);
        bar = new Rectangle(monitor.X + (monitor.Width - total) / 2, top, total, height + 2 * gap);
        var result = new Dictionary<Part, Rectangle>();
        int x = bar.X + gap;
        foreach (var each in widths)
        {
            result[each.Item1] = new Rectangle(x, bar.Y + gap, s(each.Item2), height);
            x += s(each.Item2) + gap;
        }
        return result;
    }

    Part At(Point point)
    {
        var found = buttons.Where(b => b.Value.Contains(point)).Select(b => b.Key).FirstOrDefault();
        if (found != Part.None || mode != Part.Film) return found;
        return toggles.Where(b => b.Value.Contains(point)).Select(b => b.Key).FirstOrDefault();
    }

    bool OnBars(Point point) { return bar.Contains(point) || mode == Part.Film && soundBar.Contains(point); }

    static string Glyph(Part part)
    {
        switch (part)
        {
            case Part.Picture: return "\uE722";
            case Part.Film: return "\uE714";
            case Part.Pin: return "\uE718";
            case Part.Scroll: return "\uE8CB";
            case Part.Whole: return "\uE740";
            case Part.Sound: return "\uE767";
            case Part.Mic: return "\uE720";
            default: return "\uE711";
        }
    }

    static string Label(Part part)
    {
        switch (part)
        {
            case Part.Picture: return Lang.T("Görüntü");
            case Part.Film: return Lang.T("Video");
            case Part.Pin: return Lang.T("Sabitle");
            case Part.Scroll: return Lang.T("Kayan");
            case Part.Whole: return Lang.T("Tüm ekran");
            case Part.Sound: return Recorder.WithSound ? Lang.T("Ses: açık") : Lang.T("Ses: kapalı");
            case Part.Mic: return Recorder.WithMicrophone ? Lang.T("Mikrofon: açık") : Lang.T("Mikrofon: kapalı");
            default: return "";
        }
    }

    string Told()
    {
        if (mode == Part.Film)
            return Images.Ffmpeg == null ? Lang.T("Video için FFmpeg gerekli") : Lang.T("Kaydedilecek alanı sürükle");
        if (mode == Part.Pin) return Lang.T("Ekranda tutulacak alanı sürükle");
        if (mode == Part.Scroll) return Lang.T("Yalnız kayan kısmı sürükle; sayfa kendisi kaydırılır");
        return Lang.T("Alanı sürükle: panoya kopyalanır ve kaydedilir");
    }

    public static GraphicsPath Round(Rectangle r, int radius)
    {
        var path = new GraphicsPath();
        int d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var whole = new Rectangle(Point.Empty, Frozen.Size);
        g.DrawImage(Frozen, whole, whole, GraphicsUnit.Pixel);
        using (var dim = new SolidBrush(Color.FromArgb(100, 0, 0, 0))) g.FillRectangle(dim, whole);
        float font = (float)(14 * scale);
        using (var text = new Font("Segoe UI", font, GraphicsUnit.Pixel))
        using (var icons = new Font("Segoe MDL2 Assets", (float)(16 * scale), GraphicsUnit.Pixel))
        using (var back = new SolidBrush(Color.FromArgb(240, 27, 28, 35)))
        using (var centred = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
        {
            var colour = mode == Part.Film ? Color.FromArgb(215, 58, 49) : Color.FromArgb(10, 132, 255);
            if (area.Width > 0 && area.Height > 0)
            {
                g.DrawImage(Frozen, area, area, GraphicsUnit.Pixel);
                using (var pen = new Pen(colour, (float)(2 * scale))) g.DrawRectangle(pen, area);
                string size = area.Width + " × " + area.Height;
                var measured = g.MeasureString(size, text);
                var tag = new Rectangle(area.X, area.Bottom + 8, (int)measured.Width + 16, (int)measured.Height + 8);
                if (tag.Bottom > Frozen.Height) tag.Y = area.Bottom - tag.Height - 8;
                using (var path = Round(tag, (int)(6 * scale))) g.FillPath(back, path);
                g.DrawString(size, text, Brushes.White, tag, centred);
            }
            if (dragging) return; // the bars step aside while a part is being chosen
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Action<Rectangle, Dictionary<Part, Rectangle>> draw = (frame, row) =>
            {
                using (var path = Round(frame, (int)(12 * scale))) g.FillPath(back, path);
                foreach (var button in row)
                {
                    bool on = button.Key == Part.Sound && Recorder.WithSound || button.Key == Part.Mic && Recorder.WithMicrophone;
                    var fill = button.Key == mode ? colour : on ? Color.FromArgb(46, 110, 70)
                        : button.Key == hover ? Color.FromArgb(54, 57, 69) : Color.Transparent;
                    using (var brush = new SolidBrush(fill))
                    using (var path = Round(button.Value, (int)(8 * scale))) g.FillPath(brush, path);
                    string label = Label(button.Key);
                    var r = button.Value;
                    if (label == "") g.DrawString(Glyph(button.Key), icons, Brushes.White, r, centred);
                    else
                    {
                        // The icon and the word together in the middle of the button.
                        int left = (int)(24 * scale), word = (int)Math.Ceiling(g.MeasureString(label, text).Width);
                        int x = r.X + (r.Width - left - word) / 2;
                        g.DrawString(Glyph(button.Key), icons, Brushes.White, new Rectangle(x, r.Y, left, r.Height), centred);
                        g.DrawString(label, text, Brushes.White, new Rectangle(x + left, r.Y, word + 2, r.Height), centred);
                    }
                }
            };
            draw(bar, buttons);
            if (mode == Part.Film) draw(soundBar, toggles);
            string hint = Told() + Lang.T("  ·  Tab: sonraki tür  ·  Enter: tüm ekran  ·  Esc");
            var needed = g.MeasureString(hint, text);
            var below = new Rectangle(monitor.X + (monitor.Width - (int)needed.Width - 24) / 2,
                (mode == Part.Film ? soundBar : bar).Bottom + 8, (int)needed.Width + 24, (int)needed.Height + 10);
            using (var path = Round(below, (int)(8 * scale))) g.FillPath(back, path);
            g.DrawString(hint, text, Brushes.Gainsboro, below, centred);
        }
    }

    void Choose(Part part)
    {
        mode = part;
        Invalidate();
    }

    void Next() { Choose(mode == Part.Scroll ? Part.Picture : mode + 1); }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == System.Windows.Forms.Keys.Tab) Next();
        else if (e.KeyCode == System.Windows.Forms.Keys.Enter) Take(monitor);
        else base.OnKeyDown(e);
    }

    protected override bool ProcessDialogKey(System.Windows.Forms.Keys keys)
    {
        if (keys != System.Windows.Forms.Keys.Tab) return base.ProcessDialogKey(keys);
        Next();
        return true;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) { Close(); return; }
        var part = At(e.Location);
        if (part == Part.Whole) Take(monitor);
        else if (part == Part.Close) Close();
        else if (part == Part.Sound || part == Part.Mic)
        {
            if (part == Part.Sound) Recorder.WithSound = !Recorder.WithSound;
            else Recorder.WithMicrophone = !Recorder.WithMicrophone;
            Invalidate();
        }
        else if (part != Part.None) Choose(part);
        else if (!OnBars(e.Location))
        {
            from = e.Location;
            area = Rectangle.Empty;
            dragging = true;
            Invalidate();
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (dragging)
        {
            area = ScreenText.Between(from, e.Location, Frozen.Size);
            Invalidate();
            return;
        }
        var part = At(e.Location);
        Cursor = OnBars(e.Location) ? Cursors.Default : Cursors.Cross;
        if (part == hover) return;
        hover = part;
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (!dragging || e.Button != MouseButtons.Left) return;
        dragging = false;
        if (area.Width >= 8 && area.Height >= 8) Take(area);
        else
        {
            area = Rectangle.Empty; // a click: wait for a real selection
            Invalidate();
        }
    }

    void Take(Rectangle part)
    {
        part.Intersect(new Rectangle(Point.Empty, Frozen.Size));
        var onScreen = new Rectangle(part.X + Bounds.X, part.Y + Bounds.Y, part.Width, part.Height);
        if (mode == Part.Film)
        {
            if (Images.Ffmpeg == null) return;
            Close();
            Recorder.Start(onScreen);
        }
        else if (mode == Part.Scroll)
        {
            if (part.Height < 100) return; // too low to tell how far it moved
            // Once the overlay is gone, so that the first picture shows the page itself.
            var wait = new Timer { Interval = 300 };
            wait.Tick += (s, e) => { wait.Dispose(); Scroller.Start(onScreen); };
            Close();
            wait.Start();
        }
        else
        {
            var crop = Frozen.Clone(part, PixelFormat.Format32bppArgb);
            Close();
            if (mode == Part.Pin) PinShot.Show(crop, onScreen);
            else Images.Start(() => { using (crop) return Save(crop, Captures.Pictures); });
        }
    }

    // Saves the picture and puts it on the clipboard. Needs a single-threaded apartment.
    public static Outcome Save(Bitmap crop, string folder, bool clipboard = true)
    {
        string file = Captures.Free(folder, Lang.T("Ekran"), ".png");
        crop.Save(file, ImageFormat.Png);
        string copied = Lang.T("Panoya kopyalandı");
        try { if (clipboard) Clipboard.SetDataObject(crop, true, 10, 100); }
        catch (ExternalException e)
        {
            Log.Error("ekran görüntüsü panoya kopyalanamadı", e);
            copied = Lang.T("Panoya kopyalanamadı");
        }
        return new Outcome { File = file, Bytes = new FileInfo(file).Length,
            Text = Path.GetFileName(file) + "\n" + copied + " · " + crop.Width + " × " + crop.Height };
    }
}

static class Captures
{
    public static string Pictures
    {
        get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "All In One"); }
    }

    public static string Videos
    {
        get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "All In One"); }
    }

    // A name with the moment in it that no file has yet.
    public static string Free(string folder, string kind, string ext)
    {
        Directory.CreateDirectory(folder);
        string stem = kind + " " + DateTime.Now.ToString("yyyy-MM-dd HH.mm.ss");
        for (int i = 1; ; i++)
        {
            string file = Path.Combine(folder, stem + (i == 1 ? "" : " (" + i + ")") + ext);
            if (!File.Exists(file)) return file;
        }
    }
}

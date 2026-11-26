// A scrolling picture: the chosen part is photographed, scrolled with the mouse wheel and photographed again,
// and the new rows are added below until the page stops moving.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

// Finds how far the content moved between two pictures. A picture is given as one short signature for each row.
// Rows that stay put at the top or bottom (a fixed header or footer) are recognised and added only once.
class Stitch
{
    byte[][] last;
    int end, step; // how many rows of the last picture are in the result; how far the content moved last time

    public enum Found { Moved, Still, Lost }

    public Stitch(byte[][] first)
    {
        last = first;
        end = first.Length;
    }

    static int Difference(byte[] a, byte[] b)
    {
        int sum = 0;
        for (int i = 0; i < a.Length; i++) sum += Math.Abs(a[i] - b[i]);
        return sum;
    }

    // Moved: rows from..to of the new picture follow the result, after "drop" rows are taken off its end.
    public Found Add(byte[][] next, out int drop, out int from, out int to)
    {
        drop = from = to = 0;
        int h = next.Length, width = next[0].Length;
        if (h != last.Length) return Found.Lost;
        int top = 0, bottom = 0;
        while (top < h && Difference(last[top], next[top]) <= width) top++;
        if (top == h) return Found.Still;
        while (bottom < h && Difference(last[h - 1 - bottom], next[h - 1 - bottom]) <= width) bottom++;
        top = Math.Min(top, h / 3);
        bottom = Math.Min(bottom, h / 3);
        int room = h - top - bottom, least = Math.Max(8, room / 8);

        // The shift whose overlapping rows differ least; among equals the one closest to the previous.
        var scores = new List<Tuple<int, double>>();
        double best = double.MaxValue;
        for (int shift = 1; shift <= room - least; shift++)
        {
            int rows = room - shift;
            double limit = Math.Min(best + 0.5, 6) * rows * width;
            long sum = 0;
            for (int y = 0; y < rows && sum <= limit; y++) sum += Difference(last[top + shift + y], next[top + y]);
            if (sum > limit) continue;
            double score = (double)sum / rows / width;
            scores.Add(Tuple.Create(shift, score));
            best = Math.Min(best, score);
        }
        if (best > 4) return Found.Lost;
        int moved = scores.Where(s => s.Item2 <= best + 0.3).OrderBy(s => step > 0 ? Math.Abs(s.Item1 - step) : s.Item1).First().Item1;

        // The rows at the bottom that stayed put are not content: they come off the result again.
        to = h - bottom;
        drop = Math.Max(0, end - to);
        from = end - drop - moved; // the row of the new picture where the result ends
        end = to;
        step = moved;
        last = next;
        return Found.Moved;
    }

    // The rows of the last picture that never entered the result: its fixed footer.
    public int Rest { get { return end; } }
}

// The picture being put together, piece below piece.
class LongPicture : IDisposable
{
    readonly List<Bitmap> pieces = new List<Bitmap>();
    readonly List<int> heights = new List<int>(); // how much of each piece counts
    public int Height { get { return heights.Sum(); } }

    public void Add(Bitmap source, int from, int to)
    {
        if (to <= from) return;
        pieces.Add(source.Clone(new Rectangle(0, from, source.Width, to - from), PixelFormat.Format32bppArgb));
        heights.Add(to - from);
    }

    public void Drop(int rows)
    {
        for (int i = heights.Count - 1; i >= 0 && rows > 0; i--)
        {
            int taken = Math.Min(rows, heights[i]);
            heights[i] -= taken;
            rows -= taken;
        }
    }

    public Bitmap Compose()
    {
        var whole = new Bitmap(pieces[0].Width, Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(whole))
            for (int i = 0, y = 0; i < pieces.Count; y += heights[i], i++)
                if (heights[i] > 0)
                    g.DrawImage(pieces[i], new Rectangle(0, y, whole.Width, heights[i]),
                        new Rectangle(0, 0, whole.Width, heights[i]), GraphicsUnit.Pixel);
        return whole;
    }

    public void Dispose()
    {
        foreach (var piece in pieces) piece.Dispose();
        pieces.Clear();
        heights.Clear();
    }
}

static class Scroller
{
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] static extern void mouse_event(uint flags, int dx, int dy, int data, UIntPtr extra);

    const int Longest = 25000, Samples = 96;

    static Timer tick;
    static Marker marker;
    static LongPicture result;
    static Stitch stitch;
    static Bitmap latest;
    static Rectangle part;
    static Point pointer;
    static int still, lost;

    public static bool Running { get { return tick != null; } }

    // A short signature of every row: grey values along it, leaving out the right edge where a scroll bar moves.
    public static byte[][] Rows(Bitmap picture)
    {
        int w = picture.Width, h = picture.Height, used = Math.Max(1, w - Math.Max(24, w / 25));
        var locked = picture.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var line = new byte[w * 4];
            var rows = new byte[h][];
            for (int y = 0; y < h; y++)
            {
                Marshal.Copy(locked.Scan0 + y * locked.Stride, line, 0, line.Length);
                rows[y] = new byte[Samples];
                for (int i = 0; i < Samples; i++)
                {
                    // The mean of a short stretch, so that thin strokes count wherever they fall.
                    int a = i * used / Samples, b = Math.Max(a + 1, (i + 1) * used / Samples), sum = 0;
                    for (int x = a; x < b; x++) sum += line[x * 4] + line[x * 4 + 1] + line[x * 4 + 2];
                    rows[y][i] = (byte)(sum / (3 * (b - a)));
                }
            }
            return rows;
        }
        finally { picture.UnlockBits(locked); }
    }

    static Bitmap Take()
    {
        IntPtr awareness = Native.SetThreadDpiAwarenessContext(new IntPtr(-4));
        try
        {
            var picture = new Bitmap(part.Width, part.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(picture)) g.CopyFromScreen(part.Location, Point.Empty, part.Size);
            return picture;
        }
        finally { Native.SetThreadDpiAwarenessContext(awareness); }
    }

    // area: in real screen pixels.
    public static void Start(Rectangle area)
    {
        if (Running) return;
        part = area;
        try
        {
            latest = Take();
            result = new LongPicture();
            result.Add(latest, 0, latest.Height);
            stitch = new Stitch(Rows(latest));
        }
        catch (Exception e)
        {
            Log.Error("kayan görüntü başlatılamadı", e);
            MessageBox.Show(Lang.T("Ekran alınamadı: ") + e.Message, "All In One");
            return;
        }
        still = lost = 0;
        marker = new Marker(area, Color.FromArgb(10, 132, 255), Lang.T("■  Bitir"), Stop);
        marker.Text = Lang.T("Kaydırılıyor…");
        IntPtr awareness = Native.SetThreadDpiAwarenessContext(new IntPtr(-4));
        GetCursorPos(out pointer);
        Native.SetThreadDpiAwarenessContext(awareness);
        tick = new Timer { Interval = 550 }; // long enough for smooth scrolling to settle
        tick.Tick += (s, e) => Step();
        tick.Start();
        Wheel();
    }

    // One notch for a low part, so that pictures keep overlapping; two for a tall one. The pointer is put on the
    // part every time: the wheel goes to whatever lies under it, also after the mouse has been nudged.
    static void Wheel()
    {
        IntPtr awareness = Native.SetThreadDpiAwarenessContext(new IntPtr(-4));
        SetCursorPos(part.X + part.Width / 2, part.Y + part.Height / 2);
        Native.SetThreadDpiAwarenessContext(awareness);
        mouse_event(0x800 /* wheel */, 0, 0, part.Height < 500 ? -120 : -240, UIntPtr.Zero);
    }

    static void Step()
    {
        try
        {
            if (Native.IsDown(0x1B)) { Stop(); return; }
            var now = Take();
            int drop, from, to;
            var found = stitch.Add(Rows(now), out drop, out from, out to);
            if (found == Stitch.Found.Moved)
            {
                result.Drop(drop);
                result.Add(now, from, to);
                latest.Dispose();
                latest = now;
                still = lost = 0;
                marker.Text = result.Height + " px";
                if (result.Height >= Longest) { Stop(); return; }
                Wheel();
                return;
            }
            now.Dispose();
            // The end of the page, or something that moves on its own: one or two more tries.
            if (found == Stitch.Found.Still ? ++still >= 3 : ++lost >= 4) Stop();
            else if (found == Stitch.Found.Still) Wheel();
        }
        catch (Exception e)
        {
            Log.Error("kayan görüntü", e);
            Stop();
        }
    }

    public static void Stop()
    {
        if (!Running) return;
        tick.Dispose();
        tick = null;
        marker.Dispose();
        IntPtr awareness = Native.SetThreadDpiAwarenessContext(new IntPtr(-4));
        SetCursorPos(pointer.X, pointer.Y);
        Native.SetThreadDpiAwarenessContext(awareness);
        result.Add(latest, stitch.Rest, latest.Height);
        var whole = result.Compose();
        result.Dispose();
        latest.Dispose();
        Images.Start(() => { using (whole) return Snip.Save(whole, Captures.Pictures); });
    }
}

// Dark glass radial menu: a main ring plus an outer ring for the hovered item's options.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Drawing = System.Drawing;
using Screen = System.Windows.Forms.Screen;
using FormsCursor = System.Windows.Forms.Cursor;

class Item
{
    public string Label, Icon, Hint;
    public Action Run;
    public Item[] Children;
}

class RadialMenu : Window
{
    // All sizes in device independent units.
    const double Side = 480, C = Side / 2;
    const double Dead = 56, Inner = 60, Outer = 148, Keep = 118, SubInner = 154, SubOuter = 220;
    const double SubStep = 28;

    class Parts { public UIElement Marker; public ScaleTransform Zoom; }

    // One ring: its labels and a single highlight that glides between them.
    class Ring
    {
        public readonly Canvas Layer = new Canvas();
        public readonly List<Parts> Segments = new List<Parts>();
        public readonly RotateTransform Turn = new RotateTransform(0, C, C);
        public System.Windows.Shapes.Path Pill;
        public double Step;
    }

    readonly double scale;
    readonly Grid root = new Grid { Width = Side, Height = Side, Opacity = 0 };
    readonly ScaleTransform zoom = new ScaleTransform(1, 1, C, C);
    readonly Canvas backing = new Canvas(), host = new Canvas();
    Ring main, sub;
    TextBlock heading, caption;
    StackPanel center;
    Item[] items = new Item[0];
    Brush backdrop;
    string title, detail;
    int hover = -1, child = -1, token;
    Drawing.Point middle;

    public event Action Clicked;

    public RadialMenu()
    {
        using (var g = Drawing.Graphics.FromHwnd(IntPtr.Zero)) scale = g.DpiX / 96.0;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        Width = Height = Side;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        root.RenderTransform = zoom;
        root.Children.Add(backing);
        root.Children.Add(host);
        Content = root;
        MouseLeftButtonUp += (s, e) => { if (Clicked != null) Clicked(); };
        var handle = new WindowInteropHelper(this).EnsureHandle(); // created early: first open is faster
        Native.SetWindowLong(handle, -20, Native.GetWindowLong(handle, -20) | 0x08000000 | 0x80); // NOACTIVATE | TOOLWINDOW
    }

    // Item 0 is centred at the top, numbered clockwise. Returns -1 in the dead zone.
    // childCounts holds the number of outer ring options per item, open is the item whose ring is showing.
    public static int Hit(double dx, double dy, int[] childCounts, int open, out int child)
    {
        child = -1;
        double radius = Math.Sqrt(dx * dx + dy * dy);
        if (radius < Dead) return -1;
        double angle = (Math.Atan2(dx, -dy) * 180 / Math.PI + 360) % 360;
        double step = 360.0 / childCounts.Length;
        if (open >= 0 && childCounts[open] > 0 && radius >= Keep)
        {
            double relative = (angle - open * step + 540) % 360 - 180;
            double span = childCounts[open] * SubStep;
            // Stay on the open item while heading for its options, even across a neighbour's sector.
            if (Math.Abs(relative) < span / 2)
            {
                if (radius >= SubInner) child = (int)((relative + span / 2) / SubStep);
                return open;
            }
        }
        return (int)((angle + step / 2) / step) % childCounts.Length;
    }

    public void Open(Drawing.Point at, Item[] menuItems, string name, string description)
    {
        int mine = ++token;
        Hide();
        items = menuItems;
        title = name;
        detail = description;
        hover = child = -1;

        int side = (int)(Side * scale);
        var area = Screen.FromPoint(at).Bounds;
        var placed = new Drawing.Point(
            Math.Max(area.Left, Math.Min(at.X - side / 2, area.Right - side)),
            Math.Max(area.Top, Math.Min(at.Y - side / 2, area.Bottom - side)));
        backdrop = Blurred(placed, side); // captured while the menu is hidden
        middle = new Drawing.Point(placed.X + side / 2, placed.Y + side / 2);
        // Keep the cursor in the dead zone, also when the menu was pushed in from a screen edge.
        FormsCursor.Position = middle;
        Build();

        root.BeginAnimation(OpacityProperty, null);
        zoom.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        zoom.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        root.Opacity = 0;
        zoom.ScaleX = zoom.ScaleY = 0.9;
        Left = placed.X / scale;
        Top = placed.Y / scale;
        Show();
        // Showing without activation keeps the old z-order: raise above other topmost windows every time.
        Native.SetWindowPos(new WindowInteropHelper(this).Handle, (IntPtr)(-1), placed.X, placed.Y, 0, 0, 0x11 /* NOSIZE | NOACTIVATE */);
        // Start only once the window has painted, otherwise the first part of the motion is never seen.
        AfterFirstFrames(() =>
        {
            if (mine != token) return;
            root.CacheMode = new BitmapCache(); // scale a bitmap instead of redrawing every shape per frame
            Animate(root, OpacityProperty, 0, 1, 240);
            Animate(zoom, ScaleTransform.ScaleXProperty, 0.9, 1, 340);
            Animate(zoom, ScaleTransform.ScaleYProperty, 0.9, 1, 340, () => root.CacheMode = null);
        });
    }

    public void Dismiss()
    {
        if (!IsVisible) return;
        int mine = ++token;
        root.CacheMode = new BitmapCache();
        Animate(zoom, ScaleTransform.ScaleXProperty, null, 0.95, 200);
        Animate(zoom, ScaleTransform.ScaleYProperty, null, 0.95, 200);
        Animate(root, OpacityProperty, null, 0, 200, () => { if (mine == token) Hide(); });
    }

    // Renders one menu far off screen at startup: the first WPF window and screen capture are slow.
    public void WarmUp()
    {
        int mine = ++token;
        backdrop = Blurred(new Drawing.Point(0, 0), (int)(Side * scale));
        items = new[] { new Item { Label = " ", Icon = "", Children = new[] { new Item { Label = " " } } } };
        title = detail = " ";
        Build();
        hover = 0;
        BuildSub();
        hover = -1;
        root.Opacity = 1;
        Left = Top = -20000;
        Show();
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle,
            (Action)(() => { if (mine == token) Hide(); }));
    }

    public void Track()
    {
        var p = FormsCursor.Position;
        Aim((p.X - middle.X) / scale, (p.Y - middle.Y) / scale);
    }

    // Updates the highlight for a pointer position relative to the centre.
    void Aim(double dx, double dy)
    {
        var counts = new int[items.Length];
        for (int i = 0; i < items.Length; i++)
            counts[i] = items[i].Children != null ? items[i].Children.Length : 0;
        int newChild;
        int now = Hit(dx, dy, counts, hover, out newChild);
        if (now == hover && newChild == child) return;

        if (now != hover)
        {
            Move(main, hover, now);
            hover = now;
            child = -1;
            BuildSub();
        }
        if (newChild != child)
        {
            Move(sub, child, newChild);
            child = newChild;
        }
        var selected = Selected();
        if (selected == null) Describe(title, detail);
        else Describe(selected.Label.Replace("\n", " "), selected.Hint);
    }

    Item Selected()
    {
        if (hover < 0) return null;
        return child < 0 ? items[hover] : items[hover].Children[child];
    }

    // Returns the chosen action, or null when nothing runnable is selected.
    public Action Choose()
    {
        Track();
        var selected = Selected();
        return selected == null ? null : selected.Run;
    }

    // The centre always says what releasing Alt would do.
    void Describe(string top, string bottom)
    {
        if (heading.Text == top && caption.Text == (bottom ?? "")) return;
        heading.Text = top;
        caption.Text = bottom ?? "";
        caption.Visibility = caption.Text == "" ? Visibility.Collapsed : Visibility.Visible;
        Animate(center, OpacityProperty, 0, 1, 220);
    }

    void Build()
    {
        backing.Children.Clear();
        host.Children.Clear();
        sub = null;
        double step = 360.0 / items.Length;

        // Soft shadow, dropped a little: the light comes from above.
        var shadow = new RadialGradientBrush { GradientStops = new GradientStopCollection {
            new GradientStop(Color.FromArgb(130, 0, 0, 0), 0), new GradientStop(Color.FromArgb(105, 0, 0, 0), 0.72),
            // Fade to transparent black: Colors.Transparent is white and would leave a pale halo.
            new GradientStop(Color.FromArgb(35, 0, 0, 0), 0.86), new GradientStop(Color.FromArgb(0, 0, 0, 0), 1) } };
        Place(backing, new Ellipse { Width = 376, Height = 376, Fill = shadow }, C - 188, C - 178);

        Glass(backing, Circle(Outer)); // full disc: a see-through gap would show sharp content inside the glass
        for (int i = 0; i < items.Length; i++)
        {
            var edge = (i + 0.5) * step;
            var from = Polar(Inner + 14, edge);
            var to = Polar(Outer - 14, edge);
            backing.Children.Add(new Line { X1 = from.X, Y1 = from.Y, X2 = to.X, Y2 = to.Y, Stroke = Solid(22, 255, 255, 255), StrokeThickness = 1 });
        }
        main = NewRing(Rounded(Inner + 5, Outer - 5, -step / 2, step, 12, 2.5), step);
        for (int i = 0; i < items.Length; i++)
            main.Segments.Add(Segment(main.Layer, items[i], i * step, Outer, 104));
        host.Children.Add(main.Layer);

        // The centre is a darker well, so its text stays readable over anything.
        var well = Brushes.Black.Clone();
        well.Opacity = 0.32;
        main.Layer.Children.Add(new System.Windows.Shapes.Path { Data = Circle(Dead - 2), Fill = well, Stroke = Rim(60, 12), StrokeThickness = 1 });

        heading = Text(title, 12.5, 92);
        heading.FontWeight = FontWeights.SemiBold;
        heading.MaxHeight = 34;
        caption = Text(detail ?? "", 10.5, 88);
        caption.Foreground = Solid(185, 255, 255, 255);
        caption.Margin = new Thickness(0, 3, 0, 0);
        caption.MaxHeight = 30;
        center = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        center.Children.Add(heading);
        center.Children.Add(caption);
        var holder = new Grid { Width = 100, Height = 100 };
        holder.Children.Add(center);
        Place(main.Layer, holder, C - 50, C - 50);
    }

    Ring NewRing(Geometry pill, double step)
    {
        var ring = new Ring { Step = step };
        // A lighter pane of frosted glass. The shape turns, not the element, so its light keeps coming from above.
        var shape = pill.Clone();
        shape.Transform = ring.Turn;
        var frost = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        frost.GradientStops.Add(new GradientStop(Color.FromArgb(78, 255, 255, 255), 0));
        frost.GradientStops.Add(new GradientStop(Color.FromArgb(34, 255, 255, 255), 0.5));
        frost.GradientStops.Add(new GradientStop(Color.FromArgb(48, 225, 232, 255), 1));
        ring.Pill = new System.Windows.Shapes.Path { Data = shape, Fill = frost, Stroke = Rim(180, 55), StrokeThickness = 1.2, Opacity = 0 };
        ring.Layer.Children.Add(ring.Pill);
        return ring;
    }

    // The outer rings cross-fade: the old one leaves while the new one grows out of the menu.
    void BuildSub()
    {
        var old = sub;
        sub = null;
        if (old != null) Animate(old.Layer, OpacityProperty, null, 0, 200, () => host.Children.Remove(old.Layer));
        if (hover < 0 || items[hover].Children == null) return;

        var children = items[hover].Children;
        double parent = hover * 360.0 / items.Length, span = children.Length * SubStep, first = parent - span / 2 + SubStep / 2;
        var ring = NewRing(Rounded(SubInner + 5, SubOuter - 5, first - SubStep / 2, SubStep, 10, 2.5), SubStep);
        ring.Layer.Children.Clear();
        Glass(ring.Layer, Rounded(SubInner, SubOuter, parent - span / 2, span, 16, 0));
        ring.Layer.Children.Add(ring.Pill);
        for (int i = 0; i < children.Length; i++)
            ring.Segments.Add(Segment(ring.Layer, children[i], first + i * SubStep, SubOuter, 187));
        sub = ring;
        host.Children.Add(ring.Layer);

        var grow = new ScaleTransform(1, 1, C, C);
        ring.Layer.RenderTransform = grow;
        Animate(ring.Layer, OpacityProperty, 0, 1, 260);
        Animate(grow, ScaleTransform.ScaleXProperty, 0.92, 1, 340);
        Animate(grow, ScaleTransform.ScaleYProperty, 0.92, 1, 340);
    }

    // Frosted layer: blurred screen, dark tint, a faint sheen and a rim that is brightest on top.
    void Glass(Canvas on, Geometry shape)
    {
        if (backdrop != null) on.Children.Add(new System.Windows.Shapes.Path { Data = shape, Fill = backdrop });
        on.Children.Add(new System.Windows.Shapes.Path { Data = shape, Fill = Solid(backdrop != null ? 178 : 248, 22, 22, 27) });
        var sheen = new LinearGradientBrush(Color.FromArgb(26, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 90);
        on.Children.Add(new System.Windows.Shapes.Path { Data = shape, Fill = sheen, Stroke = Rim(105, 24), StrokeThickness = 1 });
    }

    static Brush Rim(byte top, byte bottom)
    {
        return new LinearGradientBrush(Color.FromArgb(top, 255, 255, 255), Color.FromArgb(bottom, 255, 255, 255), 90);
    }

    Parts Segment(Canvas on, Item item, double angle, double outer, double labelRadius)
    {
        // A dot marks items that open an outer ring.
        Ellipse marker = null;
        if (item.Children != null)
        {
            marker = new Ellipse { Width = 4, Height = 4, Fill = Brushes.White, Opacity = 0.4 };
            var spot = Polar(outer - 8, angle);
            Place(on, marker, spot.X - 2, spot.Y - 2);
        }

        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        if (item.Icon != null)
        {
            var icon = Text(item.Icon, 20, 84);
            icon.FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
            icon.Margin = new Thickness(0, 0, 0, 5);
            stack.Children.Add(icon);
        }
        var label = Text(item.Label, item.Icon != null ? 11.5 : 12.5, 84);
        label.FontWeight = item.Icon != null ? FontWeights.Normal : FontWeights.SemiBold;
        label.LineHeight = 14.5;
        stack.Children.Add(label);
        var scaling = new ScaleTransform(1, 1, 42, 32);
        var box = new Grid { Width = 84, Height = 64, RenderTransform = scaling };
        box.Children.Add(stack);
        var at = Polar(labelRadius, angle);
        Place(on, box, at.X - 42, at.Y - 32);
        return new Parts { Marker = marker, Zoom = scaling };
    }

    // Glides the highlight the short way round; it only fades in or out when entering or leaving the ring.
    static void Move(Ring ring, int from, int to)
    {
        if (ring == null) return;
        if (from >= 0 && from < ring.Segments.Count) Emphasize(ring.Segments[from], false);
        if (to < 0)
        {
            Animate(ring.Pill, OpacityProperty, null, 0, 260);
            return;
        }
        Emphasize(ring.Segments[to], true);
        double target = to * ring.Step;
        if (ring.Pill.Opacity < 0.05)
        {
            ring.Turn.BeginAnimation(RotateTransform.AngleProperty, null);
            ring.Turn.Angle = target;
        }
        else
        {
            double now = ring.Turn.Angle;
            Animate(ring.Turn, RotateTransform.AngleProperty, null, now + ((target - now) % 360 + 540) % 360 - 180, 260);
        }
        Animate(ring.Pill, OpacityProperty, null, 1, 160);
    }

    static void Emphasize(Parts v, bool on)
    {
        if (v.Marker != null) Animate(v.Marker, OpacityProperty, null, on ? 0 : 0.4, 200); // the open ring replaces the dot
        Animate(v.Zoom, ScaleTransform.ScaleXProperty, null, on ? 1.06 : 1, 240);
        Animate(v.Zoom, ScaleTransform.ScaleYProperty, null, on ? 1.06 : 1, 240);
    }

    static void AfterFirstFrames(Action action)
    {
        int frames = 0;
        EventHandler rendering = null;
        rendering = (s, e) =>
        {
            if (++frames < 2) return;
            CompositionTarget.Rendering -= rendering;
            action();
        };
        CompositionTarget.Rendering += rendering;
    }

    public static void Animate(IAnimatable target, DependencyProperty property, double? from, double to, int ms, Action done = null)
    {
        // "Animation effects" off in Windows: no movement, fades stay so changes remain readable.
        if (!SystemParameters.ClientAreaAnimation && property != OpacityProperty) ms = 0;
        var a = new DoubleAnimation
        {
            From = from, To = to, Duration = TimeSpan.FromMilliseconds(ms),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Timeline.SetDesiredFrameRate(a, 120);
        if (done != null) a.Completed += (s, e) => done();
        target.BeginAnimation(property, a);
    }

    // Blurred, more saturated copy of the screen behind the menu; null (high contrast) gives a solid surface.
    // The Windows "transparency effects" switch is deliberately ignored: the glass look was asked for.
    // shortcut: a still snapshot, so moving content behind the glass does not update; needs DWM composition APIs.
    static Brush Blurred(Drawing.Point at, int side)
    {
        if (SystemParameters.HighContrast) return null;
        try
        {
            int small = side / 8;
            var pixels = new byte[small * small * 4];
            using (var shot = new Drawing.Bitmap(side, side))
            using (var reduced = new Drawing.Bitmap(small, small))
            {
                using (var g = Drawing.Graphics.FromImage(shot)) g.CopyFromScreen(at, Drawing.Point.Empty, shot.Size);
                using (var g = Drawing.Graphics.FromImage(reduced))
                {
                    g.InterpolationMode = Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
                    g.DrawImage(shot, 0, 0, small, small);
                }
                var data = reduced.LockBits(new Drawing.Rectangle(0, 0, small, small),
                    Drawing.Imaging.ImageLockMode.ReadOnly, Drawing.Imaging.PixelFormat.Format32bppArgb);
                Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
                reduced.UnlockBits(data);
            }
            for (int pass = 0; pass < 3; pass++)
            {
                pixels = BoxBlur(pixels, small, 4, small * 4); // horizontal
                pixels = BoxBlur(pixels, small, small * 4, 4); // vertical
            }
            for (int i = 0; i < pixels.Length; i += 4)
            {
                double grey = 0.114 * pixels[i] + 0.587 * pixels[i + 1] + 0.299 * pixels[i + 2];
                for (int c = 0; c < 3; c++)
                    pixels[i + c] = (byte)Math.Max(0, Math.Min(255, grey + (pixels[i + c] - grey) * 1.7));
            }
            var image = BitmapSource.Create(small, small, 96, 96, PixelFormats.Bgr32, null, pixels, small * 4);
            return new ImageBrush(image) { ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, Side, Side) };
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    static byte[] BoxBlur(byte[] source, int size, int along, int across)
    {
        const int r = 2;
        var result = new byte[source.Length];
        for (int line = 0; line < size; line++)
            for (int i = 0; i < size; i++)
                for (int channel = 0; channel < 4; channel++)
                {
                    int sum = 0;
                    for (int k = -r; k <= r; k++)
                        sum += source[line * across + Math.Max(0, Math.Min(size - 1, i + k)) * along + channel];
                    result[line * across + i * along + channel] = (byte)(sum / (2 * r + 1));
                }
        return result;
    }

    static Point Polar(double radius, double degrees)
    {
        double a = degrees * Math.PI / 180;
        return new Point(C + radius * Math.Sin(a), C - radius * Math.Cos(a));
    }

    static Geometry Circle(double radius) { return new EllipseGeometry(new Point(C, C), radius, radius); }

    // Ring segment with rounded corners; pad keeps an even gap to the neighbouring segment at both radii.
    static Geometry Rounded(double from, double to, double start, double sweep, double corner, double pad)
    {
        double r0 = from + corner, r1 = to - corner;
        double in0 = (corner + pad) / r0 * 180 / Math.PI, in1 = (corner + pad) / r1 * 180 / Math.PI;
        var core = new StreamGeometry();
        using (var c = core.Open())
        {
            c.BeginFigure(Polar(r1, start + in1), true, true);
            c.ArcTo(Polar(r1, start + sweep - in1), new Size(r1, r1), 0, sweep - 2 * in1 > 180, SweepDirection.Clockwise, true, false);
            c.LineTo(Polar(r0, start + sweep - in0), true, false);
            c.ArcTo(Polar(r0, start + in0), new Size(r0, r0), 0, sweep - 2 * in0 > 180, SweepDirection.Counterclockwise, true, false);
        }
        var round = new Pen(Brushes.Black, corner * 2) { LineJoin = PenLineJoin.Round };
        var shape = Geometry.Combine(core, core.GetWidenedPathGeometry(round), GeometryCombineMode.Union, null);
        shape.Freeze();
        return shape;
    }

    static Brush Solid(int a, int r, int g, int b)
    {
        var brush = new SolidColorBrush(Color.FromArgb((byte)a, (byte)r, (byte)g, (byte)b));
        brush.Freeze();
        return brush;
    }

    static TextBlock Text(string text, double size, double width)
    {
        return new TextBlock
        {
            Text = text, FontSize = size, Width = width, Foreground = Brushes.White,
            TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
    }

    static void Place(Canvas on, UIElement element, double x, double y)
    {
        Canvas.SetLeft(element, x);
        Canvas.SetTop(element, y);
        on.Children.Add(element);
    }
}

// Colour picker: an icon of its own next to the clock. A click picks the colour of any pixel and copies it;
// a right click lists everything picked so far.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using System.Windows.Markup;
using Microsoft.Win32;
using Wpf = System.Windows;
using Controls = System.Windows.Controls;
using Media = System.Windows.Media;

// The colours picked so far, newest first.
static class ColorStore
{
    public const int Most = 40;
    const string Value = "Colors";

    public static string Hex(Color c) { return "#" + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2"); }

    public static string Rgb(Color c) { return "rgb(" + c.R + ", " + c.G + ", " + c.B + ")"; }

    // null when the text is not a stored colour.
    public static Color? Parse(string hex)
    {
        int number;
        if (hex == null || hex.Length != 7 || hex[0] != '#'
            || !int.TryParse(hex.Substring(1), System.Globalization.NumberStyles.AllowHexSpecifier, null, out number)) return null;
        return Color.FromArgb(255, (number >> 16) & 255, (number >> 8) & 255, number & 255);
    }

    public static List<Color> All()
    {
        var lines = Registry.GetValue(@"HKEY_CURRENT_USER\" + Prefs.Store, Value, null) as string[];
        return lines == null ? new List<Color>() : lines.Select(Parse).Where(c => c != null).Select(c => c.Value).ToList();
    }

    // A colour picked again moves to the top.
    public static void Add(Color color)
    {
        var all = All().Where(c => c.ToArgb() != color.ToArgb()).ToList();
        all.Insert(0, Color.FromArgb(255, color.R, color.G, color.B));
        Store(all.Take(Most));
    }

    public static void Clear() { Store(new Color[0]); }

    static void Store(IEnumerable<Color> colors)
    {
        Registry.SetValue(@"HKEY_CURRENT_USER\" + Prefs.Store, Value, colors.Select(Hex).ToArray(), RegistryValueKind.MultiString);
    }

    // The text that goes to the clipboard, in the chosen form.
    public static string Text(Color c) { return Prefs.CopyRgb ? Rgb(c) : Hex(c); }
}

// The frozen screen with a magnifier at the pointer. Colours are shown as they are, nothing is dimmed.
class ColorPick : ScreenOverlay
{
    const int Reach = 7, Zoom = 10, Side = (2 * Reach + 1) * Zoom, Strip = 34, Away = 22;
    readonly Action<Color> picked;
    Point at = new Point(-1000, -1000);

    public ColorPick(Rectangle screen, Action<Color> picked) : base(screen)
    {
        this.picked = picked;
        Text = "All In One - renk seç";
    }

    // Beside the pointer, on the other side where it would leave the screen.
    public static Rectangle Magnifier(Point pointer, Size screen)
    {
        int x = pointer.X + Away, y = pointer.Y + Away;
        if (x + Side > screen.Width) x = pointer.X - Away - Side;
        if (y + Side + Strip > screen.Height) y = pointer.Y - Away - Side - Strip;
        return new Rectangle(x, y, Side, Side + Strip);
    }

    Color Under(Point p)
    {
        return Frozen.GetPixel(Math.Max(0, Math.Min(Frozen.Width - 1, p.X)), Math.Max(0, Math.Min(Frozen.Height - 1, p.Y)));
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        MoveTo(PointToClient(Cursor.Position));
    }

    // Only the magnifier's old and new places are redrawn: the whole screen each time would lag.
    void MoveTo(Point pointer)
    {
        var old = Magnifier(at, Frozen.Size);
        at = pointer;
        old.Inflate(3, 3);
        var now = Magnifier(at, Frozen.Size);
        now.Inflate(3, 3);
        Invalidate(old);
        Invalidate(now);
    }

    protected override void OnPaintBackground(PaintEventArgs e) { } // OnPaint covers everything

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.DrawImage(Frozen, e.ClipRectangle, e.ClipRectangle, GraphicsUnit.Pixel);
        Hint(g, "Rengini almak istediğin yere tıkla  ·  ok tuşları: 1 piksel  ·  Esc: vazgeç");
        var box = Magnifier(at, Frozen.Size);
        var color = Under(at);
        using (var back = new SolidBrush(Color.FromArgb(27, 28, 35))) g.FillRectangle(back, box);
        var mode = g.InterpolationMode;
        var offset = g.PixelOffsetMode;
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
        g.DrawImage(Frozen, new Rectangle(box.X, box.Y, Side, Side),
            new Rectangle(at.X - Reach, at.Y - Reach, 2 * Reach + 1, 2 * Reach + 1), GraphicsUnit.Pixel);
        g.InterpolationMode = mode;
        g.PixelOffsetMode = offset;
        // The pixel that will be taken.
        g.DrawRectangle(Pens.Black, box.X + Reach * Zoom - 1, box.Y + Reach * Zoom - 1, Zoom + 1, Zoom + 1);
        g.DrawRectangle(Pens.White, box.X + Reach * Zoom, box.Y + Reach * Zoom, Zoom - 1, Zoom - 1);
        using (var swatch = new SolidBrush(color)) g.FillRectangle(swatch, box.X + 8, box.Y + Side + 8, 18, 18);
        using (var font = new Font("Consolas", 15, GraphicsUnit.Pixel))
            g.DrawString(ColorStore.Hex(color), font, Brushes.White, box.X + 34, box.Y + Side + 9);
        g.DrawRectangle(Pens.White, box.X, box.Y, box.Width - 1, box.Height - 1);
    }

    protected override void OnMouseMove(MouseEventArgs e) { MoveTo(e.Location); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left) Take(e.Location);
        else Close();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        int dx = e.KeyCode == Keys.Left ? -1 : e.KeyCode == Keys.Right ? 1 : 0;
        int dy = e.KeyCode == Keys.Up ? -1 : e.KeyCode == Keys.Down ? 1 : 0;
        if (dx != 0 || dy != 0) Cursor.Position = new Point(Cursor.Position.X + dx, Cursor.Position.Y + dy);
        else if (e.KeyCode == Keys.Enter) Take(at);
        else base.OnKeyDown(e);
    }

    void Take(Point pointer)
    {
        picked(Under(pointer));
        Close();
    }
}

// Ties the notification area icon, the overlay and the list together.
static class Picker
{
    public static Action<string> Clip = Ocr.ToClipboard; // the test replaces it
    static NotifyIcon icon;
    static IntPtr drawn; // the icon's handle, ours to destroy
    static ColorHistory history;

    static bool closed; // closed by hand: only until the app starts again, so it is back after a restart of the PC

    public static bool Shown { get { return icon != null && icon.Visible; } }

    // Shows or hides the icon next to the clock. A hidden icon is kept: Windows remembers where the user
    // dragged it only for the same icon.
    public static void Refresh()
    {
        if (!Prefs.ShowPicker || closed)
        {
            if (icon != null) icon.Visible = false;
            return;
        }
        if (icon == null)
        {
            icon = new NotifyIcon();
            icon.MouseClick += (s, e) =>
            {
                if (e.Button == MouseButtons.Left) Pick();
                else if (e.Button == MouseButtons.Right) ShowHistory(Cursor.Position);
            };
        }
        Update();
        icon.Visible = true;
    }

    public static void Close()
    {
        closed = true;
        Refresh();
    }

    public static void Reopen()
    {
        closed = false;
        Prefs.ShowPicker = true;
        Refresh();
    }

    // For the end of the app.
    public static void Remove()
    {
        if (icon == null) return;
        icon.Dispose();
        icon = null;
        Native.DestroyIcon(drawn);
        drawn = IntPtr.Zero;
    }

    // The icon wears the last picked colour.
    public static void Update()
    {
        if (icon == null) return;
        var all = ColorStore.All();
        var last = all.Count > 0 ? all[0] : Color.FromArgb(0x0A, 0x84, 0xFF);
        IntPtr previous = drawn;
        using (var picture = Draw(last)) drawn = picture.GetHicon();
        icon.Icon = Icon.FromHandle(drawn);
        if (previous != IntPtr.Zero) Native.DestroyIcon(previous);
        icon.Text = "Renk se\u00e7" + (all.Count > 0 ? " \u00b7 son: " + ColorStore.Text(last) : "") + " \u00b7 sa\u011f t\u0131k: liste";
    }

    // A rounded square of the colour with an eyedropper on it.
    public static Bitmap Draw(Color color)
    {
        var picture = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(picture))
        using (var shape = new System.Drawing.Drawing2D.GraphicsPath())
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            const int Round = 12;
            shape.AddArc(1, 1, Round, Round, 180, 90);
            shape.AddArc(30 - Round, 1, Round, Round, 270, 90);
            shape.AddArc(30 - Round, 30 - Round, Round, Round, 0, 90);
            shape.AddArc(1, 30 - Round, Round, Round, 90, 90);
            shape.CloseFigure();
            using (var fill = new SolidBrush(color)) g.FillPath(fill, shape);
            using (var rim = new Pen(Color.FromArgb(210, 255, 255, 255), 2)) g.DrawPath(rim, shape);
            bool light = color.R * 299 + color.G * 587 + color.B * 114 > 150000;
            using (var font = new Font("Segoe MDL2 Assets", 19, GraphicsUnit.Pixel))
            using (var middle = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                g.DrawString("\uEF3C", font, light ? Brushes.Black : Brushes.White, new RectangleF(0, 1, 32, 32), middle);
        }
        return picture;
    }

    public static void Pick()
    {
        if (history != null) history.Dismiss();
        string report = null;
        ScreenOverlay.Open(screen => new ColorPick(screen, color => report = Take(color)), () =>
        {
            Update();
            if (report != null) Note.Show(report, Cursor.Position);
        });
    }

    static void ShowHistory(Point near)
    {
        if (history != null) history.Dismiss();
        history = new ColorHistory(near);
        Assets.Show(history.Window);
        // Taking the keyboard lets a click anywhere else close the list.
        history.Window.Activate();
        Native.SetForegroundWindow(new System.Windows.Interop.WindowInteropHelper(history.Window).Handle);
    }

    // Keeps and copies a picked colour; returns what to tell.
    public static string Take(Color color)
    {
        ColorStore.Add(color);
        return Copy(color);
    }

    public static string Copy(Color color)
    {
        string text = ColorStore.Text(color);
        try
        {
            Clip(text);
            return text + " kopyaland\u0131";
        }
        catch (Exception e) { return "Panoya kopyalanamad\u0131: " + e.Message; }
    }

    public static Media.Color Convert(Color c) { return Media.Color.FromRgb(c.R, c.G, c.B); }

    // Puts a small window beside a point of the screen (in pixels), inside that screen's working area.
    public static void Beside(Wpf.Window window, Point near)
    {
        var transform = Wpf.PresentationSource.FromVisual(window).CompositionTarget.TransformFromDevice;
        var screen = Screen.FromPoint(near).WorkingArea;
        double left = screen.X * transform.M11, top = screen.Y * transform.M22;
        double right = screen.Right * transform.M11, bottom = screen.Bottom * transform.M22;
        double x = near.X * transform.M11 - window.ActualWidth / 2, y = near.Y * transform.M22;
        y = y > (top + bottom) / 2 ? y - window.ActualHeight - 8 : y + 16;
        window.Left = Math.Max(left, Math.Min(right - window.ActualWidth, x));
        window.Top = Math.Max(top, Math.Min(bottom - window.ActualHeight, y));
    }
}

// "#RRGGBB kopyaland\u0131" for a moment where the pointer is; it takes neither clicks nor the keyboard.
static class Note
{
    const string Xaml = @"<Window xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
        xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' SizeToContent='WidthAndHeight' WindowStyle='None'
        AllowsTransparency='True' Background='Transparent' ResizeMode='NoResize' ShowInTaskbar='False' ShowActivated='False'
        Topmost='True' IsHitTestVisible='False' WindowStartupLocation='Manual'>
      <Border Background='#F01B1C23' BorderBrush='#55FFFFFF' BorderThickness='1' CornerRadius='8' Padding='10,6'>
        <TextBlock x:Name='Note' Foreground='White' FontFamily='Segoe UI Variable Text, Segoe UI' FontSize='12'/>
      </Border>
    </Window>";

    public static Wpf.Window Show(string text, Point near)
    {
        var window = (Wpf.Window)XamlReader.Parse(Xaml);
        ((Controls.TextBlock)window.FindName("Note")).Text = text;
        window.Loaded += (s, e) => Picker.Beside(window, near);
        window.SourceInitialized += (s, e) =>
        {
            IntPtr handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            // Click-through, never activated, not listed in Alt+Tab.
            Native.SetWindowLong(handle, -20, Native.GetWindowLong(handle, -20) | 0x20 | 0x08000000 | 0x80);
        };
        var gone = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1.4) };
        gone.Tick += (s, e) =>
        {
            gone.Stop();
            window.Close();
        };
        window.Show();
        gone.Start();
        return window;
    }
}

// The small list of picked colours.
class ColorHistory
{
    const string Xaml = @"<Window xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
        xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' Title='All In One - kopyalanan renkler'
        Width='288' SizeToContent='Height' WindowStyle='None' AllowsTransparency='True' Background='Transparent'
        ResizeMode='NoResize' ShowInTaskbar='False' Topmost='True' WindowStartupLocation='Manual'
        Foreground='#F2F3F5' FontSize='13' FontFamily='Segoe UI Variable Text, Segoe UI'>
      <Window.Resources>
        <Style TargetType='Button'>
          <Setter Property='Foreground' Value='#F2F3F5'/>
          <Setter Property='Background' Value='Transparent'/>
          <Setter Property='Padding' Value='9,6'/>
          <Setter Property='HorizontalContentAlignment' Value='Left'/>
          <Setter Property='FocusVisualStyle' Value='{x:Null}'/>
          <Setter Property='Template'>
            <Setter.Value>
              <ControlTemplate TargetType='Button'>
                <Border x:Name='face' Background='{TemplateBinding Background}' CornerRadius='8' Padding='{TemplateBinding Padding}'
                        BorderThickness='1' BorderBrush='Transparent'>
                  <ContentPresenter HorizontalAlignment='{TemplateBinding HorizontalContentAlignment}' VerticalAlignment='Center'/>
                </Border>
                <ControlTemplate.Triggers>
                  <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='face' Property='Background' Value='#22FFFFFF'/></Trigger>
                  <Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='face' Property='BorderBrush' Value='White'/></Trigger>
                  <Trigger Property='IsEnabled' Value='False'><Setter TargetName='face' Property='Opacity' Value='0.4'/></Trigger>
                </ControlTemplate.Triggers>
              </ControlTemplate>
            </Setter.Value>
          </Setter>
        </Style>
      </Window.Resources>
      <Border Background='#F51B1C23' CornerRadius='14' BorderBrush='#40FFFFFF' BorderThickness='1' Padding='8'>
        <StackPanel>
          <TextBlock Text='Kopyalanan renkler' Foreground='#A6A9B4' FontSize='12' Margin='9,4,9,6'/>
          <ScrollViewer MaxHeight='300' VerticalScrollBarVisibility='Auto'><StackPanel x:Name='Rows'/></ScrollViewer>
          <TextBlock x:Name='Empty' Text='Henüz renk seçmedin.' Foreground='#A6A9B4' Margin='9,2,9,8'/>
          <Border Height='1' Background='#2E303A' Margin='0,6'/>
          <DockPanel>
            <Button x:Name='Hide' DockPanel.Dock='Right' Content='Kapat'
                    ToolTip='Bilgisayar yeniden açılınca geri gelir; hemen açmak için All In One simgesini kullan'/>
            <Button x:Name='Clear' DockPanel.Dock='Right' Content='Temizle'/>
            <Button x:Name='Form' ToolTip='Kopyalama biçimini değiştir'/>
          </DockPanel>
        </StackPanel>
      </Border>
    </Window>";

    public readonly Wpf.Window Window = (Wpf.Window)XamlReader.Parse(Xaml);
    bool closing;

    T Get<T>(string name) where T : class { return (T)Window.FindName(name); }

    // near: the point of the screen, in pixels, the list opens beside.
    public ColorHistory(Point near)
    {
        Fill();
        Get<Controls.Button>("Form").Click += (s, e) =>
        {
            Prefs.CopyRgb = !Prefs.CopyRgb;
            Fill();
            Picker.Update();
        };
        Get<Controls.Button>("Clear").Click += (s, e) =>
        {
            ColorStore.Clear();
            Fill();
            Picker.Update();
        };
        Get<Controls.Button>("Hide").Click += (s, e) =>
        {
            Dismiss();
            Picker.Close();
        };
        Window.Closing += (s, e) => closing = true;
        Window.Deactivated += (s, e) => Dismiss();
        Window.PreviewKeyDown += (s, e) => { if (e.Key == System.Windows.Input.Key.Escape) Dismiss(); };
        Window.Loaded += (s, e) => Picker.Beside(Window, near);
    }

    public void Dismiss()
    {
        if (!closing) Window.Close(); // closing a window deactivates it, which must not close it again
    }

    void Fill()
    {
        var rows = Get<Controls.StackPanel>("Rows");
        rows.Children.Clear();
        var all = ColorStore.All();
        foreach (var each in all)
        {
            var color = each;
            var line = new Controls.StackPanel { Orientation = Controls.Orientation.Horizontal };
            line.Children.Add(new Controls.Border { Width = 20, Height = 20, CornerRadius = new Wpf.CornerRadius(6),
                Background = new Media.SolidColorBrush(Picker.Convert(color)),
                BorderBrush = new Media.SolidColorBrush(Media.Color.FromArgb(0x40, 255, 255, 255)),
                BorderThickness = new Wpf.Thickness(1), Margin = new Wpf.Thickness(0, 0, 10, 0) });
            line.Children.Add(new Controls.TextBlock { Text = ColorStore.Text(color), VerticalAlignment = Wpf.VerticalAlignment.Center,
                FontFamily = new Media.FontFamily("Consolas") });
            var row = new Controls.Button { Content = line, ToolTip = ColorStore.Hex(color) + " · " + ColorStore.Rgb(color) + "\nKopyalamak için tıkla" };
            row.Click += (s, e) =>
            {
                string report = Picker.Copy(color);
                Dismiss();
                Note.Show(report, Cursor.Position);
            };
            rows.Children.Add(row);
        }
        Get<Controls.TextBlock>("Empty").Visibility = all.Count == 0 ? Wpf.Visibility.Visible : Wpf.Visibility.Collapsed;
        Get<Controls.Button>("Clear").IsEnabled = all.Count > 0;
        Get<Controls.Button>("Form").Content = "Biçim: " + (Prefs.CopyRgb ? "RGB" : "HEX");
    }

}

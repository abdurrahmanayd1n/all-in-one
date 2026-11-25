// The image tool window: live preview of the result on the left, settings on the right.
// With several files the preview strip picks which one is shown; saving applies the settings to all of them.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Path = System.IO.Path;

static class Assets
{
    public static Stream Open(string name)
    {
        return typeof(Assets).Assembly.GetManifestResourceStream(name);
    }

    public static ImageSource Icon()
    {
        using (var stream = Open("app.ico"))
            return BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad)
                .Frames.OrderByDescending(f => f.PixelWidth).First();
    }

    public static Window Window(string name)
    {
        using (var stream = Open(name)) return (Window)XamlReader.Load(stream);
    }

    // What every popup shares: the one window size, the icon, the dark caption and the entrance.
    public static void Dress(Window window)
    {
        window.Icon = Icon();
        // Only very small screens shrink it.
        window.Width = Math.Min(window.Width, SystemParameters.WorkArea.Width);
        window.Height = Math.Min(window.Height, SystemParameters.WorkArea.Height);
        window.SourceInitialized += (s, e) =>
        {
            IntPtr handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            int dark = 1, caption = 0x00231C1B; // 0x00BBGGRR of the panel colour
            Native.DwmSetWindowAttribute(handle, 20, ref dark, 4);
            Native.DwmSetWindowAttribute(handle, 35, ref caption, 4);
        };
        window.Loaded += (s, e) =>
        {
            var page = (Grid)window.FindName("Page");
            RadialMenu.Animate(page, UIElement.OpacityProperty, 0, 1, 260);
            var slide = new TranslateTransform();
            page.RenderTransform = slide;
            RadialMenu.Animate(slide, TranslateTransform.YProperty, 10, 0, 340);
            window.Activate();
        };
    }

    public static void Show(Window window)
    {
        // The app runs a WinForms message loop: without this, typing in a modeless WPF window does not work.
        System.Windows.Forms.Integration.ElementHost.EnableModelessKeyboardInterop(window);
        window.Show();
    }

    // The thumbnail strip of a window that holds several pictures.
    // shortcut: thumbnails ignore the camera rotation tag; run them through Images.Load if that bothers.
    public static void Strip(Window window, string[] paths, Action<int> picked)
    {
        ((StackPanel)window.FindName("BatchBar")).Visibility = Visibility.Visible;
        var strip = (StackPanel)window.FindName("Strip");
        for (int i = 0; i < paths.Length; i++)
        {
            int at = i;
            var thumb = new RadioButton { GroupName = "thumbs", ToolTip = Path.GetFileName(paths[i]), IsChecked = i == 0, Content = i + 1 };
            thumb.SetResourceReference(FrameworkElement.StyleProperty, "Thumb");
            thumb.Checked += (s, e) => picked(at);
            strip.Children.Add(thumb);
        }
        bool closed = false;
        window.Closed += (s, e) => closed = true;
        window.Loaded += (s, e) => Images.Background(() =>
        {
            for (int i = 0; i < paths.Length && !closed; i++)
            {
                try
                {
                    var small = new BitmapImage();
                    using (var stream = File.OpenRead(paths[i]))
                    {
                        small.BeginInit();
                        small.CacheOption = BitmapCacheOption.OnLoad;
                        small.DecodePixelHeight = 96;
                        small.StreamSource = stream;
                        small.EndInit();
                    }
                    small.Freeze();
                    int at = i;
                    window.Dispatcher.BeginInvoke((Action)(() =>
                        ((RadioButton)strip.Children[at]).Content = new Image { Source = small, Height = 50, MaxWidth = 90 }));
                }
                catch (Exception) { } // the number stays; opening the picture shows why
            }
        });
    }

    // Sizes the preview to a picture: as large as the stage allows, never enlarged.
    public static void Fit(Window window, BitmapSource pixels)
    {
        var stage = (Grid)window.FindName("Stage");
        int w = pixels.PixelWidth, h = pixels.PixelHeight;
        double shown = Math.Min(1, Math.Min(stage.ActualWidth / w, stage.ActualHeight / h));
        var frame = (Grid)window.FindName("Frame");
        var canvas = (Canvas)window.FindName("Selection");
        frame.Visibility = Visibility.Visible;
        frame.Width = canvas.Width = w * shown;
        frame.Height = canvas.Height = h * shown;
        ((Image)window.FindName("Preview")).Source = pixels;
        ((Image)window.FindName("Original")).Source = pixels;
    }
}

enum Mode { Format, Scale, Compress, Crop, Preset }

class Editor
{
    // Everything the background worker needs, read from the controls on the UI thread.
    class Settings
    {
        public string Ext; // null: decided per file
        public int Quality, Percent, Typed, Width, Height;
        public bool KeepRatio, UseSuffix, Zip;
        public Rect Selection; // in preview units
        public Size Preview;
        public double Ratio; // 0: no fixed ratio to carry over to other pictures
        public CropShape Shape;
        public double Roundness;
        public int Turns; // quarter turns, applied after the mirroring
        public bool Mirrored;
        public string Name, Suffix, ZipName; // Name is the file name, or the common name of a batch
        public Method Method;
        public int TargetKb;
        public Preset Preset; // preset mode: the values as they stand in the controls
    }

    enum Method { Manual, Smart, Target }

    // What the information bar under the preview says about the result.
    class Report
    {
        public string Size = "—", Dimensions = "—", Kind = "—", Quality, Summary, Note = "";
        public int Trend; // -1 smaller, 1 bigger
    }

    public readonly Window Window = Assets.Window("Editor.xaml");
    readonly string[] paths;
    readonly Mode mode;
    readonly DispatcherTimer debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
    Picture picture; // the one shown
    int index, loading;
    Rect selection; // in preview units
    double ratio;
    bool ownRatio; // the ratio is each picture's own
    Picture upright; // the shown picture as it is in its file; "picture" is it turned as chosen
    int turns;
    bool mirrored, turning;
    CropShape shape;
    bool zip;
    TextBox typing; // the crop number being typed: it is not rewritten under the cursor
    string format; // null: decided per file
    int typed;
    Method method;
    double split = 0.5; // where the before/after divider stands, 0..1
    bool busy, dirty, syncing, nameEdited, saving, shownOnce;
    volatile bool closed;
    volatile int totalRun; // a newer number stops the running total
    string totalKey; // the settings the batch total belongs to
    bool totalBusy;

    bool Many { get { return paths.Length > 1; } }

    T Get<T>(string name) where T : class { return (T)Window.FindName(name); }

    public Editor(string[] files, Mode mode)
    {
        paths = files;
        this.mode = mode;
        string title = mode == Mode.Format ? "Format Değiştir" : mode == Mode.Scale ? "Ölçeklendir"
            : mode == Mode.Compress ? "Sıkıştır" : mode == Mode.Crop ? "Kırp" : "Hazır Ayarlar";
        string subject = Many ? paths.Length + " görsel" : Path.GetFileName(paths[0]);
        Window.Title = title + " - " + subject;
        Assets.Dress(Window);
        Get<TextBlock>("Heading").Text = title;
        Get<TextBlock>("Details").Text = subject;

        SetUpPanels();
        SetUpNaming();
        SetUpCompare();
        if (Many)
        {
            Assets.Strip(Window, paths, Load);
            Get<Border>("TotalBar").Visibility = Visibility.Visible;
            SetUpOutput();
        }

        debounce.Tick += (s, e) => { debounce.Stop(); Refresh(); };
        Get<Button>("Cancel").Click += (s, e) => Window.Close();
        Get<Button>("Save").Click += (s, e) => Save();
        Window.Closed += (s, e) => closed = true; // stops a running batch after the current file
        Window.Loaded += (s, e) => Load(0);
    }

    static RadioButton Chip(Panel panel, string group, string label, Action picked)
    {
        var chip = new RadioButton { Content = label, GroupName = group };
        chip.SetResourceReference(FrameworkElement.StyleProperty, "Chip");
        chip.Checked += (s, e) => picked();
        panel.Children.Add(chip);
        return chip;
    }

    // Highlights the preset that matches the slider, if any.
    void Mark(string panel, int value)
    {
        foreach (RadioButton chip in Get<WrapPanel>(panel).Children) chip.IsChecked = Equals(chip.Tag, value);
    }

    void SetUpPanels()
    {
        string panel = mode == Mode.Format ? "FormatPanel" : mode == Mode.Scale ? "ScalePanel"
            : mode == Mode.Crop ? "CropPanel" : mode == Mode.Preset ? "PresetPanel" : "QualityPanel";
        Get<StackPanel>(panel).Visibility = Visibility.Visible;

        var quality = Get<Slider>("QualitySlider");
        if (mode != Mode.Compress) quality.Value = 92;
        Action showQuality = () => Get<TextBlock>("QualityValue").Text = ((int)quality.Value).ToString();
        quality.ValueChanged += (s, e) => { showQuality(); Mark("QualityChips", (int)quality.Value); Changed(); };
        showQuality();
        var levels = new Dictionary<string, int> { { "Hafif", 85 }, { "Orta", 70 }, { "Güçlü", 50 } };
        foreach (var level in levels)
        {
            int value = level.Value;
            var chip = Chip(Get<WrapPanel>("QualityChips"), "quality", level.Key, () => quality.Value = value);
            chip.Tag = value;
            chip.IsChecked = mode == Mode.Compress && value == 70;
        }

        if (mode == Mode.Compress) SetUpMethods();
        if (mode == Mode.Preset) SetUpPresets();

        if (mode == Mode.Compress && Images.Ffmpeg != null)
        {
            Get<StackPanel>("FormatPanel").Visibility = Visibility.Visible;
            // The first choice follows each picture: JPG, PNG when transparent, WebP stays WebP.
            Chip(Get<WrapPanel>("FormatChips"), "format", Many ? "Otomatik" : "JPG", () => PickFormat(null)).IsChecked = true;
            Chip(Get<WrapPanel>("FormatChips"), "format", "WebP", () => PickFormat(".webp"));
        }

        if (mode == Mode.Format)
        {
            // Start on a format that differs from the first file's own.
            string start = Images.Writable(paths[0]).StartsWith(".jp") ? ".png" : ".jpg";
            foreach (var ext in new[] { ".jpg", ".png", ".webp", ".bmp", ".tif", ".gif" })
            {
                if (ext == ".webp" && Images.Ffmpeg == null) continue;
                string choice = ext;
                var chip = Chip(Get<WrapPanel>("FormatChips"), "format",
                    ext == ".tif" ? "TIFF" : ext == ".webp" ? "WebP" : ext.Substring(1).ToUpperInvariant(), () => PickFormat(choice));
                if (ext == start) chip.IsChecked = true;
            }
        }

        if (mode == Mode.Scale)
        {
            var scale = Get<Slider>("ScaleSlider");
            scale.ValueChanged += (s, e) => { if (!syncing) { Mark("ScaleChips", (int)scale.Value); SizeFromPercent(); } };
            foreach (int percent in new[] { 25, 50, 75, 100, 150, 200 })
            {
                int value = percent;
                var chip = Chip(Get<WrapPanel>("ScaleChips"), "scale", "%" + percent, () => { scale.Value = value; SizeFromPercent(); });
                chip.Tag = value;
                chip.IsChecked = percent == 50;
            }
            Get<TextBox>("WidthBox").TextChanged += (s, e) => SizeTyped(true);
            Get<TextBox>("HeightBox").TextChanged += (s, e) => SizeTyped(false);
            var keep = Get<CheckBox>("LockRatio");
            keep.Click += (s, e) => { if (typed != 0) SizeTyped(typed == 1); };
        }

        if (mode == Mode.Crop)
        {
            var ratios = new Dictionary<string, double> {
                { "Serbest", 0 }, { "Özgün", -1 }, { "1:1", 1 }, { "4:3", 4.0 / 3 }, { "3:4", 3.0 / 4 }, { "3:2", 1.5 },
                { "2:3", 2.0 / 3 }, { "4:5", 4.0 / 5 }, { "5:4", 5.0 / 4 }, { "16:9", 16.0 / 9 }, { "9:16", 9.0 / 16 },
                { "21:9", 21.0 / 9 }, { "9:21", 9.0 / 21 } };
            foreach (var entry in ratios)
            {
                double value = entry.Value;
                var chip = Chip(Get<WrapPanel>("AspectChips"), "aspect", entry.Key, () => PickRatio(value));
                chip.Tag = value;
                chip.IsChecked = value == 0;
            }
            SetUpShapes();
            SetUpSelection();
        }
    }

    void SetUpShapes()
    {
        var names = new Dictionary<CropShape, string> {
            { CropShape.Rectangle, "Dikdörtgen" }, { CropShape.Rounded, "Yuvarlak köşe" }, { CropShape.Circle, "Daire" },
            { CropShape.Ellipse, "Elips" }, { CropShape.Triangle, "Üçgen" }, { CropShape.Diamond, "Baklava" },
            { CropShape.Hexagon, "Altıgen" }, { CropShape.Star, "Yıldız" }, { CropShape.Heart, "Kalp" } };
        var round = Get<Slider>("RoundSlider");
        Action showRound = () => Get<TextBlock>("RoundValue").Text = "%" + (int)round.Value;
        round.ValueChanged += (s, e) => { showRound(); Redraw(); };
        showRound();
        foreach (var entry in names)
        {
            var value = entry.Key;
            Chip(Get<WrapPanel>("ShapeChips"), "shape", entry.Value, () => PickShape(value)).IsChecked = value == CropShape.Rectangle;
        }
    }

    void PickShape(CropShape value)
    {
        shape = value;
        Get<StackPanel>("RoundPanel").Visibility = value == CropShape.Rounded ? Visibility.Visible : Visibility.Collapsed;
        // A circle is an ellipse in a square.
        if (value == CropShape.Circle)
            foreach (RadioButton chip in Get<WrapPanel>("AspectChips").Children) if (Equals(chip.Tag, 1.0)) chip.IsChecked = true;
        UpdateName();
        Redraw();
    }

    void SetUpOutput()
    {
        Get<StackPanel>("OutputPanel").Visibility = Visibility.Visible;
        var panel = Get<WrapPanel>("OutputChips");
        Chip(panel, "output", "Ayrı dosyalar", () => PickOutput(false)).IsChecked = true;
        // The archive fields sit low in the panel: bring them into view when they appear.
        Chip(panel, "output", "Tek ZIP", () =>
        {
            PickOutput(true);
            Get<TextBox>("ZipBox").Dispatcher.BeginInvoke((Action)(() => Get<StackPanel>("ZipPanel").BringIntoView()),
                DispatcherPriority.Loaded);
        });
        Get<TextBox>("ZipBox").TextChanged += (s, e) => Validate();
    }

    void PickOutput(bool archive)
    {
        zip = archive;
        Get<StackPanel>("ZipPanel").Visibility = archive ? Visibility.Visible : Visibility.Collapsed;
        Validate();
    }

    void SetUpPresets()
    {
        Get<StackPanel>("FormatPanel").Visibility = Visibility.Visible;
        Get<StackPanel>("QualityPanel").Visibility = Visibility.Visible;
        var formats = Get<WrapPanel>("FormatChips");
        foreach (var ext in new[] { "", ".jpg", ".png", ".webp" })
        {
            if (ext == ".webp" && Images.Ffmpeg == null) continue;
            string choice = ext;
            Chip(formats, "format", ext == "" ? "Aynı" : Images.Kind(ext), () => PickFormat(choice == "" ? null : choice)).Tag = ext;
        }
        foreach (string name in new[] { "PresetSideBox", "PresetLimitBox", "PresetNameBox" })
            Get<TextBox>(name).TextChanged += (s, e) =>
            {
                if (syncing) return;
                UpdateName();
                Changed();
            };
        Get<Button>("PresetStore").Click += (s, e) => ChangePresets(() => Presets.Put(Read().Preset));
        Get<Button>("PresetDelete").Click += (s, e) => ChangePresets(() => Presets.Remove(Get<TextBox>("PresetNameBox").Text));
        ListPresets(null);
    }

    // Rebuilds the chips and checks the named preset, or the first one.
    void ListPresets(string name)
    {
        var panel = Get<WrapPanel>("PresetChips");
        panel.Children.Clear();
        foreach (var preset in Presets.All())
        {
            var value = preset;
            Chip(panel, "preset", value.Name, () => PickPreset(value));
        }
        var chips = panel.Children.Cast<RadioButton>().ToArray();
        var wanted = chips.FirstOrDefault(c => string.Equals((string)c.Content, name, StringComparison.CurrentCultureIgnoreCase))
            ?? chips.FirstOrDefault();
        if (wanted != null) wanted.IsChecked = true;
        else PickPreset(new Preset());
    }

    void PickPreset(Preset preset)
    {
        syncing = true;
        Get<TextBox>("PresetSideBox").Text = preset.MaxSide.ToString();
        Get<TextBox>("PresetLimitBox").Text = preset.LimitKb.ToString();
        Get<TextBox>("PresetNameBox").Text = preset.Name;
        syncing = false;
        Get<Slider>("QualitySlider").Value = preset.Quality;
        var formats = Get<WrapPanel>("FormatChips").Children.Cast<RadioButton>().ToArray();
        (formats.FirstOrDefault(c => (string)c.Tag == preset.Format) ?? formats[0]).IsChecked = true;
        UpdateName();
        Changed();
    }

    void ChangePresets(Action change)
    {
        string name = Get<TextBox>("PresetNameBox").Text;
        try
        {
            change();
            ListPresets(name);
            Show((string)null);
        }
        catch (Exception e) { Show(e.Message); }
    }

    void SetUpMethods()
    {
        Get<StackPanel>("MethodPanel").Visibility = Visibility.Visible;
        var box = Get<TextBox>("TargetBox");
        foreach (int size in new[] { 100, 250, 500, 1000, 2000 })
        {
            int value = size;
            var chip = Chip(Get<WrapPanel>("TargetChips"), "target", size.ToString(), () => box.Text = value.ToString());
            chip.Tag = value;
            chip.IsChecked = size == 500;
        }
        box.TextChanged += (s, e) =>
        {
            int value;
            int.TryParse(box.Text, out value);
            Mark("TargetChips", value);
            Changed();
        };
        var panel = Get<WrapPanel>("MethodChips");
        Chip(panel, "method", "Elle", () => PickMethod(Method.Manual)).IsChecked = true;
        Chip(panel, "method", "Akıllı", () => PickMethod(Method.Smart));
        Chip(panel, "method", "Hedef boyut", () => PickMethod(Method.Target));
    }

    void PickMethod(Method value)
    {
        method = value;
        Get<TextBlock>("MethodHint").Text = value == Method.Smart
            ? "Her görsel için gözle fark edilmeyecek en düşük kaliteyi kendisi bulur."
            : value == Method.Target ? "Dosyayı bu boyutun altına indirir. Kalite yetmezse görseli de küçültür."
            : "Kaliteyi sen seçersin.";
        Get<StackPanel>("TargetPanel").Visibility = value == Method.Target ? Visibility.Visible : Visibility.Collapsed;
        ShowQuality();
        // Typing a target should not start a search for every digit.
        debounce.Interval = TimeSpan.FromMilliseconds(value == Method.Target ? 600 : 120);
        Changed();
    }

    void ShowQuality()
    {
        bool lossy = mode == Mode.Preset || (mode == Mode.Compress ? method == Method.Manual
            : mode == Mode.Format && (format == ".jpg" || format == ".webp"));
        Get<StackPanel>("QualityPanel").Visibility = lossy ? Visibility.Visible : Visibility.Collapsed;
    }

    void SetUpCompare()
    {
        var canvas = Get<Canvas>("Compare");
        canvas.MouseLeftButtonDown += (s, e) => { canvas.Focus(); canvas.CaptureMouse(); Split(e.GetPosition(canvas).X / canvas.ActualWidth); };
        canvas.MouseMove += (s, e) => { if (canvas.IsMouseCaptured) Split(e.GetPosition(canvas).X / canvas.ActualWidth); };
        canvas.MouseLeftButtonUp += (s, e) => canvas.ReleaseMouseCapture();
        canvas.KeyDown += (s, e) =>
        {
            if (e.Key != Key.Left && e.Key != Key.Right) return;
            Split(split + (e.Key == Key.Left ? -0.02 : 0.02));
            e.Handled = true;
        };
    }

    // Each side shows only its own picture, so transparent areas never reveal the other one.
    void Split(double value)
    {
        split = Math.Max(0, Math.Min(1, value));
        var frame = Get<Grid>("Frame");
        double w = frame.Width, h = frame.Height, x = split * w;
        if (double.IsNaN(w)) return;
        bool comparing = Get<Canvas>("Compare").Visibility == Visibility.Visible;
        Get<Image>("Original").Clip = new RectangleGeometry(new Rect(0, 0, x, h));
        Get<Image>("Preview").Clip = comparing ? new RectangleGeometry(new Rect(x, 0, w - x, h)) : null;
        var line = Get<System.Windows.Shapes.Line>("Divider");
        line.X1 = line.X2 = x;
        line.Y2 = h;
        var handle = Get<Border>("Handle");
        Canvas.SetLeft(handle, x - 15);
        Canvas.SetTop(handle, h / 2 - 15);
        Canvas.SetLeft(Get<Border>("AfterTag"), Math.Max(0, w - 56));
    }

    void Comparing(bool on)
    {
        var visibility = on ? Visibility.Visible : Visibility.Collapsed;
        Get<Canvas>("Compare").Visibility = visibility;
        Get<Image>("Original").Visibility = visibility;
        Split(split);
    }

    void PickFormat(string ext)
    {
        format = ext;
        ShowQuality();
        UpdateName();
        Changed();
    }

    void SizeFromPercent()
    {
        var scale = Get<Slider>("ScaleSlider");
        Get<TextBlock>("ScaleValue").Text = "%" + (int)scale.Value;
        typed = 0;
        if (picture == null) return;
        syncing = true;
        Get<TextBox>("WidthBox").Text = Images.Percent(picture.Pixels.PixelWidth, (int)scale.Value).ToString();
        Get<TextBox>("HeightBox").Text = Images.Percent(picture.Pixels.PixelHeight, (int)scale.Value).ToString();
        syncing = false;
        UpdateName();
        Changed();
    }

    void SizeTyped(bool width)
    {
        if (syncing || picture == null) return;
        typed = width ? 1 : 2;
        int value;
        if (int.TryParse(Get<TextBox>(width ? "WidthBox" : "HeightBox").Text, out value) && value > 0)
        {
            int own = width ? picture.Pixels.PixelWidth : picture.Pixels.PixelHeight;
            double percent = value * 100.0 / own;
            syncing = true;
            if (Get<CheckBox>("LockRatio").IsChecked == true)
                Get<TextBox>(width ? "HeightBox" : "WidthBox").Text =
                    Images.Percent(width ? picture.Pixels.PixelHeight : picture.Pixels.PixelWidth, percent).ToString();
            Get<Slider>("ScaleSlider").Value = percent;
            Get<TextBlock>("ScaleValue").Text = "%" + (int)Math.Round(percent);
            foreach (RadioButton chip in Get<WrapPanel>("ScaleChips").Children) chip.IsChecked = false;
            syncing = false;
        }
        UpdateName();
        Changed();
    }

    // The handle under the point: -1, 0 or 1 on each axis; false when there is none.
    bool HandleAt(Point at, out int hx, out int hy)
    {
        const double Reach = 11;
        hx = Math.Abs(at.X - selection.Left) <= Reach ? -1 : Math.Abs(at.X - selection.Right) <= Reach ? 1 : 0;
        hy = Math.Abs(at.Y - selection.Top) <= Reach ? -1 : Math.Abs(at.Y - selection.Bottom) <= Reach ? 1 : 0;
        // A side handle sits in the middle of its side.
        if (hx == 0 && Math.Abs(at.X - (selection.Left + selection.Width / 2)) > Reach) return false;
        if (hy == 0 && Math.Abs(at.Y - (selection.Top + selection.Height / 2)) > Reach) return false;
        return hx != 0 || hy != 0;
    }

    // A drag inside a partial selection moves it.
    bool Movable(Point at)
    {
        var canvas = Get<Canvas>("Selection");
        return selection.Contains(at) && (selection.Width < canvas.Width - 1 || selection.Height < canvas.Height - 1);
    }

    void SetUpSelection()
    {
        var canvas = Get<Canvas>("Selection");
        Point start = new Point(), grab = new Point();
        Rect from = new Rect();
        bool moving = false;
        int hx = 0, hy = 0;
        canvas.MouseLeftButtonDown += (s, e) =>
        {
            start = e.GetPosition(canvas);
            from = selection;
            if (!HandleAt(start, out hx, out hy)) hx = hy = 0;
            moving = hx == 0 && hy == 0 && Movable(start);
            grab = new Point(start.X - selection.X, start.Y - selection.Y);
            canvas.Focus();
            canvas.CaptureMouse();
        };
        canvas.MouseMove += (s, e) =>
        {
            var at = e.GetPosition(canvas);
            if (!canvas.IsMouseCaptured)
            {
                int x, y;
                canvas.Cursor = HandleAt(at, out x, out y)
                    ? (x == 0 ? Cursors.SizeNS : y == 0 ? Cursors.SizeWE : x == y ? Cursors.SizeNWSE : Cursors.SizeNESW)
                    : Movable(at) ? Cursors.SizeAll : Cursors.Cross;
                return;
            }
            if (hx != 0 || hy != 0)
                Select(Images.Stretched(from, hx, hy, at, ratio, canvas.Width, canvas.Height));
            else if (moving)
                Select(new Rect(Math.Max(0, Math.Min(canvas.Width - selection.Width, at.X - grab.X)),
                    Math.Max(0, Math.Min(canvas.Height - selection.Height, at.Y - grab.Y)), selection.Width, selection.Height));
            else Select(Images.Dragged(start, at, ratio, canvas.Width, canvas.Height));
        };
        canvas.MouseLeftButtonUp += (s, e) => canvas.ReleaseMouseCapture();
        canvas.KeyDown += (s, e) =>
        {
            int dx = e.Key == Key.Left ? -1 : e.Key == Key.Right ? 1 : 0;
            int dy = e.Key == Key.Up ? -1 : e.Key == Key.Down ? 1 : 0;
            if ((dx == 0 && dy == 0) || picture == null) return;
            int step = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 10 : 1;
            var area = Area(Read(), picture);
            if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
                SelectPixels(area.X, area.Y, area.Width + dx * step, area.Height + dy * step, dy != 0);
            else SelectPixels(area.X + dx * step, area.Y + dy * step, area.Width, area.Height, false);
            e.Handled = true;
        };
        foreach (string name in new[] { "CropX", "CropY", "CropW", "CropH" })
        {
            var box = Get<TextBox>(name);
            box.TextChanged += (s, e) => { if (!syncing) CropTyped(box); };
            box.LostKeyboardFocus += (s, e) => ShowPixels(); // what was typed gives way to what applies
        }
        Get<Button>("CropCenter").Click += (s, e) => Select(new Rect((canvas.Width - selection.Width) / 2,
            (canvas.Height - selection.Height) / 2, selection.Width, selection.Height));
        Get<Button>("CropAll").Click += (s, e) => Select(Images.Centered(ratio, canvas.Width, canvas.Height));
        foreach (Turn step in Enum.GetValues(typeof(Turn)))
        {
            var own = step;
            Get<Button>("Turn" + step).Click += (s, e) => Rotate(own);
        }
    }

    static Picture Turned(Picture p, int turns, bool mirrored)
    {
        if (turns == 0 && !mirrored) return p;
        return new Picture { Pixels = Images.Oriented(p.Pixels, turns, mirrored), Colors = p.Colors, Transparent = p.Transparent };
    }

    // Turns the picture; the selection turns with it, so the same content stays selected.
    void Rotate(Turn step)
    {
        if (picture == null || saving) return;
        var canvas = Get<Canvas>("Selection");
        var part = new Rect(selection.X / canvas.Width, selection.Y / canvas.Height,
            selection.Width / canvas.Width, selection.Height / canvas.Height);
        Images.Compose(step, ref turns, ref mirrored);
        picture = Turned(upright, turns, mirrored);
        Assets.Fit(Window, picture.Pixels);
        // On its side a fixed ratio turns into its counterpart, 16:9 into 9:16.
        if ((step == Turn.Left || step == Turn.Right) && ratio > 0 && !ownRatio)
        {
            ratio = 1 / ratio;
            turning = true;
            foreach (RadioButton chip in Get<WrapPanel>("AspectChips").Children)
                if (Math.Abs((double)chip.Tag - ratio) < 1e-9) chip.IsChecked = true;
            turning = false;
        }
        if (ownRatio) ratio = canvas.Width / canvas.Height;
        var moved = Images.Moved(part, step);
        Select(new Rect(moved.X * canvas.Width, moved.Y * canvas.Height, moved.Width * canvas.Width, moved.Height * canvas.Height));
    }

    void CropTyped(TextBox box)
    {
        int value;
        if (picture == null || !int.TryParse(box.Text, out value) || value < 0) return;
        var area = Area(Read(), picture);
        int x = area.X, y = area.Y, w = area.Width, h = area.Height;
        // A typed position keeps its place: the size gives way at the far edge.
        if (box.Name == "CropX") { x = Math.Min(value, picture.Pixels.PixelWidth - 1); w = Math.Min(w, picture.Pixels.PixelWidth - x); }
        else if (box.Name == "CropY") { y = Math.Min(value, picture.Pixels.PixelHeight - 1); h = Math.Min(h, picture.Pixels.PixelHeight - y); }
        else if (box.Name == "CropW") w = value;
        else h = value;
        typing = box;
        SelectPixels(x, y, w, h, box.Name == "CropH" || box.Name == "CropY");
        typing = null;
    }

    // Selects an area given in the pixels of the shown picture.
    void SelectPixels(int x, int y, int w, int h, bool heightLed)
    {
        var canvas = Get<Canvas>("Selection");
        var area = Images.Fitted(x, y, w, h, ratio, picture.Pixels.PixelWidth, picture.Pixels.PixelHeight, heightLed);
        double shown = canvas.Width / picture.Pixels.PixelWidth, high = canvas.Height / picture.Pixels.PixelHeight;
        Select(new Rect(area.X * shown, area.Y * high, area.Width * shown, area.Height * high));
    }

    void ShowPixels()
    {
        if (picture == null) return;
        var area = Area(Read(), picture);
        bool was = syncing;
        syncing = true;
        var values = new Dictionary<string, int> { { "CropX", area.X }, { "CropY", area.Y }, { "CropW", area.Width }, { "CropH", area.Height } };
        foreach (var value in values)
        {
            var box = Get<TextBox>(value.Key);
            if (box != typing) box.Text = value.Value.ToString();
        }
        syncing = was;
    }

    void PickRatio(double value)
    {
        if (turning) return; // turning marks the matching chip without resetting the selection
        ownRatio = value < 0;
        var canvas = Get<Canvas>("Selection");
        ratio = !ownRatio ? value : picture == null ? 0 : canvas.Width / canvas.Height;
        if (picture != null) Select(Images.Centered(ratio, canvas.Width, canvas.Height));
    }

    void Select(Rect area)
    {
        selection = area;
        Redraw();
        ShowPixels();
    }

    // Draws the selection in its shape, with thirds lines and handles.
    void Redraw()
    {
        if (picture == null || selection.IsEmpty) return;
        var canvas = Get<Canvas>("Selection");
        double round = Get<Slider>("RoundSlider").Value;
        var outline = Get<System.Windows.Shapes.Rectangle>("Outline");
        Canvas.SetLeft(outline, selection.X);
        Canvas.SetTop(outline, selection.Y);
        outline.Width = selection.Width;
        outline.Height = selection.Height;
        Get<System.Windows.Shapes.Path>("Shade").Data = new CombinedGeometry(GeometryCombineMode.Exclude,
            new RectangleGeometry(new Rect(0, 0, canvas.Width, canvas.Height)), Images.Shaped(shape, selection, round));
        Get<System.Windows.Shapes.Path>("ShapeOutline").Data = Images.Shaped(shape, selection, round);

        var thirds = new GeometryGroup();
        for (int i = 1; i < 3; i++)
        {
            double x = selection.X + selection.Width * i / 3, y = selection.Y + selection.Height * i / 3;
            thirds.Children.Add(new LineGeometry(new Point(x, selection.Top), new Point(x, selection.Bottom)));
            thirds.Children.Add(new LineGeometry(new Point(selection.Left, y), new Point(selection.Right, y)));
        }
        Get<System.Windows.Shapes.Path>("Thirds").Data = thirds;

        var handles = new GeometryGroup { FillRule = FillRule.Nonzero };
        for (int hx = 0; hx < 3; hx++)
            for (int hy = 0; hy < 3; hy++)
                if (hx != 1 || hy != 1)
                    handles.Children.Add(new RectangleGeometry(new Rect(selection.X + selection.Width * hx / 2 - 5,
                        selection.Y + selection.Height * hy / 2 - 5, 10, 10), 2, 2));
        Get<System.Windows.Shapes.Path>("Handles").Data = handles;
        Changed();
    }

    // The crop rectangle in the pixels of any picture of the selection.
    static Int32Rect Area(Settings s, Picture p)
    {
        if (s.Preview.Width <= 0 || s.Selection.IsEmpty) return new Int32Rect();
        int w = p.Pixels.PixelWidth, h = p.Pixels.PixelHeight;
        var r = Images.Transfer(s.Selection, s.Preview.Width, s.Preview.Height, w, h, s.Ratio);
        return Images.ToPixels(r.TopLeft, r.BottomRight, 1, w, h);
    }

    void SetUpNaming()
    {
        var name = Get<TextBox>("NameBox");
        var use = Get<CheckBox>("UseSuffix");
        var suffix = Get<TextBox>("SuffixBox");
        if (Many) Get<TextBlock>("NameLabel").Text = "Ortak ad (boş bırakırsan her dosya kendi adını korur)";
        use.IsChecked = Naming.UseSuffix;
        suffix.Text = Naming.Suffix;
        suffix.IsEnabled = use.IsChecked == true;
        RoutedEventHandler toggled = (s, e) =>
        {
            suffix.IsEnabled = use.IsChecked == true;
            nameEdited = false;
            RememberSuffix();
            UpdateName();
        };
        use.Checked += toggled;
        use.Unchecked += toggled;
        suffix.TextChanged += (s, e) => { nameEdited = false; RememberSuffix(); UpdateName(); };
        name.TextChanged += (s, e) =>
        {
            if (syncing) return;
            nameEdited = true;
            if (Many) UpdateName(); else Validate();
        };
        UpdateName();
    }

    // The fixed suffix is a lasting setting: it also applies to the one-click options of the menu.
    void RememberSuffix()
    {
        bool use = Get<CheckBox>("UseSuffix").IsChecked == true;
        string suffix = Get<TextBox>("SuffixBox").Text;
        if (Naming.Problem("x" + suffix + "x") == null) Naming.Remember(use, suffix);
    }

    string OperationSuffix(Settings s, int width, int height)
    {
        if (mode == Mode.Compress) return "_sikistirilmis";
        if (mode == Mode.Crop) return "_kirpilmis";
        if (mode == Mode.Preset) return s.Preset.Suffix;
        if (mode != Mode.Scale) return "";
        return s.Typed == 0 ? "_" + s.Percent : "_" + width + "x" + height;
    }

    string ExtensionFor(Settings s, string path, Picture p)
    {
        if (mode == Mode.Preset) return Presets.Extension(s.Preset, path, p);
        if (s.Ext != null) return s.Ext;
        if (mode == Mode.Compress) return Images.CompressedFormat(path, p);
        bool shaped = s.Shape != CropShape.Rectangle;
        string ext = Images.Writable(path, shaped || p.Transparent);
        return shaped && ext == ".gif" ? ".png" : ext; // GIF would leave the edge of a shape jagged
    }

    void UpdateName()
    {
        var s = Read();
        string path = paths[index];
        // A preset may swap its format for a transparent picture, so only the picture can tell.
        string fixedExt = mode == Mode.Preset ? null : s.Ext;
        string extension = fixedExt ?? (picture != null ? ExtensionFor(s, path, picture) : "");
        Get<TextBlock>("Extension").Text = Many && fixedExt == null ? "" : extension;

        int w = 0, h = 0;
        if (mode == Mode.Scale && picture != null) Size(s, picture, out w, out h);
        string suggested = Naming.Compose(path, OperationSuffix(s, w, h), s.UseSuffix, s.Suffix);
        if (!Many && !nameEdited)
        {
            bool was = syncing;
            syncing = true;
            Get<TextBox>("NameBox").Text = suggested;
            syncing = was;
        }
        Get<TextBlock>("SuffixHint").Text = Many
            ? "Örnek: " + Naming.BatchName(path, Get<TextBox>("NameBox").Text, index, OperationSuffix(s, w, h), s.UseSuffix, s.Suffix) + extension
            : s.UseSuffix ? "Açıkken kaydedilen her dosya kendi adı + bu eki alır."
            : "Kapalıyken işleme göre ad önerilir. Aynı ad varsa (2) eklenir.";
        Validate();
    }

    Settings Read()
    {
        var canvas = Get<Canvas>("Selection");
        var s = new Settings
        {
            Ext = format,
            Quality = (int)Get<Slider>("QualitySlider").Value,
            Percent = (int)Math.Round(Get<Slider>("ScaleSlider").Value),
            Typed = typed,
            KeepRatio = Get<CheckBox>("LockRatio").IsChecked == true,
            UseSuffix = Get<CheckBox>("UseSuffix").IsChecked == true,
            Suffix = Get<TextBox>("SuffixBox").Text,
            Name = Get<TextBox>("NameBox").Text,
            Selection = selection,
            Preview = double.IsNaN(canvas.Width) ? new Size() : new Size(canvas.Width, canvas.Height),
            Ratio = ownRatio ? 0 : ratio,
            Shape = shape,
            Roundness = Get<Slider>("RoundSlider").Value,
            Turns = turns,
            Mirrored = mirrored,
            Zip = zip,
            ZipName = Get<TextBox>("ZipBox").Text,
            Method = method
        };
        int.TryParse(Get<TextBox>("TargetBox").Text, out s.TargetKb);
        int.TryParse(Get<TextBox>("WidthBox").Text, out s.Width);
        int.TryParse(Get<TextBox>("HeightBox").Text, out s.Height);
        if (mode == Mode.Preset)
        {
            s.Preset = new Preset { Name = Get<TextBox>("PresetNameBox").Text.Trim(), Format = format ?? "", Quality = s.Quality };
            // An empty box means "no limit"; anything that is not a number is reported.
            s.Preset.MaxSide = Number(Get<TextBox>("PresetSideBox").Text);
            s.Preset.LimitKb = Number(Get<TextBox>("PresetLimitBox").Text);
        }
        return s;
    }

    static int Number(string text)
    {
        int value;
        return text.Trim() == "" ? 0 : int.TryParse(text, out value) ? value : -1;
    }

    static void Size(Settings s, Picture p, out int width, out int height)
    {
        Images.TargetSize(p.Pixels.PixelWidth, p.Pixels.PixelHeight, s.Percent, s.Typed, s.Width, s.Height, s.KeepRatio,
            out width, out height);
    }

    string Invalid()
    {
        var s = Read();
        string archive = s.Zip && Naming.Problem(s.ZipName) != null ? "ZIP adı: " + Naming.Problem(s.ZipName) : null;
        return (Many && s.Name == "" ? null : Naming.Problem(s.Name)) ?? archive ?? SettingsProblem(s);
    }

    // Why no result can be computed; the file name does not matter for that.
    string SettingsProblem(Settings s)
    {
        if (s.Method == Method.Target && s.TargetKb < 5) return "Hedef en az 5 KB olmalı.";
        if (mode == Mode.Preset && s.Preset.Problem() != null) return s.Preset.Problem();
        if (picture == null) return null;
        if (mode == Mode.Scale)
        {
            // A typed field counts only if it holds a number; the other one may be derived per file.
            bool missing = (s.Typed == 1 && s.Width < 1) || (s.Typed == 2 && s.Height < 1)
                || (s.Typed != 0 && !s.KeepRatio && (s.Width < 1 || s.Height < 1));
            int w = 0, h = 0;
            if (!missing) Size(s, picture, out w, out h);
            if (missing || w < 1 || h < 1 || (long)w * h > Images.MaxPixels)
                return "Geçerli bir boyut yaz (en fazla 100 megapiksel).";
        }
        if (mode == Mode.Crop)
        {
            var area = Area(s, picture);
            if (area.Width < 1 || area.Height < 1) return "Kırpılacak alanı seç.";
        }
        return null;
    }

    void Validate()
    {
        string problem = Invalid();
        Show(problem);
        Get<Button>("Save").IsEnabled = problem == null && picture != null && !saving;
    }

    void Show(string problem)
    {
        var error = Get<TextBlock>("Error");
        error.Text = problem ?? "";
        error.Visibility = problem == null ? Visibility.Collapsed : Visibility.Visible;
    }

    void Load(int at)
    {
        index = at;
        int mine = ++loading;
        string path = paths[at];
        if (Many) Get<TextBlock>("Current").Text = (at + 1) + " / " + paths.Length + "  ·  " + Path.GetFileName(path);
        Images.Background(() =>
        {
            try
            {
                var loaded = Images.Load(path);
                Window.Dispatcher.BeginInvoke((Action)(() => { if (mine == loading) Loaded(loaded, path); }));
            }
            catch (Exception e)
            {
                Window.Dispatcher.BeginInvoke((Action)(() =>
                {
                    if (mine != loading) return;
                    var text = Get<TextBlock>("Loading");
                    text.Text = "Görsel açılamadı";
                    text.Visibility = Visibility.Visible;
                    Get<Grid>("Frame").Visibility = Visibility.Hidden;
                    Show(e.Message);
                }));
            }
        });
    }

    void Loaded(Picture loaded, string path)
    {
        var canvas = Get<Canvas>("Selection");
        var before = shownOnce ? new Size(canvas.Width, canvas.Height) : new Size();
        upright = loaded;
        picture = Turned(upright, turns, mirrored);
        int w = upright.Pixels.PixelWidth, h = upright.Pixels.PixelHeight; // as in the file
        Assets.Fit(Window, picture.Pixels);
        Comparing(false); // until the result of this picture is ready
        Get<TextBlock>("Loading").Visibility = Visibility.Collapsed;
        RadialMenu.Animate(Get<Image>("Preview"), UIElement.OpacityProperty, 0, 1, 300);
        string clear = picture.Transparent ? " · saydam" : "";
        Get<TextBlock>("Details").Text = Many
            ? paths.Length + " görsel · toplam " + Images.Size(paths.Sum(p => new FileInfo(p).Length)) + "\nGösterilen: " + w + " × " + h + clear
            : Path.GetFileName(path) + "\n" + w + " × " + h + " · " + Images.Size(new FileInfo(path).Length) + clear;

        if (!shownOnce)
        {
            var chips = Get<WrapPanel>("FormatChips").Children;
            if (mode == Mode.Compress && !Many && chips.Count == 2)
            {
                ((RadioButton)chips[0]).Content = picture.Transparent ? "PNG" : "JPG";
                if (Images.CompressedFormat(path, picture) == ".webp") ((RadioButton)chips[1]).IsChecked = true;
            }
            if (picture.Transparent && mode == Mode.Format)
            {
                // Preselect a format that can keep the clear background.
                string suggested = Images.Writable(path) == ".png" ? "TIFF" : "PNG";
                foreach (RadioButton chip in chips) if ((string)chip.Content == suggested) chip.IsChecked = true;
            }
        }

        if (mode == Mode.Scale)
        {
            if (typed == 0) SizeFromPercent();
            else SizeTyped(typed == 1);
        }
        if (mode == Mode.Crop)
        {
            canvas.Visibility = Visibility.Visible;
            double carried = ownRatio ? 0 : ratio;
            if (ownRatio) ratio = canvas.Width / canvas.Height;
            Select(before.Width > 0
                ? Images.Transfer(selection, before.Width, before.Height, canvas.Width, canvas.Height, carried)
                : Images.Centered(ratio, canvas.Width, canvas.Height));
        }
        shownOnce = true;
        Get<TextBlock>("SizeBefore").Text = Images.Size(new FileInfo(path).Length);
        Get<TextBlock>("DimensionsBefore").Text = w + " × " + h;
        Get<TextBlock>("KindBefore").Text = Images.Kind(Path.GetExtension(path));
        Show(new Report()); // nothing of the previous picture's result may stay
        Get<Border>("Info").Visibility = Visibility.Visible;
        UpdateName();
        Changed();
    }

    void Show(Report report)
    {
        Get<TextBlock>("SizeAfter").Text = "→ " + report.Size;
        Get<TextBlock>("DimensionsAfter").Text = "→ " + report.Dimensions;
        Get<TextBlock>("KindAfter").Text = "→ " + report.Kind;
        Get<StackPanel>("QualityInfo").Visibility = report.Quality == null ? Visibility.Hidden : Visibility.Visible;
        Get<TextBlock>("QualityAfter").Text = report.Quality;
        var summary = Get<TextBlock>("Result");
        summary.Text = (Many && report.Summary != null ? "Bu görsel · " : "") + report.Summary;
        summary.Foreground = Tint(report.Trend);
        var note = Get<TextBlock>("Note");
        note.Text = report.Note;
        note.Visibility = note.Text == "" ? Visibility.Collapsed : Visibility.Visible;
        Get<Grid>("Values").Opacity = 1;
    }

    static Brush Tint(int trend)
    {
        return new SolidColorBrush(trend < 0 ? Color.FromRgb(0x7E, 0xE2, 0xA8)
            : trend > 0 ? Color.FromRgb(0xFF, 0xB8, 0x6B) : Color.FromRgb(0xF2, 0xF3, 0xF5));
    }

    // Everything that changes the size of the saved files; which picture is shown does not belong to it.
    string TotalKey(Settings s)
    {
        string key = s.Ext + "|" + s.Method + "|"
            + (s.Method == Method.Manual ? s.Quality : s.Method == Method.Target ? s.TargetKb : 0);
        // The name of a preset does not change any size.
        if (mode == Mode.Preset) key += "|" + s.Preset.MaxSide + " " + s.Preset.Format + " " + s.Preset.LimitKb;
        // With a locked ratio the field that was not typed differs from picture to picture.
        if (mode == Mode.Scale)
            key += "|" + (s.Typed == 0 ? "%" + s.Percent
                : s.Typed + " " + (s.KeepRatio ? (s.Typed == 1 ? s.Width : s.Height).ToString() : s.Width + "x" + s.Height));
        if (mode == Mode.Crop && s.Preview.Width > 0 && !s.Selection.IsEmpty)
            key += string.Format("|{0} {1:0.000} {2:0.000} {3:0.000} {4:0.000} {5} {6:0}", s.Ratio,
                s.Selection.X / s.Preview.Width, s.Selection.Y / s.Preview.Height,
                s.Selection.Width / s.Preview.Width, s.Selection.Height / s.Preview.Height,
                s.Shape, s.Shape == CropShape.Rounded ? s.Roundness : 0)
                + " " + s.Turns + s.Mirrored;
        return key;
    }

    // Batch only: what saving the whole selection will do to its size. One worker at a time, started after the preview.
    // shortcut: every settings change recompresses every file; estimate from a sample if large selections feel slow.
    void Total()
    {
        if (!Many || closed || saving || busy || totalBusy || debounce.IsEnabled || picture == null) return;
        var s = Read();
        if (SettingsProblem(s) != null || TotalKey(s) == totalKey) return;
        totalKey = TotalKey(s);
        totalBusy = true;
        int run = ++totalRun;
        var line = Get<TextBlock>("Total");
        Images.Background(() =>
        {
            System.Threading.Thread.CurrentThread.Priority = System.Threading.ThreadPriority.BelowNormal;
            long before = 0, after = 0;
            int saved = 0, skipped = 0, failed = 0;
            for (int i = 0; i < paths.Length && run == totalRun && !closed; i++)
            {
                int at = i;
                Window.Dispatcher.BeginInvoke((Action)(() =>
                {
                    if (run != totalRun) return;
                    line.Foreground = Tint(0);
                    line.Text = "Tüm seçim hesaplanıyor… " + (at + 1) + " / " + paths.Length;
                }));
                // The same files are left out as when saving.
                try
                {
                    long size = new FileInfo(paths[i]).Length;
                    if (s.Method == Method.Target && size <= s.TargetKb * 1024L) { skipped++; continue; }
                    var p = Turned(Images.Load(paths[i]), s.Turns, s.Mirrored);
                    string ext = ExtensionFor(s, paths[i], p);
                    var made = Make(s, p, ext);
                    long result = made.Data.Length;
                    if ((mode == Mode.Compress && result >= size)
                        || (mode == Mode.Preset && Presets.Skip(s.Preset, paths[i], p, ext, made) != null)) { skipped++; continue; }
                    saved++;
                    before += size;
                    after += result;
                }
                catch (Exception) { failed++; }
            }
            string text = saved == 0 ? "Tüm seçim: hiçbir dosya kaydedilmeyecek"
                : "Tüm seçim (" + saved + " dosya): " + Images.Size(before) + " → " + Images.Size(after)
                    + " · " + Images.Difference(before, after);
            if (skipped > 0) text += " · " + skipped + " atlanacak";
            if (failed > 0) text += " · " + failed + " dosya işlenemiyor";
            Window.Dispatcher.BeginInvoke((Action)(() =>
            {
                totalBusy = false;
                if (run != totalRun) { Total(); return; }
                line.Foreground = Tint(after.CompareTo(before));
                line.Text = text;
            }));
        });
    }

    // Stops the running total and forgets the shown one.
    void DropTotal(string text)
    {
        totalRun++;
        totalKey = null;
        var line = Get<TextBlock>("Total");
        line.Foreground = Tint(0);
        line.Text = text;
    }

    void Changed()
    {
        if (picture == null) return;
        if (Many && TotalKey(Read()) != totalKey) DropTotal("Tüm seçim: hesaplanacak…");
        // The values on screen belong to the old settings until the new result is ready.
        Get<Grid>("Values").Opacity = 0.45;
        Validate();
        debounce.Stop();
        debounce.Start();
    }

    BitmapSource Render(Settings s, Picture p, string ext)
    {
        if (mode == Mode.Scale)
        {
            int w, h;
            Size(s, p, out w, out h);
            return Images.Resize(p.Pixels, w, h);
        }
        if (mode == Mode.Compress && ext == ".png") return Images.Simplify(p.Pixels, s.Quality);
        if (mode != Mode.Crop) return p.Pixels;
        var cut = Images.Cut(p.Pixels, Area(s, p));
        return s.Shape == CropShape.Rectangle ? cut : Images.Masked(cut, s.Shape, s.Roundness);
    }

    // Encodes the shown picture in the background to show its real size and look; only the newest settings matter.
    void Refresh()
    {
        if (picture == null) return;
        if (SettingsProblem(Read()) != null) { Get<TextBlock>("Result").Text = ""; DropTotal(""); return; }
        if (busy) { dirty = true; return; }
        busy = true;
        var s = Read();
        var current = picture;
        string path = paths[index];
        if (!saving) Get<TextBlock>("Result").Text = s.Method == Method.Manual ? "Hesaplanıyor…" : "En uygun ayar aranıyor…";
        Images.Background(() =>
        {
            BitmapSource look = null;
            var report = new Report();
            try
            {
                string ext = ExtensionFor(s, path, current);
                long before = new FileInfo(path).Length;
                report.Kind = Images.Kind(ext);
                if (s.Method == Method.Target && before <= s.TargetKb * 1024L)
                {
                    report.Summary = "Kaydedilmeyecek";
                    report.Note = "Bu görsel zaten " + s.TargetKb + " KB altında.";
                }
                else
                {
                    var found = Make(s, current, ext);
                    long after = found.Data.Length;
                    report.Size = Images.Size(after);
                    report.Dimensions = found.Width + " × " + found.Height;
                    if (ext == ".jpg" || ext == ".jpeg" || ext == ".webp") report.Quality = found.Quality.ToString();
                    report.Summary = Images.Difference(before, after);
                    report.Trend = after.CompareTo(before);
                    string skip = mode == Mode.Preset ? Presets.Skip(s.Preset, path, current, ext, found) : null;
                    if (mode == Mode.Compress && after >= before) report.Note = "Küçülmediği için kaydedilmeyecek.";
                    else if (skip != null) report.Note = skip + " Kaydedilmeyecek.";
                    else if (current.Transparent && (ext == ".jpg" || ext == ".jpeg" || ext == ".bmp"))
                        report.Note = "Saydam alanlar beyaz olacak.";
                    else if (found.Resized) report.Note = "Hedefe sığması için görsel de küçültüldü.";
                    else if (mode == Mode.Crop && s.Shape != CropShape.Rectangle) report.Note = "Şeklin dışı saydam olur.";
                    if (mode != Mode.Crop) look = Images.Decode(found.Data, ext);
                }
            }
            catch (Exception e)
            {
                Log.Error("önizleme: " + path, e);
                report.Summary = "Önizleme hazırlanamadı";
                report.Note = e.Message;
            }
            Window.Dispatcher.BeginInvoke((Action)(() =>
            {
                busy = false;
                if (dirty || current != picture) { dirty = false; Refresh(); return; }
                if (debounce.IsEnabled) return; // newer settings are about to be computed
                if (!saving) Show(report);
                Total();
                if (mode == Mode.Crop) return;
                Get<Image>("Preview").Source = look ?? current.Pixels;
                Comparing(look != null);
            }));
        });
    }

    // What the settings make of one picture, without saving it.
    Candidate Make(Settings s, Picture p, string ext)
    {
        if (mode == Mode.Preset) return Presets.Make(s.Preset, p, ext);
        if (s.Method != Method.Manual) return Search(s, p, ext);
        var pixels = Render(s, p, ext);
        return new Candidate { Data = Images.Encode(pixels, p.Colors, ext, s.Quality),
            Quality = s.Quality, Width = pixels.PixelWidth, Height = pixels.PixelHeight };
    }

    Candidate Search(Settings s, Picture p, string ext)
    {
        return s.Method == Method.Smart ? Images.Smart(p.Pixels, p.Colors, ext)
            : Images.UnderSize(p.Pixels, p.Colors, ext, s.TargetKb * 1024L);
    }

    void Save()
    {
        if (picture == null || saving || Invalid() != null) return;
        saving = true;
        if (totalBusy) DropTotal(""); // the processor belongs to saving now
        var button = Get<Button>("Save");
        button.IsEnabled = false;
        button.Content = "Kaydediliyor…";
        var s = Read();
        var shown = picture;
        string shownPath = paths[index];
        var confirm = Images.Once(Images.AskWhiteBackground);
        var bundle = s.Zip ? new Bundle(Path.GetDirectoryName(paths[0]), s.ZipName) : null;
        bool stopped = false;
        Func<string, int, Outcome> job = (path, i) =>
        {
            var p = path == shownPath ? shown : Turned(Images.Load(path), s.Turns, s.Mirrored);
            string ext = ExtensionFor(s, path, p);
            bool searched = s.Method != Method.Manual;
            if (s.Method == Method.Target && new FileInfo(path).Length <= s.TargetKb * 1024L)
                return new Outcome { Text = "Dosya zaten " + s.TargetKb + " KB altında." };
            if (mode == Mode.Preset)
            {
                var made = Make(s, p, ext);
                string skip = Presets.Skip(s.Preset, path, p, ext, made);
                if (skip != null) return new Outcome { Text = skip };
                string own = !Many ? s.Name : Naming.BatchName(path, s.Name, i, s.Preset.Suffix, s.UseSuffix, s.Suffix);
                return Images.SaveEncoded(path, made.Data, ext, own, false, bundle);
            }
            var pixels = searched ? p.Pixels : Render(s, p, ext);
            string name = !Many ? s.Name : Naming.BatchName(path, s.Name, i,
                OperationSuffix(s, pixels.PixelWidth, pixels.PixelHeight), s.UseSuffix, s.Suffix);
            if (!searched) return Images.Export(path, p, pixels, ext, s.Quality, name, mode == Mode.Compress, confirm, bundle);
            var found = Search(s, p, ext);
            return Images.Noted(Images.SaveEncoded(path, found.Data, ext, name, true, bundle), found);
        };
        Images.Background(() =>
        {
            Outcome outcome = null;
            string failure = null;
            try
            {
                outcome = !Many ? job(paths[0], 0) : Images.Batch(paths, job,
                    i => Window.Dispatcher.BeginInvoke((Action)(() => button.Content = "Kaydediliyor " + (i + 1) + "/" + paths.Length + "…")),
                    () => stopped = closed);
                if (bundle != null)
                {
                    // Closing the window cancels: half an archive is not kept.
                    if (stopped) outcome = null;
                    else bundle.Finish(outcome);
                }
            }
            catch (Exception e)
            {
                Log.Error("kaydetme: " + shownPath, e);
                outcome = null;
                failure = "Kaydedilemedi: " + e.Message;
            }
            finally { if (bundle != null) bundle.Dispose(); }
            Window.Dispatcher.BeginInvoke((Action)(() =>
            {
                saving = false;
                button.Content = "Kaydet";
                if (outcome != null && outcome.File != null)
                {
                    Images.Notify(outcome);
                    // Stay open when some files failed, so the reason can be read.
                    if (outcome.Failed == 0) { Window.Close(); return; }
                }
                Validate();
                Show(failure ?? (outcome == null ? null : outcome.Text));
                Total();
            }));
        });
    }

    public static void Open(string[] files, Mode mode)
    {
        Assets.Show(new Editor(files, mode).Window);
    }
}

// The window of a video tool: a frame of the video on the left, settings and progress on the right.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Path = System.IO.Path;

class VideoWindow
{
    public readonly Window Window = Assets.Window("Editor.xaml");
    public readonly VideoJob Job; // what the controls currently say; read again before running
    readonly string[] paths;
    readonly DispatcherTimer debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
    int index, loading;
    bool running, nameEdited, syncing;
    volatile bool cancel;

    bool Joins { get { return Job.Task == VideoTask.Merge; } }
    bool Many { get { return paths.Length > 1 && !Joins; } } // a batch: one result for each file

    T Get<T>(string name) where T : class { return (T)Window.FindName(name); }

    // preset: the values the window starts with. start: begin at once (the one-click options of the menu).
    public VideoWindow(string[] files, VideoJob preset, bool start)
    {
        paths = (string[])files.Clone(); // joining changes the order
        Job = preset;
        Assets.Dress(Window);
        string title = Job.Task == VideoTask.Compress ? Lang.T("Video Sıkıştır") : Job.Task == VideoTask.Gif ? Lang.T("GIF Yap")
            : Job.Task == VideoTask.Audio ? Lang.T("Sesi Ayır") : Job.Task == VideoTask.Trim ? Lang.T("Video Kes") : Lang.T("Video Birleştir");
        Window.Title = title + " - " + (paths.Length > 1 ? paths.Length + Lang.T(" video") : Path.GetFileName(paths[0]));
        Get<TextBlock>("Heading").Text = title;
        Get<Canvas>("Selection").Visibility = Visibility.Collapsed;
        if (Many) Assets.Strip(Window, paths, Pick);
        SetUpPanels();
        SetUpNaming();
        if (Joins) Assets.Order(Window, paths, Pick, UpdateName);

        var save = Get<Button>("Save");
        save.Content = Lang.T("Başlat");
        save.Click += (s, e) => Start();
        var close = Get<Button>("Cancel");
        close.Content = Lang.T("Kapat");
        close.Click += (s, e) =>
        {
            if (running) cancel = true; // stops the work, the window stays
            else Window.Close();
        };
        Window.Closed += (s, e) => cancel = true;
        debounce.Tick += (s, e) => { debounce.Stop(); Pick(index); };
        Window.Loaded += (s, e) =>
        {
            Pick(0);
            if (start) Start();
        };
        Validate();
    }

    void Chips<TValue>(string panel, string group, TValue picked, Action<TValue> pick, params Tuple<string, TValue>[] choices)
    {
        foreach (var choice in choices)
        {
            var value = choice.Item2;
            var chip = new RadioButton { Content = choice.Item1, GroupName = group, IsChecked = Equals(value, picked) };
            chip.SetResourceReference(FrameworkElement.StyleProperty, "Chip");
            chip.Checked += (s, e) => pick(value);
            Get<WrapPanel>(panel).Children.Add(chip);
        }
    }

    static Tuple<string, TValue> Choice<TValue>(string label, TValue value) { return Tuple.Create(label, value); }

    void SetUpPanels()
    {
        string panel = Job.Task == VideoTask.Compress ? "VideoCompressPanel" : Job.Task == VideoTask.Gif ? "VideoGifPanel"
            : Job.Task == VideoTask.Audio ? "VideoAudioPanel" : Job.Task == VideoTask.Trim ? "VideoTrimPanel" : null;
        if (panel != null) Get<StackPanel>(panel).Visibility = Visibility.Visible;

        var begin = Get<TextBox>("TrimStartBox");
        var end = Get<TextBox>("TrimEndBox");
        begin.Text = Job.Start.ToString("0.###");
        end.Text = Job.End.ToString("0.###");
        begin.TextChanged += (s, e) => { Validate(); debounce.Stop(); debounce.Start(); };
        end.TextChanged += (s, e) => Validate();
        Chips("TrimChips", "tr", Job.Exact, value =>
        {
            Job.Exact = value;
            Get<TextBlock>("Extension").Text = Job.ExtensionFor(paths[0]);
        }, Choice(Lang.T("Hızlı"), false), Choice(Lang.T("Hassas"), true));

        // 0 stands for "aim at a size".
        Chips("VideoQualityChips", "vq", Job.TargetMb > 0 ? 0 : Job.Crf, value =>
        {
            Get<StackPanel>("VideoTargetPanel").Visibility = value == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (value != 0) Job.Crf = value;
            Job.TargetMb = value == 0 ? 1 : 0;
            Validate();
        }, Choice(Lang.T("Yüksek"), 23), Choice(Lang.T("Orta"), 28), Choice(Lang.T("Küçük"), 33), Choice(Lang.T("Hedef boyut"), 0));
        var targetBox = Get<TextBox>("VideoTargetBox");
        if (Job.TargetMb > 0)
        {
            targetBox.Text = Job.TargetMb.ToString();
            Get<StackPanel>("VideoTargetPanel").Visibility = Visibility.Visible;
        }
        targetBox.TextChanged += (s, e) => Validate();
        Chips("VideoSizeChips", "vs", Job.ShortSide, value => Job.ShortSide = value,
            Choice(Lang.T("Aynı"), 0), Choice("1080p", 1080), Choice("720p", 720), Choice("480p", 480));
        var mute = Get<CheckBox>("VideoMute");
        mute.IsChecked = Job.Mute;
        mute.Click += (s, e) => Job.Mute = mute.IsChecked == true;

        var from = Get<TextBox>("GifStartBox");
        var length = Get<TextBox>("GifLengthBox");
        from.Text = Job.Start.ToString("0.###");
        length.Text = Job.Length.ToString("0.###");
        from.TextChanged += (s, e) => { Validate(); debounce.Stop(); debounce.Start(); }; // the preview follows the start
        length.TextChanged += (s, e) => Validate();
        Chips("GifWidthChips", "gw", Job.Width, value => Job.Width = value,
            Choice("320", 320), Choice("480", 480), Choice("640", 640), Choice(Lang.T("Aynı"), 0));
        Chips("GifRateChips", "gr", Job.Rate, value => Job.Rate = value, Choice("10", 10), Choice("15", 15), Choice("24", 24));

        Chips("AudioChips", "au", Job.AudioExt, value =>
        {
            Job.AudioExt = value;
            Get<TextBlock>("Extension").Text = value;
        }, Choice("MP3", ".mp3"), Choice("M4A", ".m4a"), Choice("WAV", ".wav"));
    }

    void SetUpNaming()
    {
        var name = Get<TextBox>("NameBox");
        var use = Get<CheckBox>("UseSuffix");
        var suffix = Get<TextBox>("SuffixBox");
        if (Many) Get<TextBlock>("NameLabel").Text = Lang.T("Ortak ad (boş bırakırsan her dosya kendi adını korur)");
        use.IsChecked = Naming.UseSuffix;
        suffix.Text = Naming.Suffix;
        suffix.IsEnabled = use.IsChecked == true;
        Action changed = () =>
        {
            suffix.IsEnabled = use.IsChecked == true;
            nameEdited = false;
            if (Naming.Problem("x" + suffix.Text + "x") == null) Naming.Remember(use.IsChecked == true, suffix.Text);
            UpdateName();
        };
        use.Click += (s, e) => changed();
        suffix.TextChanged += (s, e) => changed();
        name.TextChanged += (s, e) =>
        {
            if (syncing) return;
            nameEdited = true;
            Validate();
        };
        // Joined videos keep their own kind of file when they are alike, so the ending is known only then.
        Get<TextBlock>("Extension").Text = Joins ? "" : Job.ExtensionFor(paths[0]);
        UpdateName();
    }

    void UpdateName()
    {
        if (!Many && !nameEdited)
        {
            syncing = true;
            Get<TextBox>("NameBox").Text = Naming.Compose(paths[0], Job.Suffix,
                Get<CheckBox>("UseSuffix").IsChecked == true, Get<TextBox>("SuffixBox").Text);
            syncing = false;
        }
        Validate();
    }

    // Moves the text fields into the job; returns what is wrong with them, or null.
    public string ReadJob()
    {
        if (Job.Task == VideoTask.Compress && Job.TargetMb > 0)
        {
            int target;
            if (!int.TryParse(Get<TextBox>("VideoTargetBox").Text, out target) || target < 1) return Lang.T("Hedef en az 1 MB olmalı.");
            Job.TargetMb = target;
        }
        if (Job.Task == VideoTask.Gif)
        {
            double? from = Video.Time(Get<TextBox>("GifStartBox").Text), length = Video.Time(Get<TextBox>("GifLengthBox").Text);
            if (from == null || length == null) return Lang.T("Süreleri saniye veya dk:sn olarak yaz.");
            Job.Start = from.Value;
            Job.Length = length.Value;
        }
        if (Job.Task == VideoTask.Trim)
        {
            double? from = Video.Time(Get<TextBox>("TrimStartBox").Text), to = Video.Time(Get<TextBox>("TrimEndBox").Text);
            if (from == null || to == null) return Lang.T("Süreleri saniye veya dk:sn olarak yaz.");
            if (to > 0 && to <= from) return Lang.T("Bitiş başlangıçtan sonra olmalı.");
            Job.Start = from.Value;
            Job.End = to.Value;
        }
        if (Joins && paths.Length < 2) return Lang.T("Birleştirmek için en az iki video seç.");
        string name = Get<TextBox>("NameBox").Text;
        return Many && name == "" ? null : Naming.Problem(name);
    }

    void Validate()
    {
        if (running) return;
        string problem = ReadJob();
        Error(problem);
        Get<Button>("Save").IsEnabled = problem == null;
    }

    void Error(string problem)
    {
        var error = Get<TextBlock>("Error");
        error.Text = problem ?? "";
        error.Visibility = problem == null ? Visibility.Collapsed : Visibility.Visible;
    }

    // Shows a frame and the facts of the chosen video.
    void Pick(int at)
    {
        index = at;
        int mine = ++loading;
        string path = paths[at];
        if (Many) Get<TextBlock>("Current").Text = (at + 1) + " / " + paths.Length + "  ·  " + Path.GetFileName(path);
        double from = Job.Task == VideoTask.Gif || Job.Task == VideoTask.Trim ? Job.Start : -1;
        Images.Background(() =>
        {
            System.Windows.Media.Imaging.BitmapSource frame = null;
            string facts;
            try
            {
                var info = Video.Probe(path);
                facts = info.Describe + " · " + Images.Size(new FileInfo(path).Length);
                // A frame a little way in says more than the often black first one.
                frame = info.VideoCodec == null ? null : Video.Frame(path, from >= 0 ? from : info.Seconds / 10);
            }
            catch (Exception e) { facts = e.Message; }
            Window.Dispatcher.BeginInvoke((Action)(() =>
            {
                if (mine != loading) return;
                Get<TextBlock>("Details").Text = (Many ? paths.Length + Lang.T(" video · gösterilen:\n") : Path.GetFileName(path) + "\n") + facts;
                var wait = Get<TextBlock>("Loading");
                wait.Text = Lang.T("Önizleme yok");
                wait.Visibility = frame == null ? Visibility.Visible : Visibility.Collapsed;
                if (frame == null) Get<Grid>("Frame").Visibility = Visibility.Hidden;
                else
                {
                    Assets.Fit(Window, frame);
                    Get<Canvas>("Selection").Visibility = Visibility.Collapsed;
                }
            }));
        });
    }

    void Start()
    {
        if (running || ReadJob() != null) return;
        running = true;
        cancel = false;
        var save = Get<Button>("Save");
        save.IsEnabled = false;
        Get<Button>("Cancel").Content = Lang.T("İptal");
        Get<ScrollViewer>("Settings").IsEnabled = false;
        Error(null);
        var bar = Get<ProgressBar>("Progress");
        var status = Get<TextBlock>("Status");
        bar.Value = 0;
        bar.Visibility = status.Visibility = Visibility.Visible;
        status.Text = Lang.T("Hazırlanıyor…");

        string name = Get<TextBox>("NameBox").Text, suffix = Get<TextBox>("SuffixBox").Text;
        bool use = Get<CheckBox>("UseSuffix").IsChecked == true;
        var job = Job;
        var order = (string[])paths.Clone();
        int count = Many ? order.Length : 1;
        Func<int, Action<double>> report = i =>
        {
            int shown = -1;
            return p =>
            {
                int percent = (int)(p * 100);
                if (percent == shown) return;
                shown = percent;
                Window.Dispatcher.BeginInvoke((Action)(() =>
                {
                    if (!running) return;
                    bar.Value = (i + p) / count;
                    status.Text = (Many ? (i + 1) + " / " + count + " · " : "") + Lang.Percent(percent);
                }));
            };
        };
        Func<string, int, Outcome> one = (path, i) => Video.Run(job, path,
            !Many ? name : Naming.BatchName(path, name, i, job.Suffix, use, suffix), report(i), () => cancel);
        Images.Background(() =>
        {
            Outcome outcome = null;
            string failure = null;
            try
            {
                outcome = job.Task == VideoTask.Merge ? Video.Merge(order, name, report(0), () => cancel)
                    : !Many ? one(order[0], 0) : Images.Batch(order, (path, i) =>
                {
                    // A cancelled file is neither saved nor an error; the batch stops before the next one.
                    try { return one(path, i); }
                    catch (OperationCanceledException) { return null; }
                }, null, () => cancel);
            }
            catch (OperationCanceledException) { }
            catch (Exception e)
            {
                Log.Error("video: " + paths[0], e);
                failure = e.Message;
            }
            Window.Dispatcher.BeginInvoke((Action)(() =>
            {
                running = false;
                bool stopped = cancel;
                Get<ScrollViewer>("Settings").IsEnabled = true;
                Get<Button>("Cancel").Content = Lang.T("Kapat");
                bar.Visibility = Visibility.Collapsed;
                status.Visibility = Visibility.Collapsed;
                if (outcome != null && outcome.File != null)
                {
                    Images.Notify(outcome);
                    if (outcome.Failed == 0 && !stopped) { Window.Close(); return; }
                }
                Validate();
                if (stopped)
                {
                    status.Text = Lang.T("İptal edildi") + (outcome != null && outcome.Saved > 0 ? Lang.T("; biten ") + outcome.Saved + Lang.T(" dosya duruyor.") : ".");
                    status.Visibility = Visibility.Visible;
                }
                else Error(failure ?? (outcome == null ? null : outcome.Text));
            }));
        });
    }

    public static void Open(string[] files, VideoJob preset, bool start)
    {
        Assets.Show(new VideoWindow(files, preset, start).Window);
    }
}

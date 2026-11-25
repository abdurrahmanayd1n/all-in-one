// Text from pictures with the recognition engine built into Windows: nothing leaves the computer.
// The window shows the picture on the left and its text, ready to correct and copy, on the right.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Foundation;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Security.Cryptography;
using Path = System.IO.Path;
using BitmapAlphaMode = Windows.Graphics.Imaging.BitmapAlphaMode;
using BitmapPixelFormat = Windows.Graphics.Imaging.BitmapPixelFormat;

static class Ocr
{
    // Small print reads far better enlarged: pictures are doubled, but not past this length of the long side.
    // Measured with 11 to 32 px text in three fonts: 1x 92%, 1.5x 97.7%, 2x 98.1%, 3x 98.0% of characters right.
    const double Comfortable = 4000;

    public static Language[] Languages { get { return OcrEngine.AvailableRecognizerLanguages.ToArray(); } }

    // The lines of text found in the picture; empty when there are none. language null: the user's own.
    public static string Read(Picture picture, Language language)
    {
        var engine = language == null ? OcrEngine.TryCreateFromUserProfileLanguages() : OcrEngine.TryCreateFromLanguage(language);
        if (engine == null)
            throw new NotSupportedException("Windows'ta metin tanıma dili kurulu değil. Ayarlar > Saat ve dil > Dil bölümünden ekle.");
        var pixels = Prepared(picture);
        int w = pixels.PixelWidth, h = pixels.PixelHeight;
        var data = new byte[w * h * 4];
        pixels.CopyPixels(data, w * 4, 0);
        using (var bitmap = SoftwareBitmap.CreateCopyFromBuffer(CryptographicBuffer.CreateFromByteArray(data),
            BitmapPixelFormat.Bgra8, w, h, BitmapAlphaMode.Premultiplied))
        {
            // The .NET helpers for WinRT tasks do not accept the metadata files that ship with Windows, so wait by hand.
            var work = engine.RecognizeAsync(bitmap);
            while (work.Status == AsyncStatus.Started) System.Threading.Thread.Sleep(10);
            if (work.Status != AsyncStatus.Completed) throw new IOException("Metin okunamadı.", work.ErrorCode);
            return string.Join(Environment.NewLine, work.GetResults().Lines.Select(line => line.Text));
        }
    }

    // An opaque picture of a size the engine reads well.
    static BitmapSource Prepared(Picture picture)
    {
        var source = picture.Pixels;
        int longest = Math.Max(source.PixelWidth, source.PixelHeight);
        double scale = Math.Min(Math.Max(1, Math.Min(2, Comfortable / longest)), (double)OcrEngine.MaxImageDimension / longest);
        if (scale != 1) source = Images.Resize(source, Images.Percent(source.PixelWidth, scale * 100),
            Images.Percent(source.PixelHeight, scale * 100));
        if (!picture.Transparent) return source;

        var data = new byte[source.PixelWidth * source.PixelHeight * 4];
        source.CopyPixels(data, source.PixelWidth * 4, 0);
        long light = 0, solid = 0;
        for (int i = 0; i < data.Length; i += 4)
            if (data[i + 3] > 128) { light += (data[i] + data[i + 1] + data[i + 2]) / 3; solid++; }
        // Light writing on a clear background needs a dark one behind it.
        int back = solid > 0 && light / solid > 128 ? 0 : 255;
        for (int i = 0; i < data.Length; i += 4)
        {
            int a = data[i + 3];
            for (int c = 0; c < 3; c++) data[i + c] = (byte)((data[i + c] * a + back * (255 - a)) / 255);
            data[i + 3] = 255;
        }
        return BitmapSource.Create(source.PixelWidth, source.PixelHeight, 96, 96, PixelFormats.Bgra32, null, data,
            source.PixelWidth * 4);
    }

    // The texts of several pictures in one, each under its file name; a single picture stays as it is.
    public static string Combined(string[] files, string[] texts)
    {
        if (files.Length == 1) return texts[0] ?? "";
        return string.Join(Environment.NewLine + Environment.NewLine, files
            .Select((file, i) => string.IsNullOrWhiteSpace(texts[i]) ? null
                : "— " + Path.GetFileName(file) + " —" + Environment.NewLine + texts[i])
            .Where(block => block != null));
    }

    // Needs a single-threaded apartment. Retries while another program holds the clipboard.
    public static void ToClipboard(string text)
    {
        System.Windows.Forms.Clipboard.SetDataObject(text, true, 10, 100);
    }

    // The one-click option of the menu.
    public static Outcome Copy(string[] files)
    {
        var texts = new string[files.Length];
        int failed = 0;
        string problem = null;
        for (int i = 0; i < files.Length; i++)
        {
            try { texts[i] = Read(Images.Load(files[i]), null); }
            catch (Exception e)
            {
                failed++;
                problem = problem ?? Path.GetFileName(files[i]) + ": " + e.Message;
            }
        }
        string all = Combined(files, texts);
        string errors = failed == 0 ? "" : "\n" + failed + " dosya okunamadı. " + problem;
        if (string.IsNullOrWhiteSpace(all)) return new Outcome { Text = "Görselde metin bulunamadı." + errors };
        ToClipboard(all);
        var report = Copied(files.Length > 1 ? texts.Count(t => !string.IsNullOrWhiteSpace(t)) + " görselden alındı"
            : all.Split('\n')[0]);
        report.Text += errors;
        return report;
    }

    // What to tell after text has reached the clipboard.
    public static Outcome Copied(string first)
    {
        first = first.Trim();
        return new Outcome { Text = "Metin panoya kopyalandı\n" + (first.Length > 80 ? first.Substring(0, 80) + "…" : first) };
    }
}

class Reader
{
    public readonly Window Window = Assets.Window("Editor.xaml");
    readonly string[] paths;
    readonly string[] texts; // null while a picture is still to be read
    readonly string[] problems;
    Language language;
    int index, loading, generation;
    bool showing;
    volatile bool closed;

    bool Many { get { return paths.Length > 1; } }

    T Get<T>(string name) where T : class { return (T)Window.FindName(name); }

    public Reader(string[] files)
    {
        paths = files;
        texts = new string[files.Length];
        problems = new string[files.Length];
        Assets.Dress(Window);
        Window.Title = "Metni Al - " + (Many ? paths.Length + " görsel" : Path.GetFileName(paths[0]));
        Get<TextBlock>("Heading").Text = "Metni Al";
        Get<ScrollViewer>("Settings").Visibility = Visibility.Collapsed;
        Get<DockPanel>("ReaderPanel").Visibility = Visibility.Visible;

        var copy = Get<Button>("Save");
        copy.Content = "Kopyala";
        copy.Click += (s, e) => Copy(Get<TextBox>("Recognized").Text);
        Get<Button>("Cancel").Content = "Kapat";
        Get<Button>("Cancel").Click += (s, e) => Window.Close();
        Get<TextBox>("Recognized").TextChanged += (s, e) =>
        {
            if (showing || texts[index] == null) return;
            texts[index] = Get<TextBox>("Recognized").Text; // corrections stay with their picture
            Buttons();
        };
        if (Many)
        {
            Assets.Strip(Window, paths, Pick);
            var all = Get<Button>("Extra");
            all.Content = "Tümünü kopyala";
            all.Visibility = Visibility.Visible;
            all.Click += (s, e) => Copy(Ocr.Combined(paths, texts));
        }
        SetUpLanguages();
        Window.Closed += (s, e) => closed = true;
        Window.Loaded += (s, e) =>
        {
            Pick(0);
            Recognize();
        };
    }

    void SetUpLanguages()
    {
        var installed = Ocr.Languages;
        if (installed.Length < 2) return;
        Get<StackPanel>("LanguagePanel").Visibility = Visibility.Visible;
        var own = Windows.Media.Ocr.OcrEngine.TryCreateFromUserProfileLanguages();
        foreach (var each in installed)
        {
            var value = each;
            var chip = new RadioButton { Content = value.DisplayName, GroupName = "language",
                IsChecked = own != null && own.RecognizerLanguage.LanguageTag == value.LanguageTag };
            chip.SetResourceReference(FrameworkElement.StyleProperty, "Chip");
            chip.Checked += (s, e) =>
            {
                language = value;
                Array.Clear(texts, 0, texts.Length);
                Array.Clear(problems, 0, problems.Length);
                ShowText();
                Recognize();
            };
            Get<WrapPanel>("LanguageChips").Children.Add(chip);
        }
    }

    // Shows another picture of the selection.
    void Pick(int at)
    {
        index = at;
        int mine = ++loading;
        string path = paths[at];
        if (Many) Get<TextBlock>("Current").Text = (at + 1) + " / " + paths.Length + "  ·  " + Path.GetFileName(path);
        ShowText();
        Images.Background(() =>
        {
            Picture picture = null;
            try { picture = Images.Load(path); }
            catch (Exception) { } // the reading of the text reports why
            Window.Dispatcher.BeginInvoke((Action)(() =>
            {
                if (mine != loading) return;
                var wait = Get<TextBlock>("Loading");
                wait.Text = "Görsel açılamadı";
                wait.Visibility = picture == null ? Visibility.Visible : Visibility.Collapsed;
                if (picture == null) Get<Grid>("Frame").Visibility = Visibility.Hidden;
                else Assets.Fit(Window, picture.Pixels);
            }));
        });
    }

    // Reads the pictures one after another, the shown one first. A newer call takes over.
    void Recognize()
    {
        int run = ++generation;
        var chosen = language;
        Images.Background(() =>
        {
            while (true)
            {
                int next = -1;
                Window.Dispatcher.Invoke((Action)(() =>
                {
                    if (run == generation && !closed) next = texts[index] == null ? index : Array.IndexOf(texts, null);
                }));
                if (next < 0) return;
                string text = "", problem = null;
                try { text = Ocr.Read(Images.Load(paths[next]), chosen); }
                catch (Exception e) { problem = e.Message; }
                Window.Dispatcher.Invoke((Action)(() =>
                {
                    if (run != generation) return;
                    texts[next] = text;
                    problems[next] = problem;
                    if (next == index) ShowText();
                    else Buttons();
                }));
            }
        });
    }

    void ShowText()
    {
        var box = Get<TextBox>("Recognized");
        showing = true;
        box.Text = texts[index] ?? "";
        showing = false;
        box.IsEnabled = texts[index] != null;
        var error = Get<TextBlock>("Error");
        error.Text = problems[index] ?? "";
        error.Visibility = problems[index] == null ? Visibility.Collapsed : Visibility.Visible;
        Buttons();
    }

    void Buttons()
    {
        string text = texts[index];
        int done = texts.Count(t => t != null);
        Get<TextBlock>("Details").Text = text == null ? "Metin okunuyor…"
            : problems[index] != null ? "Okunamadı"
            : string.IsNullOrWhiteSpace(text) ? "Bu görselde metin bulunamadı"
            : text.Split('\n').Length + " satır · " + text.Length + " karakter";
        if (Many && done < paths.Length) Get<TextBlock>("Details").Text += " · " + done + " / " + paths.Length + " okundu";
        Get<Button>("Save").IsEnabled = !string.IsNullOrWhiteSpace(text);
        Get<Button>("Save").Content = "Kopyala";
        var all = Get<Button>("Extra");
        all.IsEnabled = done == paths.Length && texts.Any(t => !string.IsNullOrWhiteSpace(t));
        all.Content = "Tümünü kopyala";
    }

    // The button that was used says so until something changes.
    void Copy(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var pressed = System.Windows.Input.Keyboard.FocusedElement as Button;
        try
        {
            Ocr.ToClipboard(text);
            Buttons();
            (pressed ?? Get<Button>("Save")).Content = "Kopyalandı ✓";
        }
        catch (Exception e)
        {
            var error = Get<TextBlock>("Error");
            error.Text = "Panoya kopyalanamadı: " + e.Message;
            error.Visibility = Visibility.Visible;
        }
    }

    public static void Open(string[] files)
    {
        Assets.Show(new Reader(files).Window);
    }
}

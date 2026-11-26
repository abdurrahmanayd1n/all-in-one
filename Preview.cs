// Alt+Space: a look inside the selected file in a window like File Explorer's. Nothing can be changed there.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml;
using Path = System.IO.Path;

enum ItemKind { Folder, Picture, Pdf, Text, Document, Archive, Video, Sound, Other }

class Preview
{
    const int MostText = 200 * 1024, MostPages = 40, MostEntries = 500;
    static readonly string[] Writing = { ".txt", ".md", ".log", ".csv", ".tsv", ".json", ".xml", ".xaml", ".html", ".htm",
        ".css", ".js", ".ts", ".cs", ".java", ".py", ".c", ".cpp", ".h", ".ps1", ".cmd", ".bat", ".sh", ".ini", ".cfg",
        ".yml", ".yaml", ".toml", ".sql", ".php", ".rs", ".go", ".kt", ".srt", ".reg", ".gitignore" };
    static readonly string[] Sounds = { ".mp3", ".wav", ".m4a", ".aac", ".wma", ".flac", ".ogg" };

    static Preview shown; // one at a time

    public readonly Window Window = Assets.Window("Preview.xaml");
    readonly string[] paths;
    int index;
    volatile int loading; // a newer number stops what is still being read
    bool closing;

    T Get<T>(string name) where T : class { return (T)Window.FindName(name); }

    public Preview(string[] files)
    {
        paths = files;
        Window.Icon = Assets.Icon();
        Window.SourceInitialized += (s, e) =>
        {
            IntPtr handle = new System.Windows.Interop.WindowInteropHelper(Window).Handle;
            int dark = 1, round = 2;
            Native.DwmSetWindowAttribute(handle, 20, ref dark, 4);
            Native.DwmSetWindowAttribute(handle, 33, ref round, 4); // rounded corners, Windows 11
        };
        Window.Width = Math.Min(Window.Width, SystemParameters.WorkArea.Width);
        Window.Height = Math.Min(Window.Height, SystemParameters.WorkArea.Height);
        Get<Button>("Close").Click += (s, e) => Shut();
        Get<Button>("Grow").Click += (s, e) => Window.WindowState = Window.WindowState == WindowState.Maximized
            ? WindowState.Normal : WindowState.Maximized;
        // A maximised window without a frame of its own would reach past the screen's edges.
        Window.StateChanged += (s, e) => Get<Grid>("Page").Margin
            = new Thickness(Window.WindowState == WindowState.Maximized ? 7 : 0);
        Get<Button>("Previous").Click += (s, e) => Move(-1);
        Get<Button>("Next").Click += (s, e) => Move(1);
        Window.PreviewKeyDown += (s, e) =>
        {
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key == Key.Escape || key == Key.Space) Shut();
            else if (key == Key.Left) Move(-1);
            else if (key == Key.Right) Move(1);
            else return;
            e.Handled = true;
        };
        Window.Closing += (s, e) => closing = true;
        Window.Closed += (s, e) =>
        {
            loading++;
            Get<Grid>("Body").Children.Clear(); // lets go of a playing file
            if (shown == this) shown = null;
        };
        Window.Loaded += (s, e) =>
        {
            Window.Activate();
            // Looking elsewhere puts it away, as a popup does.
            Window.Deactivated += (a, b) => Shut();
        };
        Get<TextBlock>("Hint").Text = Lang.T(paths.Length > 1 ? Lang.T("← → sonraki dosya  ·  Esc veya Boşluk: kapat") : Lang.T("Esc veya Boşluk: kapat"));
        Show(0);
    }

    void Shut()
    {
        if (!closing) Window.Close();
    }

    void Move(int by)
    {
        int next = index + by;
        if (next >= 0 && next < paths.Length) Show(next);
    }

    public static ItemKind Of(string path)
    {
        if (Directory.Exists(path)) return ItemKind.Folder;
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (Images.IsImage(path)) return ItemKind.Picture;
        if (ext == ".pdf") return ItemKind.Pdf;
        if (ext == ".docx") return ItemKind.Document;
        if (ext == ".zip") return ItemKind.Archive;
        if (Video.IsVideo(path)) return ItemKind.Video;
        if (Sounds.Contains(ext)) return ItemKind.Sound;
        if (Writing.Contains(ext) || LooksWritten(path)) return ItemKind.Text;
        return ItemKind.Other;
    }

    // A file of unknown kind is shown as text when its beginning has no zero bytes.
    static bool LooksWritten(string path)
    {
        try
        {
            var head = new byte[4096];
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                int count = file.Read(head, 0, head.Length);
                return count > 0 && Array.IndexOf(head, (byte)0, 0, count) < 0;
            }
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    // The beginning of a text file. UTF-8 and UTF-16 are recognised; anything else is the user's old code page.
    public static string ReadText(string path)
    {
        var data = new byte[MostText + 1];
        int count = 0;
        using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            for (int read; count < data.Length && (read = file.Read(data, count, data.Length - count)) > 0; ) count += read;
        bool cut = count > MostText;
        count = Math.Min(count, MostText);
        string text;
        if (count >= 2 && (data[0] == 0xFF && data[1] == 0xFE || data[0] == 0xFE && data[1] == 0xFF))
            text = (data[0] == 0xFF ? Encoding.Unicode : Encoding.BigEndianUnicode).GetString(data, 2, count - 2);
        else
        {
            int skip = count >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF ? 3 : 0;
            try
            {
                // A cut may fall inside a letter: the unfinished bytes at the end are left out.
                var strict = new UTF8Encoding(false, true).GetDecoder();
                var letters = new char[count];
                text = new string(letters, 0, strict.GetChars(data, skip, count - skip, letters, 0, !cut));
            }
            catch (DecoderFallbackException) { text = Encoding.Default.GetString(data, 0, count); }
        }
        return cut ? text + "\n\n" + Lang.T("… (yalnız başı gösteriliyor)") : text;
    }

    // The words of a Word document, a paragraph a line.
    public static string ReadDocument(string path)
    {
        var text = new StringBuilder();
        using (var archive = new ZipArchive(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)))
        {
            var entry = archive.GetEntry("word/document.xml");
            if (entry == null) throw new IOException(Lang.T("Belgenin içi okunamadı."));
            using (var stream = entry.Open())
            using (var xml = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit }))
            {
                bool more = xml.Read();
                while (more && text.Length < MostText)
                {
                    if (xml.NodeType == XmlNodeType.Element && xml.LocalName == "t")
                    {
                        text.Append(xml.ReadElementContentAsString()); // leaves the reader on the next node
                        continue;
                    }
                    if (xml.NodeType == XmlNodeType.Element && xml.LocalName == "tab") text.Append('\t');
                    if (xml.LocalName == "p" && (xml.NodeType == XmlNodeType.EndElement || xml.IsEmptyElement)) text.Append('\n');
                    more = xml.Read();
                }
            }
        }
        return text.ToString();
    }

    public static string ReadArchive(string path)
    {
        using (var archive = new ZipArchive(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)))
        {
            var lines = archive.Entries.Take(MostEntries)
                .Select(e => e.FullName + (e.FullName.EndsWith("/") ? "" : "    " + Images.Size(e.Length)));
            return string.Join("\n", lines) + (archive.Entries.Count > MostEntries
                ? "\n… (" + archive.Entries.Count + " " + Lang.T("öğe") + ")" : "");
        }
    }

    void Show(int at)
    {
        index = at;
        int mine = ++loading;
        string path = paths[at];
        string name = Path.GetFileName(path) == "" ? path : Path.GetFileName(path);
        Get<TextBlock>("TabName").Text = name;
        Get<TextBlock>("ItemName").Text = name;
        Get<TextBlock>("Address").Text = Lang.T("Bu bilgisayar") + "  ›  "
            + string.Join("  ›  ", path.Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries));
        Get<Button>("Previous").IsEnabled = at > 0;
        Get<Button>("Next").IsEnabled = at < paths.Length - 1;
        Get<Image>("TabIcon").Source = Get<Image>("BigIcon").Source = null;
        Get<StackPanel>("Facts").Children.Clear();
        Get<TextBlock>("Count").Text = paths.Length > 1 ? Lang.F("{0} / {1} seçili öğe", at + 1, paths.Length)
            : Lang.T("1 öğe seçili");
        ShowPlaces(path);
        Wait(Lang.T("Yükleniyor…"));

        var kind = Of(path);
        string ext = Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
        string kindName = kind == ItemKind.Folder ? Lang.T("Klasör") : Lang.T(kind == ItemKind.Picture ? Lang.T("Görsel") : kind == ItemKind.Video
            ? Lang.T("Video") : kind == ItemKind.Sound ? Lang.T("Ses") : kind == ItemKind.Text ? Lang.T("Metin") : kind == ItemKind.Document ? Lang.T("Belge")
            : kind == ItemKind.Archive ? Lang.T("Arşiv") : kind == ItemKind.Pdf ? Lang.T("PDF belgesi") : Lang.T("Dosya")) + (ext == "" ? "" : " · " + ext);
        Get<TextBlock>("Kind").Text = kindName;
        Describe(path, kind);

        Images.Background(() =>
        {
            var icon = ShellImage.Get(path, 20);
            var big = ShellImage.Get(path, 96);
            Later(mine, () =>
            {
                Get<Image>("TabIcon").Source = icon;
                Get<Image>("BigIcon").Source = big;
            });
        });
        Images.Background(() =>
        {
            try { Load(mine, path, kind); }
            catch (Exception e)
            {
                Log.Error("önizleme: " + path, e);
                Later(mine, () => Large(mine, path, e.Message));
            }
        });
    }

    // Runs on the window's thread, unless another item has been chosen or the window is gone.
    void Later(int mine, Action work)
    {
        Window.Dispatcher.BeginInvoke((Action)(() => { if (mine == loading) work(); }));
    }

    void Put(UIElement content)
    {
        var body = Get<Grid>("Body");
        body.Children.Clear();
        body.Children.Add(content);
    }

    void Wait(string text)
    {
        Put(new TextBlock { Text = text, Foreground = Brushes.Gray, HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center });
    }

    void Load(int mine, string path, ItemKind kind)
    {
        if (kind == ItemKind.Picture)
        {
            var pixels = Images.Load(path).Pixels;
            Later(mine, () =>
            {
                Put(new Image { Source = pixels, Margin = new Thickness(24), StretchDirection = StretchDirection.DownOnly });
                Fact(Lang.T("Boyutlar"), pixels.PixelWidth + " × " + pixels.PixelHeight);
            });
        }
        else if (kind == ItemKind.Text || kind == ItemKind.Document || kind == ItemKind.Archive)
        {
            string text = kind == ItemKind.Text ? ReadText(path) : kind == ItemKind.Document ? ReadDocument(path) : ReadArchive(path);
            Later(mine, () => Put(new TextBox
            {
                Text = text, IsReadOnly = true, ContextMenu = null, BorderThickness = new Thickness(0),
                Background = Brushes.Transparent, Foreground = new SolidColorBrush(Color.FromRgb(0xE4, 0xE4, 0xE4)),
                FontFamily = new FontFamily(kind == ItemKind.Document ? "Segoe UI" : "Cascadia Mono, Consolas"), FontSize = 13.5,
                TextWrapping = kind == ItemKind.Document ? TextWrapping.Wrap : TextWrapping.NoWrap, Padding = new Thickness(18),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto
            }));
        }
        else if (kind == ItemKind.Pdf)
        {
            var document = Pdf.Open(path);
            int count = (int)document.PageCount;
            StackPanel sheets = null;
            Later(mine, () =>
            {
                sheets = new StackPanel { Margin = new Thickness(24, 16, 24, 16) };
                Put(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = sheets, Focusable = true });
                Fact(Lang.T("Sayfa"), count.ToString());
            });
            for (int i = 0; i < Math.Min(count, MostPages) && mine == loading; i++)
            {
                var page = Pdf.Render(document, i, 1100);
                // Waits for the window, so that pages do not pile up faster than they are shown.
                Window.Dispatcher.Invoke((Action)(() =>
                {
                    if (mine == loading) sheets.Children.Add(new Image { Source = page, MaxWidth = 900,
                        Margin = new Thickness(0, 0, 0, 16) });
                }));
            }
            if (count > MostPages)
                Later(mine, () => sheets.Children.Add(new TextBlock { Foreground = Brushes.Gray,
                    HorizontalAlignment = HorizontalAlignment.Center, Text = Lang.F("İlk {0} sayfa gösteriliyor.", MostPages) }));
        }
        else if (kind == ItemKind.Video || kind == ItemKind.Sound) Later(mine, () => Play(mine, path, kind == ItemKind.Video));
        else Later(mine, () => Large(mine, path, null));
    }

    // A video plays without sound until it is clicked; a sound starts on a click.
    void Play(int mine, string path, bool picture)
    {
        var player = new MediaElement { LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Close,
            Stretch = Stretch.Uniform, IsMuted = picture, ScrubbingEnabled = true };
        var button = new Button { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 18), Padding = new Thickness(16, 7, 16, 7), Focusable = false,
            Content = Lang.T(picture ? Lang.T("Sesi aç") : Lang.T("▶  Çal")) };
        bool playing = picture;
        button.Click += (s, e) =>
        {
            if (picture)
            {
                player.IsMuted = !player.IsMuted;
                button.Content = Lang.T(player.IsMuted ? Lang.T("Sesi aç") : Lang.T("Sesi kapat"));
            }
            else
            {
                playing = !playing;
                if (playing) player.Play();
                else player.Pause();
                button.Content = Lang.T(playing ? Lang.T("❚❚  Duraklat") : Lang.T("▶  Çal"));
            }
        };
        player.MediaEnded += (s, e) =>
        {
            player.Position = TimeSpan.Zero;
            if (picture) player.Play();
        };
        // A kind Windows cannot play still gets its thumbnail.
        player.MediaFailed += (s, e) => { if (mine == loading) Large(mine, path, Lang.T("Windows bu dosyayı oynatamıyor.")); };
        var holder = new Grid { Margin = new Thickness(16) };
        if (!picture)
            holder.Children.Add(new TextBlock { Text = "\uE8D6", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 96,
                Foreground = Brushes.Gray, HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center });
        holder.Children.Add(player);
        holder.Children.Add(button);
        Put(holder);
        player.Source = new Uri(path);
        if (picture) player.Play();
        else player.Stop(); // opens the file without a sound
    }

    // Folders and files with nothing to show inside: their picture as Explorer draws it.
    void Large(int mine, string path, string note)
    {
        var image = new Image { Width = 256, Height = 256 };
        var column = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        column.Children.Add(image);
        column.Children.Add(new TextBlock { Text = note ?? Path.GetFileName(path), TextWrapping = TextWrapping.Wrap, MaxWidth = 460,
            TextAlignment = TextAlignment.Center, FontSize = 15, Margin = new Thickness(0, 16, 0, 0) });
        Put(column);
        Images.Background(() =>
        {
            var big = ShellImage.Get(path, 256);
            Later(mine, () => image.Source = big);
        });
    }

    void Fact(string label, string value)
    {
        var facts = Get<StackPanel>("Facts");
        facts.Children.Add(new TextBlock { Text = Lang.T(label), Foreground = Brushes.Gray, FontSize = 12 });
        facts.Children.Add(new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 10) });
    }

    void Describe(string path, ItemKind kind)
    {
        try
        {
            if (kind == ItemKind.Folder)
            {
                var folder = new DirectoryInfo(path);
                Fact(Lang.T("Değiştirilme"), folder.LastWriteTime.ToString("g"));
                Fact(Lang.T("İçindekiler"), Lang.F("{0} klasör, {1} dosya", folder.EnumerateDirectories().Count(),
                    folder.EnumerateFiles().Count()));
                return;
            }
            var file = new FileInfo(path);
            Get<TextBlock>("Count").Text += "   ·   " + Images.Size(file.Length);
            Fact(Lang.T("Boyut"), Images.Size(file.Length));
            Fact(Lang.T("Değiştirilme"), file.LastWriteTime.ToString("g"));
            Fact(Lang.T("Oluşturulma"), file.CreationTime.ToString("g"));
        }
        catch (IOException e) { Fact(Lang.T("Bilgi"), e.Message); }
        catch (UnauthorizedAccessException e) { Fact(Lang.T("Bilgi"), e.Message); }
    }

    // The usual places, as in Explorer's side bar; the one that holds the item is marked. They are not links.
    void ShowPlaces(string path)
    {
        var places = Get<StackPanel>("Places");
        places.Children.Clear();
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var all = new List<Tuple<string, string>>
        {
            Tuple.Create(Lang.T("Masaüstü"), Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)),
            Tuple.Create(Lang.T("İndirilenler"), Path.Combine(home, "Downloads")),
            Tuple.Create(Lang.T("Belgeler"), Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)),
            Tuple.Create(Lang.T("Resimler"), Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)),
            Tuple.Create(Lang.T("Müzik"), Environment.GetFolderPath(Environment.SpecialFolder.MyMusic)),
            Tuple.Create(Lang.T("Videolar"), Environment.GetFolderPath(Environment.SpecialFolder.MyVideos))
        };
        all.AddRange(DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed || d.DriveType == DriveType.Removable)
            .Select(d => Tuple.Create(d.Name.TrimEnd('\\'), d.Name)));
        string holder = all.Select(p => p.Item2).Where(p => p != "" && path.StartsWith(p.TrimEnd('\\') + "\\",
            StringComparison.OrdinalIgnoreCase)).OrderByDescending(p => p.Length).FirstOrDefault();
        int mine = loading;
        var images = new List<Tuple<Image, string>>();
        foreach (var place in all.Where(p => p.Item2 != ""))
        {
            var icon = new Image { Width = 18, Height = 18, Margin = new Thickness(0, 0, 10, 0) };
            images.Add(Tuple.Create(icon, place.Item2));
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(icon);
            row.Children.Add(new TextBlock { Text = Lang.T(place.Item1), VerticalAlignment = VerticalAlignment.Center });
            places.Children.Add(new Border { Child = row, CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 7, 10, 7),
                Background = place.Item2 == holder ? new SolidColorBrush(Color.FromRgb(0x38, 0x38, 0x38)) : Brushes.Transparent });
        }
        Images.Background(() =>
        {
            foreach (var each in images)
            {
                if (mine != loading) return;
                var picture = ShellImage.Get(each.Item2, 20);
                var target = each.Item1;
                Later(mine, () => target.Source = picture);
            }
        });
    }

    public static void Open(string[] files)
    {
        if (shown != null) shown.Shut();
        shown = new Preview(files);
        Assets.Show(shown.Window);
    }
}

// The pictures Explorer shows for files and folders: thumbnails where there are any, icons otherwise.
static class ShellImage
{
    [StructLayout(LayoutKind.Sequential)] struct Size { public int Width, Height; }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(Size size, int flags, out IntPtr bitmap);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    static extern void SHCreateItemFromParsingName(string path, IntPtr context, [In] ref Guid id,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory item);

    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr item);

    // Needs a single-threaded apartment. null when Windows has none.
    public static BitmapSource Get(string path, int pixels)
    {
        try
        {
            var id = typeof(IShellItemImageFactory).GUID;
            IShellItemImageFactory item;
            SHCreateItemFromParsingName(path, IntPtr.Zero, ref id, out item);
            IntPtr bitmap;
            // Twice the shown size for sharpness; above 256 Windows only pads the picture it has.
            int side = Math.Min(256, pixels * 2);
            if (item.GetImage(new Size { Width = side, Height = side }, 0, out bitmap) != 0) return null;
            try
            {
                var source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                return source;
            }
            finally { DeleteObject(bitmap); }
        }
        catch (FileNotFoundException) { return null; } // gone meanwhile
        catch (Exception e)
        {
            Log.Error("simge alınamadı: " + path, e);
            return null;
        }
    }
}

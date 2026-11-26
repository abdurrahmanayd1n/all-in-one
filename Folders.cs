// Folder tools: what takes the space, one ZIP of a selection, and the files inside for renaming.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Path = System.IO.Path;

// A scanned folder: its total and the folders below it.
class Node
{
    public string Path;
    public long Bytes, OwnBytes; // everything below, and the files directly inside
    public int Files, Folders, Unread; // Unread: folders Windows would not list
    public List<Node> Children = new List<Node>();
}

static class Folders
{
    // Links (junctions, symbolic links) are not followed: they would count things twice or go round in circles.
    static bool IsLink(FileSystemInfo item) { return (item.Attributes & FileAttributes.ReparsePoint) != 0; }

    // seen runs for every folder, so a long scan can show that it is alive.
    public static Node Scan(string folder, Action seen, Func<bool> cancelled)
    {
        var node = new Node { Path = folder };
        if (cancelled()) throw new OperationCanceledException();
        seen();
        try
        {
            foreach (var item in new DirectoryInfo(folder).EnumerateFileSystemInfos())
            {
                var file = item as FileInfo;
                if (file != null)
                {
                    node.OwnBytes += file.Length;
                    node.Files++;
                }
                else if (!IsLink(item))
                {
                    var child = Scan(item.FullName, seen, cancelled);
                    node.Children.Add(child);
                    node.Bytes += child.Bytes;
                    node.Files += child.Files;
                    node.Folders += child.Folders + 1;
                    node.Unread += child.Unread;
                }
            }
        }
        catch (UnauthorizedAccessException) { node.Unread++; }
        catch (PathTooLongException) { node.Unread++; }
        catch (IOException) { node.Unread++; }
        node.Bytes += node.OwnBytes;
        return node;
    }

    // The files directly inside the folders, without hidden and system ones such as desktop.ini.
    public static string[] Contents(string[] folders)
    {
        return folders.SelectMany(folder => new DirectoryInfo(folder).GetFiles()
            .Where(f => (f.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0)
            .Select(f => f.FullName)).ToArray();
    }

    public static void RenameContents(string[] folders)
    {
        var files = Contents(folders);
        if (files.Length == 0) Images.Notify(new Outcome { Text = Lang.T("Klasörün içinde adlandırılacak dosya yok.") });
        else RenameWindow.Open(files);
    }

    // A drive has no folder to put an archive in.
    public static bool Packable(string[] items)
    {
        return items.Length > 0 && items.All(i => Path.GetDirectoryName(i) != null);
    }

    public static string ArchiveName(string[] items)
    {
        if (items.Length == 1)
            return Directory.Exists(items[0]) ? Path.GetFileName(items[0]) : Path.GetFileNameWithoutExtension(items[0]);
        return "arsiv";
    }

    class Entry
    {
        public string Source, Name; // Source null: an empty folder
        public long Bytes;
    }

    // Everything that goes in, with its name inside the archive.
    static void Collect(string path, string name, List<Entry> entries, List<string> problems)
    {
        if (File.Exists(path))
        {
            entries.Add(new Entry { Source = path, Name = name, Bytes = new FileInfo(path).Length });
            return;
        }
        try
        {
            var inside = new DirectoryInfo(path).GetFileSystemInfos();
            if (inside.Length == 0) entries.Add(new Entry { Name = name + "/" });
            foreach (var item in inside)
            {
                if (item is DirectoryInfo && IsLink(item)) problems.Add(item.FullName + Lang.T(": bağlantı klasörü, atlandı"));
                else Collect(item.FullName, name + "/" + item.Name, entries, problems);
            }
        }
        catch (Exception e) { problems.Add(path + ": " + e.Message); }
    }

    // Packs the selected files and folders into a new archive next to the first one. Nothing existing is overwritten,
    // an unfinished archive is removed, and what could not be added is listed.
    public static Outcome Zip(string[] items, string name, CompressionLevel level, Action<double> progress, Func<bool> cancelled,
        List<string> problems)
    {
        if (!Packable(items)) throw new NotSupportedException(Lang.T("Bir sürücünün tamamı ZIP yapılamaz; içindeki klasörleri seç."));
        var entries = new List<Entry>();
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            // Two selected items with the same name (from different folders) get numbers.
            string own = Path.GetFileName(item), unique = own;
            for (int i = 2; !taken.Add(unique); i++)
                unique = Path.GetFileNameWithoutExtension(own) + " (" + i + ")" + Path.GetExtension(own);
            Collect(item, unique, entries, problems);
        }
        long total = Math.Max(1, entries.Sum(e => e.Bytes)), done = 0;
        int added = 0;
        string target;
        var stream = Images.CreateFree(Path.GetDirectoryName(items[0]), name, ".zip", out target);
        bool complete = false;
        try
        {
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, false, System.Text.Encoding.UTF8))
            {
                var buffer = new byte[1 << 20];
                foreach (var entry in entries)
                {
                    if (cancelled()) throw new OperationCanceledException();
                    if (entry.Source == null) { zip.CreateEntry(entry.Name); continue; }
                    // The new archive lies inside one of the selected folders.
                    if (string.Equals(entry.Source, target, StringComparison.OrdinalIgnoreCase)) continue;
                    FileStream source;
                    try { source = new FileStream(entry.Source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); }
                    catch (Exception e) { problems.Add(entry.Source + ": " + e.Message); continue; }
                    using (source)
                    {
                        var made = zip.CreateEntry(entry.Name, level);
                        var written = File.GetLastWriteTime(entry.Source);
                        if (written.Year >= 1980 && written.Year <= 2107) made.LastWriteTime = written;
                        // A failure from here on would leave half a file in the archive: it ends the whole job.
                        using (var into = made.Open())
                        {
                            int read;
                            while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                            {
                                if (cancelled()) throw new OperationCanceledException();
                                into.Write(buffer, 0, read);
                                done += read;
                                progress(Math.Min(1, (double)done / total));
                            }
                        }
                        added++;
                    }
                }
            }
            complete = true;
        }
        finally
        {
            stream.Dispose();
            if (!complete) File.Delete(target);
        }
        long size = new FileInfo(target).Length;
        string text = Path.GetFileName(target) + "\n" + added + Lang.T(" dosya · ") + Images.Size(size);
        if (problems.Count > 0)
        {
            text += " · " + problems.Count + Lang.T(" öğe eklenemedi");
            foreach (var problem in problems.Take(50)) Log.Error("ZIP'e eklenemedi: " + problem);
        }
        return new Outcome { File = target, Bytes = size, Text = text };
    }
}

class ZipWindow
{
    public readonly Window Window = Assets.Window("Editor.xaml");
    readonly string[] items;
    CompressionLevel level = CompressionLevel.Optimal;
    bool running, syncing;
    volatile bool cancel;

    T Get<T>(string name) where T : class { return (T)Window.FindName(name); }

    // start: begin at once (the one-click option of the menu).
    public ZipWindow(string[] chosen, bool start)
    {
        items = chosen;
        Assets.Dress(Window);
        Window.Title = "ZIP Yap - " + (items.Length > 1 ? items.Length + Lang.T(" öğe") : Path.GetFileName(items[0]));
        Get<TextBlock>("Heading").Text = "ZIP Yap";
        Get<TextBlock>("Details").Text = items.Length + Lang.T(" öğe");
        Get<TextBlock>("Loading").Visibility = Visibility.Collapsed;
        Get<Grid>("Frame").Visibility = Visibility.Collapsed;
        Get<ScrollViewer>("RenameList").Visibility = Visibility.Visible;
        var rows = Get<StackPanel>("Rows");
        foreach (var item in items.Take(500))
            rows.Children.Add(new TextBlock { Text = (Directory.Exists(item) ? Lang.T("Klasör   ") : Lang.T("Dosya    ")) + Path.GetFileName(item),
                Margin = new Thickness(0, 0, 0, 6), TextTrimming = TextTrimming.CharacterEllipsis });

        Get<StackPanel>("ZipLevelPanel").Visibility = Visibility.Visible;
        foreach (var choice in new[] { Tuple.Create(Lang.T("Normal"), CompressionLevel.Optimal), Tuple.Create(Lang.T("Hızlı"), CompressionLevel.Fastest),
            Tuple.Create(Lang.T("Sıkıştırmasız"), CompressionLevel.NoCompression) })
        {
            var value = choice.Item2;
            var chip = new RadioButton { Content = choice.Item1, GroupName = "level", IsChecked = value == level };
            chip.SetResourceReference(FrameworkElement.StyleProperty, "Chip");
            chip.Checked += (s, e) => level = value;
            Get<WrapPanel>("ZipLevelChips").Children.Add(chip);
        }

        var name = Get<TextBox>("NameBox");
        var use = Get<CheckBox>("UseSuffix");
        var suffix = Get<TextBox>("SuffixBox");
        use.IsChecked = Naming.UseSuffix;
        suffix.Text = Naming.Suffix;
        suffix.IsEnabled = use.IsChecked == true;
        Get<TextBlock>("NameLabel").Text = Lang.T("ZIP adı");
        Get<TextBlock>("Extension").Text = ".zip";
        Action suggest = () =>
        {
            syncing = true;
            name.Text = Folders.ArchiveName(items) + (use.IsChecked == true ? suffix.Text : "");
            syncing = false;
            Validate();
        };
        Action changed = () =>
        {
            suffix.IsEnabled = use.IsChecked == true;
            if (Naming.Problem("x" + suffix.Text + "x") == null) Naming.Remember(use.IsChecked == true, suffix.Text);
            suggest();
        };
        use.Click += (s, e) => changed();
        suffix.TextChanged += (s, e) => changed();
        name.TextChanged += (s, e) => { if (!syncing) Validate(); };

        var save = Get<Button>("Save");
        save.Content = "ZIP Yap";
        save.Click += (s, e) => Start();
        var close = Get<Button>("Cancel");
        close.Content = Lang.T("Kapat");
        close.Click += (s, e) =>
        {
            if (running) cancel = true; // stops the work, the window stays
            else Window.Close();
        };
        Window.Closed += (s, e) => cancel = true;
        if (start) Window.Loaded += (s, e) => Start();
        suggest();
    }

    string Problem()
    {
        return !Folders.Packable(items) ? Lang.T("Bir sürücünün tamamı ZIP yapılamaz; içindeki klasörleri seç.")
            : Naming.Problem(Get<TextBox>("NameBox").Text);
    }

    void Say(string name, string text)
    {
        var line = Get<TextBlock>(name);
        line.Text = text ?? "";
        line.Visibility = text == null ? Visibility.Collapsed : Visibility.Visible;
    }

    void Validate()
    {
        if (running) return;
        Say("Error", Problem());
        Get<Button>("Save").IsEnabled = Problem() == null;
    }

    void Start()
    {
        if (running || Problem() != null) return;
        running = true;
        cancel = false;
        Get<Button>("Save").IsEnabled = false;
        Get<Button>("Cancel").Content = Lang.T("İptal");
        Get<ScrollViewer>("Settings").IsEnabled = false;
        Say("Error", null);
        Say("Status", Lang.T("Hazırlanıyor…"));
        var bar = Get<ProgressBar>("Progress");
        bar.Value = 0;
        bar.Visibility = Visibility.Visible;
        string name = Get<TextBox>("NameBox").Text;
        var chosen = level;
        Images.Background(() =>
        {
            Outcome outcome = null;
            string failure = null;
            var problems = new List<string>();
            int shown = -1;
            try
            {
                outcome = Folders.Zip(items, name, chosen, p =>
                {
                    int percent = (int)(p * 100);
                    if (percent == shown) return;
                    shown = percent;
                    Window.Dispatcher.BeginInvoke((Action)(() =>
                    {
                        if (!running) return;
                        bar.Value = p;
                        Say("Status", Lang.Percent(percent));
                    }));
                }, () => cancel, problems);
            }
            catch (OperationCanceledException) { }
            catch (Exception e)
            {
                Log.Error("ZIP: " + items[0], e);
                failure = Lang.T("ZIP yapılamadı: ") + e.Message;
            }
            Window.Dispatcher.BeginInvoke((Action)(() =>
            {
                running = false;
                Get<ScrollViewer>("Settings").IsEnabled = true;
                Get<Button>("Cancel").Content = Lang.T("Kapat");
                bar.Visibility = Visibility.Collapsed;
                Say("Status", null);
                if (outcome != null)
                {
                    Images.Notify(outcome);
                    // Stay open when something is missing from the archive, so it can be read.
                    if (problems.Count == 0) { Window.Close(); return; }
                }
                Validate();
                if (outcome != null)
                    Say("Error", Lang.T("Arşiv eksik: ") + problems.Count + Lang.T(" öğe eklenemedi.\n") + string.Join("\n", problems.Take(4)));
                else if (failure != null) Say("Error", failure);
                else Say("Status", Lang.T("İptal edildi; yarım arşiv silindi."));
            }));
        });
    }

    public static void Open(string[] items, bool start)
    {
        Assets.Show(new ZipWindow(items, start).Window);
    }
}

class SizeWindow
{
    const int Shown = 400; // rows
    public readonly Window Window = Assets.Window("Editor.xaml");
    readonly string[] folders;
    readonly Stack<Node> path = new Stack<Node>(); // the way down to what is shown
    List<Node> roots = new List<Node>();
    volatile bool cancel;
    bool scanning;

    T Get<T>(string name) where T : class { return (T)Window.FindName(name); }

    public SizeWindow(string[] chosen)
    {
        folders = chosen;
        Assets.Dress(Window);
        Window.Title = Lang.T("Klasör Boyutu - ") + (folders.Length > 1 ? folders.Length + Lang.T(" klasör") : folders[0]);
        Get<TextBlock>("Heading").Text = Lang.T("Klasör Boyutu");
        Get<ScrollViewer>("Settings").Visibility = Visibility.Collapsed;
        Get<TextBlock>("Loading").Visibility = Visibility.Collapsed;
        Get<Grid>("Frame").Visibility = Visibility.Collapsed;
        Get<ScrollViewer>("RenameList").Visibility = Visibility.Visible;
        Get<TextBlock>("Details").TextWrapping = TextWrapping.Wrap;

        var reveal = Get<Button>("Save");
        reveal.Content = Lang.T("Klasörü aç");
        reveal.Click += (s, e) => OpenFolder(path.Count > 0 ? path.Peek().Path : folders[0]);
        var up = Get<Button>("Extra");
        up.Content = Lang.T("Yukarı");
        up.Visibility = Visibility.Visible;
        up.Click += (s, e) =>
        {
            path.Pop();
            Show();
        };
        Get<Button>("Cancel").Content = Lang.T("Kapat");
        Get<Button>("Cancel").Click += (s, e) => Window.Close();
        Window.Closed += (s, e) => cancel = true;
        Window.Loaded += (s, e) => Scan();
    }

    void Scan()
    {
        scanning = true;
        Get<TextBlock>("Details").Text = Lang.T("Taranıyor…");
        Images.Background(() =>
        {
            int count = 0;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var found = new List<Node>();
            try
            {
                foreach (var folder in folders)
                    found.Add(Folders.Scan(folder, () =>
                    {
                        count++;
                        if (watch.ElapsedMilliseconds < 250) return;
                        watch.Restart();
                        int so = count;
                        Window.Dispatcher.BeginInvoke((Action)(() =>
                        {
                            if (scanning) Get<TextBlock>("Details").Text = Lang.T("Taranıyor… ") + so + Lang.T(" klasör");
                        }));
                    }, () => cancel));
            }
            catch (OperationCanceledException) { return; }
            Window.Dispatcher.BeginInvoke((Action)(() =>
            {
                scanning = false;
                roots = found;
                if (roots.Count == 1) path.Push(roots[0]);
                Get<Button>("Save").IsEnabled = true;
                Show();
            }));
        });
    }

    // The folders and files of the current level, largest first.
    void Show()
    {
        var here = path.Count > 0 ? path.Peek() : null;
        var lines = new List<Tuple<string, long, Node>>();
        var folderList = here == null ? roots : here.Children;
        foreach (var child in folderList)
            lines.Add(Tuple.Create(here == null ? child.Path : Path.GetFileName(child.Path), child.Bytes, child));
        if (here != null)
        {
            try
            {
                foreach (var file in new DirectoryInfo(here.Path).GetFiles())
                    lines.Add(Tuple.Create(file.Name, file.Length, (Node)null));
            }
            catch (Exception e) { Log.Error("klasör listelenemedi: " + here.Path, e); }
        }
        lines = lines.OrderByDescending(l => l.Item2).ToList();
        long total = here != null ? here.Bytes : roots.Sum(r => r.Bytes);
        long largest = Math.Max(1, lines.Count > 0 ? lines[0].Item2 : 1);

        var rows = Get<StackPanel>("Rows");
        rows.Children.Clear();
        foreach (var line in lines.Take(Shown)) rows.Children.Add(Row(line.Item1, line.Item2, total, largest, line.Item3));
        if (lines.Count > Shown)
            rows.Children.Add(new TextBlock { Text = Lang.T("… ve ") + (lines.Count - Shown) + Lang.T(" küçük öğe daha"), Foreground = Brushes.Gray });
        if (lines.Count == 0) rows.Children.Add(new TextBlock { Text = Lang.T("Klasör boş."), Foreground = Brushes.Gray });
        ((ScrollViewer)Get<ScrollViewer>("RenameList")).ScrollToTop();

        int files = here != null ? here.Files : roots.Sum(r => r.Files);
        int inside = here != null ? here.Folders : roots.Sum(r => r.Folders);
        int unread = here != null ? here.Unread : roots.Sum(r => r.Unread);
        Get<TextBlock>("Details").Text = (here != null ? here.Path : roots.Count + Lang.T(" klasör")) + "\n\nToplam " + Images.Size(total)
            + "\n" + files + Lang.T(" dosya · ") + inside + Lang.T(" alt klasör")
            + (unread > 0 ? "\n" + unread + Lang.T(" klasör okunamadı (izin yok); toplama girmedi.") : "")
            + Lang.T("\n\nİçine girmek için soldaki bir klasöre tıkla.");
        Get<Button>("Extra").IsEnabled = path.Count > (roots.Count == 1 ? 1 : 0);
    }

    // Name, size and a bar showing the share of the largest entry; folders can be entered.
    UIElement Row(string name, long bytes, long total, long largest, Node folder)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition());
        Action<string, int, string> cell = (text, column, color) =>
        {
            var block = new TextBlock { Text = text, TextTrimming = TextTrimming.CharacterEllipsis,
                TextAlignment = column == 0 ? TextAlignment.Left : TextAlignment.Right,
                Foreground = (Brush)new BrushConverter().ConvertFromString(color) };
            Grid.SetColumn(block, column);
            grid.Children.Add(block);
        };
        cell((folder != null ? "▸  " : "     ") + name, 0, folder != null ? "#F2F3F5" : "#C4C6CE");
        cell(bytes == 0 ? "0 KB" : Images.Size(bytes), 1, "#F2F3F5");
        cell(Lang.Percent(total > 0 ? (int)Math.Round(bytes * 100.0 / total) : 0), 2, "#A6A9B4");
        var bar = new Grid { Height = 3, Margin = new Thickness(0, 4, 0, 0) };
        double share = Math.Max(0.002, (double)bytes / largest);
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(share, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0, 1 - share), GridUnitType.Star) });
        bar.Children.Add(new Border { CornerRadius = new CornerRadius(1.5),
            Background = (Brush)new BrushConverter().ConvertFromString(folder != null ? "#0A84FF" : "#5A5D6A") });
        Grid.SetRow(bar, 1);
        Grid.SetColumnSpan(bar, 3);
        grid.Children.Add(bar);
        if (folder == null)
        {
            grid.Margin = new Thickness(10, 5, 10, 5);
            return grid;
        }
        var button = new Button { Content = grid, Background = Brushes.Transparent, Padding = new Thickness(8.5, 3.5, 8.5, 3.5),
            HorizontalContentAlignment = HorizontalAlignment.Stretch, Template = RowTemplate() };
        System.Windows.Automation.AutomationProperties.SetName(button, name);
        button.Click += (s, e) =>
        {
            path.Push(folder);
            Show();
        };
        return button;
    }

    // A row that spans the list and lights up under the pointer.
    static ControlTemplate RowTemplate()
    {
        return (ControlTemplate)System.Windows.Markup.XamlReader.Parse(@"<ControlTemplate TargetType='Button'
            xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>
            <Border x:Name='face' Background='Transparent' CornerRadius='7' BorderThickness='1.5' BorderBrush='Transparent'
                    Padding='{TemplateBinding Padding}'><ContentPresenter/></Border>
            <ControlTemplate.Triggers>
              <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='face' Property='Background' Value='#1AFFFFFF'/></Trigger>
              <Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='face' Property='BorderBrush' Value='White'/></Trigger>
            </ControlTemplate.Triggers>
          </ControlTemplate>");
    }

    static void OpenFolder(string folder)
    {
        System.Diagnostics.Process.Start(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"),
            "\"" + folder + "\"");
    }

    public static void Open(string[] folders)
    {
        Assets.Show(new SizeWindow(folders).Window);
    }
}

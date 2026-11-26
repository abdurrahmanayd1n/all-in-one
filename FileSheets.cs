// Three windows over files that share one shape: a list on the left, what can be done on the right.
// Finding copies, sorting a folder into subfolders, and the checksum of a file.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Path = System.IO.Path;

// The window of Editor.xaml with its list showing and an own panel on the right.
class Sheet
{
    public readonly Window Window = Assets.Window("Editor.xaml");
    public readonly StackPanel Rows;
    public readonly StackPanel Side = new StackPanel { Margin = new Thickness(0, 0, 12, 12) };
    public volatile bool Closed;

    public T Get<T>(string name) where T : class { return (T)Window.FindName(name); }

    public Sheet(string title, string heading)
    {
        Assets.Dress(Window);
        Window.Title = title;
        Get<TextBlock>("Heading").Text = heading;
        Get<ScrollViewer>("Settings").Visibility = Visibility.Collapsed;
        Get<TextBlock>("Loading").Visibility = Visibility.Collapsed;
        Get<Grid>("Frame").Visibility = Visibility.Collapsed;
        Get<ScrollViewer>("RenameList").Visibility = Visibility.Visible;
        Rows = Get<StackPanel>("Rows");
        ((Grid)Get<ScrollViewer>("Settings").Parent).Children.Add(new ScrollViewer
            { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = Side });
        var close = Get<Button>("Cancel");
        close.Content = Lang.T("Kapat");
        close.Click += (s, e) => Window.Close();
        Window.Closed += (s, e) => Closed = true;
    }

    public static TextBlock Text(string text, string colour = "#F2F3F5")
    {
        return new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)new BrushConverter().ConvertFromString(colour) };
    }

    public void Note(string text)
    {
        var label = Text(text, "#A6A9B4");
        label.Margin = new Thickness(0, 0, 0, 12);
        Side.Children.Add(label);
    }

    public void Say(string text, bool problem)
    {
        Get<TextBlock>("Error").Visibility = Get<TextBlock>("Status").Visibility = Visibility.Collapsed;
        if (text == null) return;
        var line = Get<TextBlock>(problem ? "Error" : "Status");
        line.Text = text;
        line.Visibility = Visibility.Visible;
    }

    // Runs on the window's thread, unless the window is gone.
    public void Later(Action work)
    {
        Window.Dispatcher.BeginInvoke((Action)(() => { if (!Closed) work(); }));
    }
}

// Files with exactly the same content.
class Copies
{
    public long Size;
    public string[] Files; // two or more, the oldest first
}

static class Duplicates
{
    // Every file under the folders; links and junctions are not followed, system files not looked at.
    static IEnumerable<FileInfo> All(IEnumerable<string> folders, Func<bool> cancelled)
    {
        var open = new Stack<DirectoryInfo>(folders.Select(f => new DirectoryInfo(f)));
        while (open.Count > 0 && !cancelled())
        {
            var folder = open.Pop();
            FileSystemInfo[] inside;
            try { inside = folder.GetFileSystemInfos(); }
            catch (UnauthorizedAccessException) { continue; }
            catch (IOException) { continue; }
            foreach (var item in inside)
            {
                if ((item.Attributes & (FileAttributes.ReparsePoint | FileAttributes.System)) != 0) continue;
                if (item is DirectoryInfo) open.Push((DirectoryInfo)item);
                else yield return (FileInfo)item;
            }
        }
    }

    // The fingerprint of the first bytes (all of them when "most" is 0), or null when the file cannot be read.
    static string Print(string file, int most, Func<bool> cancelled)
    {
        try
        {
            using (var hash = SHA256.Create())
            using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                var buffer = new byte[1 << 16];
                long left = most > 0 ? most : long.MaxValue;
                int read;
                while (left > 0 && (read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, left))) > 0)
                {
                    if (cancelled()) throw new OperationCanceledException();
                    hash.TransformBlock(buffer, 0, read, null, 0);
                    left -= read;
                }
                hash.TransformFinalBlock(buffer, 0, 0);
                return Convert.ToBase64String(hash.Hash);
            }
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    // Sizes are compared first, then the beginnings, and only files that still look alike are read to the end.
    public static List<Copies> Find(string[] folders, Action<string> progress, Func<bool> cancelled)
    {
        var sized = All(folders, cancelled).Where(f => f.Length > 0)
            .GroupBy(f => f.FullName, StringComparer.OrdinalIgnoreCase).Select(g => g.First()) // folders may overlap
            .GroupBy(f => f.Length).Where(g => g.Count() > 1).OrderByDescending(g => g.Key).ToList();
        var found = new List<Copies>();
        int done = 0;
        foreach (var size in sized)
        {
            if (cancelled()) break;
            progress(Lang.F("Karşılaştırılıyor… {0} / {1}", ++done, sized.Count));
            foreach (var start in size.GroupBy(f => Print(f.FullName, 1 << 16, cancelled)).Where(g => g.Key != null && g.Count() > 1))
                foreach (var same in start.GroupBy(f => size.Key <= 1 << 16 ? start.Key : Print(f.FullName, 0, cancelled))
                    .Where(g => g.Key != null && g.Count() > 1))
                    found.Add(new Copies { Size = size.Key, Files = same.OrderBy(f => f.LastWriteTimeUtc)
                        .ThenBy(f => f.FullName.Length).Select(f => f.FullName).ToArray() });
        }
        // The most space to be gained first.
        return found.OrderByDescending(c => c.Size * (c.Files.Length - 1)).ToList();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct Operation
    {
        public IntPtr Window;
        public uint Kind;
        public string From, To;
        public ushort Flags;
        public bool Aborted;
        public IntPtr Mappings;
        public string Title;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHFileOperationW(ref Operation operation);

    // To the Recycle Bin. Windows asks first where it could only delete for good.
    public static void Recycle(string file)
    {
        // Other drives have no Recycle Bin: nothing is deleted there.
        if (new DriveInfo(Path.GetPathRoot(file)).DriveType != DriveType.Fixed)
            throw new IOException(Lang.T("Bu sürücüde Geri Dönüşüm Kutusu yok; silinmedi."));
        var operation = new Operation { Kind = 3 /* delete */, From = file + "\0",
            Flags = 0x40 /* undo */ | 0x10 /* no question */ | 0x4 /* silent */ | 0x4000 /* warn before deleting for good */ };
        int result = SHFileOperationW(ref operation);
        if (result != 0 || operation.Aborted || File.Exists(file))
            throw new IOException(Lang.T("Geri Dönüşüm Kutusu'na taşınamadı."));
    }
}

class DuplicatesWindow
{
    const int Shown = 200; // groups listed
    public readonly Sheet Sheet = new Sheet("All In One - " + Lang.T("Kopya Dosyalar"), Lang.T("Kopya Dosyalar"));
    readonly List<Tuple<Copies, CheckBox[]>> groups = new List<Tuple<Copies, CheckBox[]>>();
    readonly Dictionary<string, DateTime> seen = new Dictionary<string, DateTime>(); // when each was last written
    readonly string[] folders;

    public DuplicatesWindow(string[] chosen)
    {
        folders = chosen;
        Sheet.Note(Lang.T("İşaretlediklerin Geri Dönüşüm Kutusu'na gider. Her gruptan en az bir dosya kalır."));
        var delete = Sheet.Get<Button>("Save");
        delete.Content = Lang.T("Sil");
        delete.IsDefault = false;
        delete.Click += (s, e) => Delete(true);
        var extra = Sheet.Get<Button>("Extra");
        extra.Content = Lang.T("Fazlalıkları işaretle");
        extra.Visibility = Visibility.Visible;
        extra.Click += (s, e) =>
        {
            // All but the oldest of each group.
            foreach (var group in groups)
                for (int i = 0; i < group.Item2.Length; i++) group.Item2[i].IsChecked = i > 0;
        };
    }

    public void Search()
    {
        Sheet.Say(Lang.T("Dosyalar taranıyor…"), false);
        Images.Background(() =>
        {
            try
            {
                var found = Duplicates.Find(folders, text => Sheet.Later(() => Sheet.Say(text, false)), () => Sheet.Closed);
                var times = found.Take(Shown).SelectMany(c => c.Files).Distinct().ToDictionary(f => f, File.GetLastWriteTimeUtc);
                Sheet.Later(() =>
                {
                    foreach (var pair in times) seen[pair.Key] = pair.Value;
                    List(found);
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception e)
            {
                Log.Error("kopya arama", e);
                Sheet.Later(() => Sheet.Say(e.Message, true));
            }
        });
    }

    public void List(List<Copies> found)
    {
        Sheet.Say(null, false);
        Sheet.Rows.Children.Clear();
        groups.Clear();
        foreach (var copies in found.Take(Shown))
        {
            var head = Sheet.Text(Lang.F("{0} aynı dosya · her biri {1}", copies.Files.Length, Images.Size(copies.Size)), "#A6A9B4");
            head.Margin = new Thickness(0, 6, 0, 6);
            Sheet.Rows.Children.Add(head);
            var boxes = copies.Files.Select(file =>
            {
                var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
                var show = new Button { Content = Lang.T("Göster"), Padding = new Thickness(10, 2, 10, 2), FontSize = 12 };
                show.Click += (s, e) => Toast.Reveal(file);
                DockPanel.SetDock(show, Dock.Right);
                row.Children.Add(show);
                // The name first, as it tells the files apart; under it the folder.
                var names = new StackPanel { Width = 440 };
                names.Children.Add(new TextBlock { Text = Path.GetFileName(file), TextTrimming = TextTrimming.CharacterEllipsis });
                var place = Sheet.Text(Where(file), "#8A8D99");
                place.FontSize = 11.5;
                place.TextWrapping = TextWrapping.NoWrap;
                place.TextTrimming = TextTrimming.CharacterEllipsis;
                names.Children.Add(place);
                var box = new CheckBox { ToolTip = file, Margin = new Thickness(0, 0, 8, 0), Content = names };
                box.Checked += (s, e) => Count();
                box.Unchecked += (s, e) => Count();
                row.Children.Add(box);
                Sheet.Rows.Children.Add(row);
                return box;
            }).ToArray();
            groups.Add(Tuple.Create(copies, boxes));
        }
        long gain = found.Sum(c => c.Size * (c.Files.Length - 1));
        Sheet.Get<TextBlock>("Details").Text = found.Count == 0 ? Lang.T("Aynı içerikli dosya bulunamadı.")
            : Lang.F("{0} grup · {1} yer kazanılabilir", found.Count, Images.Size(gain))
                + (found.Count > Shown ? "\n" + Lang.F("İlk {0} grup gösteriliyor.", Shown) : "");
        Sheet.Get<Button>("Extra").IsEnabled = groups.Count > 0;
        Count();
    }

    // The folder of a file, from the searched folder on.
    string Where(string file)
    {
        string folder = Path.GetDirectoryName(file);
        var roots = folders.Select(f => f.TrimEnd(Path.DirectorySeparatorChar)).OrderByDescending(f => f.Length);
        string root = roots.FirstOrDefault(f => (folder + Path.DirectorySeparatorChar)
            .StartsWith(f + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        if (root == null) return folder;
        string top = Path.GetFileName(root);
        return (top == "" ? root : top) + folder.Substring(root.Length);
    }

    // The marked files; a problem when some group would lose every copy.
    public string[] Marked(out string problem)
    {
        problem = groups.Any(g => g.Item2.All(b => b.IsChecked == true))
            ? Lang.T("Bir grubun bütün kopyaları işaretli; en az birini bırak.") : null;
        return groups.SelectMany(g => g.Item1.Files.Where((f, i) => g.Item2[i].IsChecked == true)).ToArray();
    }

    void Count()
    {
        string problem;
        var marked = Marked(out problem);
        Sheet.Say(problem ?? (marked.Length == 0 ? null : Lang.F("{0} dosya işaretli", marked.Length)), problem != null);
        Sheet.Get<Button>("Save").IsEnabled = problem == null && marked.Length > 0;
    }

    void Delete(bool ask)
    {
        string problem;
        var marked = Marked(out problem);
        if (problem != null || marked.Length == 0) return;
        if (ask && MessageBox.Show(Sheet.Window, Lang.F("{0} dosya Geri Dönüşüm Kutusu'na taşınsın mı?", marked.Length),
            "All In One", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        var chosen = new HashSet<string>(marked);
        int gone = 0;
        var failed = new List<string>();
        foreach (var group in groups)
        {
            // A copy that stays must still be there and unchanged, or the others are kept too.
            bool safe = group.Item1.Files.Any(f => !chosen.Contains(f) && Unchanged(f, group.Item1.Size));
            foreach (var file in group.Item1.Files.Where(chosen.Contains))
            {
                try
                {
                    if (!safe || !Unchanged(file, group.Item1.Size))
                        throw new IOException(Lang.T("Aramadan sonra değişmiş; silinmedi."));
                    Duplicates.Recycle(file);
                    gone++;
                }
                catch (Exception e)
                {
                    Log.Error("kopya silinemedi: " + file, e);
                    failed.Add(Path.GetFileName(file) + ": " + e.Message);
                }
            }
        }
        // Groups that still have two copies stay in the list.
        List(groups.Select(g => new Copies { Size = g.Item1.Size, Files = g.Item1.Files.Where(File.Exists).ToArray() })
            .Where(c => c.Files.Length > 1).ToList());
        Sheet.Say(Lang.F("{0} dosya Geri Dönüşüm Kutusu'na taşındı.", gone)
            + (failed.Count == 0 ? "" : "\n" + string.Join("\n", failed.Take(3))), failed.Count > 0);
    }

    bool Unchanged(string file, long size)
    {
        var info = new FileInfo(file);
        DateTime then;
        return info.Exists && info.Length == size && seen.TryGetValue(file, out then) && info.LastWriteTimeUtc == then;
    }

    public static void Open(string[] folders)
    {
        var window = new DuplicatesWindow(folders);
        Assets.Show(window.Sheet.Window);
        window.Search();
    }
}

// Sorting the files of a folder into subfolders by their kind or month.
static class Organizer
{
    static readonly Tuple<string, string>[] Kinds =
    {
        Tuple.Create("Görseller", ".jpg .jpeg .jfif .png .gif .bmp .tif .tiff .webp .heic .svg .ico .psd"),
        Tuple.Create("Videolar", ".mp4 .mkv .mov .avi .webm .m4v .wmv .flv .mpg .mpeg .mts .m2ts .3gp"),
        Tuple.Create("Sesler", ".mp3 .wav .m4a .aac .wma .flac .ogg .opus"),
        Tuple.Create("Belgeler", ".pdf .doc .docx .xls .xlsx .ppt .pptx .txt .md .rtf .odt .ods .odp .csv .epub"),
        Tuple.Create("Arşivler", ".zip .rar .7z .tar .gz .iso"),
        Tuple.Create("Programlar", ".exe .msi .msix .appx .apk .bat .cmd .ps1")
    };

    public static string Kind(string file)
    {
        string ext = Path.GetExtension(file).ToLowerInvariant();
        var found = Kinds.FirstOrDefault(k => ext != "" && k.Item2.Split(' ').Contains(ext));
        return Lang.T(found == null ? "Diğer dosyalar" : found.Item1);
    }

    // What would move where, without touching anything. Hidden and system files and shortcuts stay.
    public static List<RenameItem> Plan(string folder, bool byMonth)
    {
        var plan = new List<RenameItem>();
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in new DirectoryInfo(folder).GetFiles().OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            if ((file.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
            string ext = file.Extension.ToLowerInvariant();
            if (ext == ".lnk" || ext == ".url") continue;
            string into = Path.Combine(folder, byMonth ? file.LastWriteTime.ToString("yyyy-MM", CultureInfo.InvariantCulture)
                : Kind(file.Name));
            string stem = Path.GetFileNameWithoutExtension(file.Name), target = Path.Combine(into, file.Name);
            // Nothing is overwritten: a taken name gets a number.
            for (int i = 2; File.Exists(target) || Directory.Exists(target) || !taken.Add(target); i++)
                target = Path.Combine(into, stem + " (" + i + ")" + file.Extension);
            var item = new RenameItem { From = file.FullName, To = target };
            if (File.Exists(into)) item.Problem = Lang.F("\"{0}\" adında bir dosya var; klasör açılamıyor.", Path.GetFileName(into));
            plan.Add(item);
        }
        return plan;
    }

    // Moves them all or none; returns the folders that had to be made.
    public static List<string> Apply(IList<RenameItem> plan)
    {
        var made = plan.Select(p => Path.GetDirectoryName(p.To)).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(d => !Directory.Exists(d)).ToList();
        try
        {
            foreach (var folder in made) Directory.CreateDirectory(folder);
            Renamer.Apply(plan);
        }
        catch
        {
            Empty(made);
            throw;
        }
        return made;
    }

    public static void Undo(IList<RenameItem> moved, IEnumerable<string> made)
    {
        Renamer.Apply(Renamer.Reversed(moved));
        Empty(made);
    }

    // Removes those of the folders that hold nothing.
    static void Empty(IEnumerable<string> folders)
    {
        foreach (var folder in folders)
            if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
    }
}

class OrganizeWindow
{
    const int Shown = 500;
    public readonly Sheet Sheet = new Sheet("All In One - " + Lang.T("Klasörü Düzenle"), Lang.T("Klasörü Düzenle"));
    readonly string folder;
    bool byMonth;
    List<RenameItem> plan, moved;
    List<string> made;

    public OrganizeWindow(string chosen)
    {
        folder = chosen;
        Sheet.Note(Lang.T("Klasörün içindeki dosyalar alt klasörlere taşınır. Alt klasörlere, gizli dosyalara ve kısayollara dokunulmaz."));
        foreach (var choice in new[] { Tuple.Create(Lang.T("Türüne göre"), false), Tuple.Create(Lang.T("Aya göre"), true) })
        {
            bool value = choice.Item2;
            var chip = new RadioButton { Content = choice.Item1, GroupName = "organize", IsChecked = value == byMonth,
                HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 140 };
            chip.SetResourceReference(FrameworkElement.StyleProperty, "Chip");
            chip.Checked += (s, e) =>
            {
                byMonth = value;
                Preview();
            };
            Sheet.Side.Children.Add(chip);
        }
        var move = Sheet.Get<Button>("Save");
        move.Content = Lang.T("Düzenle");
        move.Click += (s, e) => Move();
        var undo = Sheet.Get<Button>("Extra");
        undo.Content = Lang.T("Geri al");
        undo.Click += (s, e) => Undo();
        Preview();
    }

    public void Preview()
    {
        try { plan = Organizer.Plan(folder, byMonth); }
        catch (Exception e)
        {
            plan = new List<RenameItem>();
            Sheet.Say(e.Message, true);
            Sheet.Get<Button>("Save").IsEnabled = false;
            return;
        }
        var first = plan.FirstOrDefault(p => p.Problem != null);
        Sheet.Say(first == null ? null : first.Problem, true);
        List(plan);
        Sheet.Get<TextBlock>("Details").Text = plan.Count == 0 ? Lang.T("Taşınacak dosya yok.")
            : Lang.F("{0} dosya · {1} klasör", plan.Count, plan.Select(p => Path.GetDirectoryName(p.To)).Distinct().Count());
        Sheet.Get<Button>("Save").IsEnabled = first == null && plan.Count > 0;
    }

    // Under the name of each folder, the files that go there.
    void List(List<RenameItem> items)
    {
        Sheet.Rows.Children.Clear();
        foreach (var group in items.Take(Shown).GroupBy(p => Path.GetDirectoryName(p.To)).OrderBy(g => g.Key))
        {
            var head = Sheet.Text("▸ " + Path.GetFileName(group.Key), "#6CB4FF");
            head.Margin = new Thickness(0, 8, 0, 4);
            Sheet.Rows.Children.Add(head);
            foreach (var item in group)
            {
                var line = Sheet.Text(Path.GetFileName(item.From) + (Path.GetFileName(item.From) == Path.GetFileName(item.To) ? ""
                    : "  →  " + Path.GetFileName(item.To)), item.Problem == null ? "#F2F3F5" : "#FF8A80");
                line.Margin = new Thickness(16, 0, 0, 3);
                Sheet.Rows.Children.Add(line);
            }
        }
        if (items.Count > Shown) Sheet.Rows.Children.Add(Sheet.Text(Lang.T("… ve ") + (items.Count - Shown) + Lang.T(" dosya daha"), "#A6A9B4"));
    }

    void Move()
    {
        // The folder may have changed since the preview was made.
        Preview();
        if (!Sheet.Get<Button>("Save").IsEnabled) return;
        try { made = Organizer.Apply(plan); }
        catch (Exception e)
        {
            Log.Error("klasör düzenlenemedi", e);
            Preview();
            Sheet.Say(e.Message, true);
            return;
        }
        moved = plan;
        Sheet.Get<Button>("Save").IsEnabled = false;
        Sheet.Get<Button>("Extra").Visibility = Visibility.Visible;
        Sheet.Get<Button>("Extra").IsEnabled = true;
        Sheet.Say(Lang.F("{0} dosya taşındı.", moved.Count), false);
        Images.Notify(new Outcome { File = moved[0].To, Text = Lang.F("{0} dosya taşındı.", moved.Count) });
    }

    void Undo()
    {
        if (moved == null) return;
        try { Organizer.Undo(moved, made); }
        catch (Exception e)
        {
            Sheet.Say(e.Message, true);
            return;
        }
        moved = null;
        Sheet.Get<Button>("Extra").IsEnabled = false;
        Preview();
        Sheet.Say(Lang.T("Dosyalar eski yerlerine döndü."), false);
    }

    public static void Open(string folder)
    {
        Assets.Show(new OrganizeWindow(folder).Sheet.Window);
    }
}

// The fingerprints a download page quotes, to check that a file arrived as it was sent.
class Checksum
{
    public string File, Sha256, Sha1, Md5;

    public static Checksum Of(string file, Action<double> progress, Func<bool> cancelled)
    {
        using (var strong = SHA256.Create())
        using (var older = SHA1.Create())
        using (var oldest = System.Security.Cryptography.MD5.Create())
        using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            var all = new HashAlgorithm[] { strong, older, oldest };
            var buffer = new byte[1 << 20];
            long done = 0, length = Math.Max(1, stream.Length);
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (cancelled()) throw new OperationCanceledException();
                foreach (var each in all) each.TransformBlock(buffer, 0, read, null, 0);
                progress((double)(done += read) / length);
            }
            foreach (var each in all) each.TransformFinalBlock(buffer, 0, 0);
            Func<HashAlgorithm, string> hex = a => BitConverter.ToString(a.Hash).Replace("-", "").ToLowerInvariant();
            return new Checksum { File = file, Sha256 = hex(strong), Sha1 = hex(older), Md5 = hex(oldest) };
        }
    }

    // Which of the fingerprints the pasted text is, by name; null when none. Spaces and letter case do not count,
    // and a line such as "hash  file name" counts by its hash.
    public string Matches(string pasted)
    {
        var words = (pasted ?? "").ToLowerInvariant().Split(new[] { ' ', '\t', '\r', '\n', '*', ':' },
            StringSplitOptions.RemoveEmptyEntries);
        var joined = string.Concat(words);
        Func<string, bool> found = hash => joined == hash || words.Contains(hash);
        return found(Sha256) ? "SHA-256" : found(Sha1) ? "SHA-1" : found(Md5) ? "MD5" : null;
    }
}

class ChecksumWindow
{
    public readonly Sheet Sheet = new Sheet("All In One - " + Lang.T("Doğrulama Kodu"), Lang.T("Doğrulama Kodu"));
    readonly List<Checksum> found = new List<Checksum>();
    readonly TextBox wanted = new TextBox { Margin = new Thickness(0, 0, 0, 10), TextWrapping = TextWrapping.Wrap, MinHeight = 60 };
    readonly TextBlock verdict = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 15, FontWeight = FontWeights.SemiBold };
    readonly string[] files;

    public ChecksumWindow(string[] chosen)
    {
        files = chosen;
        Sheet.Note(Lang.T("Karşılaştırmak için, dosyayı aldığın yerde yazan kodu buraya yapıştır."));
        System.Windows.Automation.AutomationProperties.SetName(wanted, Lang.T("Beklenen kod"));
        Sheet.Side.Children.Add(wanted);
        Sheet.Side.Children.Add(verdict);
        wanted.TextChanged += (s, e) => Compare();
        Sheet.Get<Button>("Save").Visibility = Visibility.Collapsed;
        Sheet.Get<TextBlock>("Details").Text = files.Length == 1 ? Path.GetFileName(files[0]) : files.Length + Lang.T(" dosya");
    }

    public void Compute()
    {
        Images.Background(() =>
        {
            for (int i = 0; i < files.Length && !Sheet.Closed; i++)
            {
                string file = files[i], count = files.Length > 1 ? (i + 1) + " / " + files.Length + " · " : "";
                int shown = -1;
                try
                {
                    var sum = Checksum.Of(file, p =>
                    {
                        int percent = (int)(p * 100);
                        if (percent == shown) return;
                        shown = percent;
                        Sheet.Later(() => Sheet.Say(count + Lang.Percent(percent), false));
                    }, () => Sheet.Closed);
                    Sheet.Later(() => Add(sum));
                }
                catch (OperationCanceledException) { return; }
                catch (Exception e)
                {
                    Log.Error("doğrulama kodu: " + file, e);
                    Sheet.Later(() => Sheet.Rows.Children.Add(Sheet.Text(Path.GetFileName(file) + ": " + e.Message, "#FF8A80")));
                }
            }
            Sheet.Later(() => Sheet.Say(null, false));
        });
    }

    public void Add(Checksum sum)
    {
        found.Add(sum);
        var name = Sheet.Text(Path.GetFileName(sum.File), "#6CB4FF");
        name.Margin = new Thickness(0, 4, 0, 8);
        Sheet.Rows.Children.Add(name);
        foreach (var each in new[] { Tuple.Create("SHA-256", sum.Sha256), Tuple.Create("SHA-1", sum.Sha1),
            Tuple.Create("MD5", sum.Md5) })
        {
            string value = each.Item2;
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
            var copy = new Button { Content = Lang.T("Kopyala"), Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Bottom };
            copy.Click += (s, e) =>
            {
                try { Ocr.ToClipboard(value); Sheet.Say(each.Item1 + " " + Lang.T("kopyalandı"), false); }
                catch (ExternalException x) { Sheet.Say(x.Message, true); }
            };
            DockPanel.SetDock(copy, Dock.Right);
            row.Children.Add(copy);
            var column = new StackPanel();
            column.Children.Add(Sheet.Text(each.Item1, "#A6A9B4"));
            column.Children.Add(new TextBox { Text = value, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
                FontFamily = new FontFamily("Consolas") });
            row.Children.Add(column);
            Sheet.Rows.Children.Add(row);
        }
        Compare();
    }

    void Compare()
    {
        if (wanted.Text.Trim() == "" || found.Count == 0)
        {
            verdict.Text = "";
            return;
        }
        var match = found.Select(f => new { f.File, Kind = f.Matches(wanted.Text) }).FirstOrDefault(m => m.Kind != null);
        verdict.Text = match == null ? "✗ " + Lang.T("Eşleşmiyor")
            : "✓ " + Lang.F("Eşleşiyor: {0} ({1})", Path.GetFileName(match.File), match.Kind);
        verdict.Foreground = (Brush)new BrushConverter().ConvertFromString(match == null ? "#FF8A80" : "#6FDD8B");
    }

    public static void Open(string[] files)
    {
        var window = new ChecksumWindow(files);
        Assets.Show(window.Sheet.Window);
        window.Compute();
    }
}

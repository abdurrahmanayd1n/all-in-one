// Renaming many files at once: a preview of every old and new name, all or nothing, and an undo.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Path = System.IO.Path;

enum LetterCase { Keep, Lower, Upper }

class RenameOptions
{
    public string Pattern = "{ad}", Find = "", Replace = "";
    public int Start = 1, Digits = 2;
    public LetterCase Case;
}

class RenameItem
{
    public string From, To, Problem; // full paths; Problem null: it can be done

    public bool Changes { get { return !string.Equals(From, To, StringComparison.Ordinal); } }
}

static class Renamer
{
    static readonly CultureInfo Turkish = new CultureInfo("tr-TR"); // i and İ, ı and I

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)] static extern int StrCmpLogicalW(string a, string b);

    // By name the way Explorer sorts (2 before 10), or oldest first.
    public static string[] Order(IEnumerable<string> files, bool byDate)
    {
        var list = files.ToList();
        list.Sort((a, b) =>
        {
            int result = byDate ? File.GetLastWriteTimeUtc(a).CompareTo(File.GetLastWriteTimeUtc(b)) : 0;
            return result != 0 ? result : StrCmpLogicalW(Path.GetFileName(a), Path.GetFileName(b));
        });
        return list.ToArray();
    }

    static string Swap(string text, string find, string replacement)
    {
        if (find == "") return text;
        var result = new System.Text.StringBuilder();
        int at = 0, hit;
        while ((hit = Turkish.CompareInfo.IndexOf(text, find, at, CompareOptions.IgnoreCase)) >= 0)
        {
            result.Append(text, at, hit - at).Append(replacement);
            at = hit + find.Length;
        }
        return result.Append(text.Substring(at)).ToString();
    }

    // The new name without folder and extension.
    public static string Stem(string stem, int number, DateTime date, RenameOptions o)
    {
        // The old name goes in last, so braces inside it stay as they are.
        string name = o.Pattern.Replace("{n}", number.ToString().PadLeft(Math.Max(1, o.Digits), '0'))
            .Replace("{tarih}", date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
            .Replace("{ad}", Swap(stem, o.Find, o.Replace));
        return o.Case == LetterCase.Lower ? name.ToLower(Turkish) : o.Case == LetterCase.Upper ? name.ToUpper(Turkish) : name;
    }

    // What would happen, without touching anything. taken: whether a path exists.
    public static List<RenameItem> Plan(string[] files, RenameOptions o, Func<string, bool> taken)
    {
        var plan = new List<RenameItem>();
        for (int i = 0; i < files.Length; i++)
        {
            string file = files[i], ext = Path.GetExtension(file), stem = Path.GetFileNameWithoutExtension(file);
            if (stem == "") { stem = ext; ext = ""; } // ".gitignore" is all name
            string name = Stem(stem, o.Start + i, File.Exists(file) ? File.GetLastWriteTime(file) : DateTime.Today, o);
            var item = new RenameItem { From = file, To = file, Problem = Naming.Problem(name) };
            if (item.Problem == null) item.To = Path.Combine(Path.GetDirectoryName(file), name + ext);
            plan.Add(item);
        }
        var wanted = plan.GroupBy(p => p.To, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(),
            StringComparer.OrdinalIgnoreCase);
        // A name held by a file that is itself being renamed becomes free.
        var leaving = new HashSet<string>(plan.Where(p => p.Problem == null && p.Changes).Select(p => p.From),
            StringComparer.OrdinalIgnoreCase);
        foreach (var item in plan.Where(p => p.Problem == null))
        {
            if (wanted[item.To] > 1) item.Problem = Lang.T("Aynı ad birden fazla dosyaya veriliyor.");
            else if (item.Changes && !leaving.Contains(item.To) && taken(item.To)) item.Problem = Lang.T("Bu adda bir dosya zaten var.");
        }
        return plan;
    }

    // Renames in two steps so that files may exchange names. If anything fails, everything is put back.
    public static void Apply(IList<RenameItem> items)
    {
        var done = new List<Tuple<string, string>>(); // moved from, moved to
        Action<string, string> move = (from, to) =>
        {
            File.Move(from, to); // never overwrites
            done.Add(Tuple.Create(from, to));
        };
        try
        {
            var temps = new List<string>();
            foreach (var item in items)
            {
                string temp = Path.Combine(Path.GetDirectoryName(item.From), "~aio-" + Guid.NewGuid().ToString("N"));
                move(item.From, temp);
                temps.Add(temp);
            }
            for (int i = 0; i < items.Count; i++) move(temps[i], items[i].To);
        }
        catch (Exception e)
        {
            var stuck = new List<string>();
            for (int i = done.Count - 1; i >= 0; i--)
            {
                try { File.Move(done[i].Item2, done[i].Item1); }
                catch (Exception) { stuck.Add(done[i].Item2); }
            }
            throw new IOException(Lang.T("Yeniden adlandırılamadı: ") + e.Message + (stuck.Count == 0 ? Lang.T("\nHiçbir dosya değişmedi.")
                : Lang.T("\nGeri alınamayan dosyalar: ") + string.Join(", ", stuck)), e);
        }
    }

    public static List<RenameItem> Reversed(IEnumerable<RenameItem> items)
    {
        return items.Select(i => new RenameItem { From = i.To, To = i.From }).ToList();
    }
}

class RenameWindow
{
    const int Shown = 500; // rows in the preview; all files are renamed
    public readonly Window Window = Assets.Window("Editor.xaml");
    string[] files; // as they are called now
    List<RenameItem> plan = new List<RenameItem>(), last; // last: what "undo" reverses
    LetterCase letters;
    bool byDate;

    T Get<T>(string name) where T : class { return (T)Window.FindName(name); }

    public RenameWindow(string[] chosen)
    {
        files = chosen;
        Assets.Dress(Window);
        Window.Title = Lang.T("Yeniden Adlandır - ") + files.Length + Lang.T(" dosya");
        Get<TextBlock>("Heading").Text = Lang.T("Yeniden Adlandır");
        Get<ScrollViewer>("Settings").Visibility = Visibility.Collapsed;
        Get<ScrollViewer>("Renaming").Visibility = Visibility.Visible;
        Get<TextBlock>("Loading").Visibility = Visibility.Collapsed;
        Get<Grid>("Frame").Visibility = Visibility.Collapsed;
        Get<ScrollViewer>("RenameList").Visibility = Visibility.Visible;

        var pattern = Get<TextBox>("PatternBox");
        foreach (var token in new[] { Tuple.Create("{ad}", Lang.T("Eski ad")), Tuple.Create("{n}", Lang.T("Numara")), Tuple.Create("{tarih}", Lang.T("Tarih")) })
        {
            string value = token.Item1;
            var button = new Button { Content = "+ " + token.Item2, Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 0, 6, 0),
                ToolTip = value + " ekler" };
            button.Click += (s, e) =>
            {
                int at = pattern.SelectionStart;
                pattern.Text = pattern.Text.Remove(at, pattern.SelectionLength).Insert(at, value);
                pattern.SelectionStart = at + value.Length;
                pattern.Focus();
            };
            Get<WrapPanel>("TokenButtons").Children.Add(button);
        }
        pattern.SelectionStart = pattern.Text.Length;
        foreach (string name in new[] { "PatternBox", "FindBox", "ReplaceBox", "StartBox", "DigitsBox" })
            Get<TextBox>(name).TextChanged += (s, e) => Preview();
        Chip("CaseChips", "case", Lang.T("Aynı"), true, () => letters = LetterCase.Keep);
        Chip("CaseChips", "case", Lang.T("küçük"), false, () => letters = LetterCase.Lower);
        Chip("CaseChips", "case", Lang.T("BÜYÜK"), false, () => letters = LetterCase.Upper);
        Chip("OrderChips", "order", Lang.T("Ada göre"), true, () => byDate = false);
        Chip("OrderChips", "order", Lang.T("Tarihe göre"), false, () => byDate = true);

        var apply = Get<Button>("Save");
        apply.Content = Lang.T("Adlandır");
        apply.Click += (s, e) => Rename();
        var undo = Get<Button>("Extra");
        undo.Content = Lang.T("Geri al");
        undo.Click += (s, e) => Undo();
        Get<Button>("Cancel").Content = Lang.T("Kapat");
        Get<Button>("Cancel").Click += (s, e) => Window.Close();
        Preview();
    }

    void Chip(string panel, string group, string label, bool picked, Action pick)
    {
        var chip = new RadioButton { Content = label, GroupName = group, IsChecked = picked };
        chip.SetResourceReference(FrameworkElement.StyleProperty, "Chip");
        chip.Checked += (s, e) =>
        {
            pick();
            Preview();
        };
        Get<WrapPanel>(panel).Children.Add(chip);
    }

    RenameOptions Options()
    {
        var o = new RenameOptions
        {
            Pattern = Get<TextBox>("PatternBox").Text, Find = Get<TextBox>("FindBox").Text,
            Replace = Get<TextBox>("ReplaceBox").Text, Case = letters
        };
        if (!int.TryParse(Get<TextBox>("StartBox").Text, out o.Start) || o.Start < 0) o.Start = 1;
        if (!int.TryParse(Get<TextBox>("DigitsBox").Text, out o.Digits)) o.Digits = 1;
        return o;
    }

    void Preview()
    {
        try
        {
            plan = Renamer.Plan(Renamer.Order(files, byDate), Options(), path => File.Exists(path) || Directory.Exists(path));
        }
        catch (Exception e) // a file disappeared or cannot be read
        {
            plan = new List<RenameItem>();
            Say(e.Message, true);
            Get<Button>("Save").IsEnabled = false;
            return;
        }
        int changes = plan.Count(p => p.Problem == null && p.Changes), problems = plan.Count(p => p.Problem != null);
        List();
        Get<TextBlock>("Details").Text = files.Length + Lang.T(" dosya · ") + changes + Lang.T(" tanesinin adı değişecek");
        var first = plan.FirstOrDefault(p => p.Problem != null);
        Say(first == null ? null : problems + Lang.T(" dosyada sorun var: ") + first.Problem, true);
        Get<Button>("Save").IsEnabled = problems == 0 && changes > 0;
    }

    // Problems first, so they are never hidden below the shown rows.
    void List()
    {
        var rows = Get<StackPanel>("Rows");
        rows.Children.Clear();
        foreach (var item in plan.OrderBy(p => p.Problem == null ? 1 : 0).Take(Shown))
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 7), ToolTip = item.Problem };
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.Children.Add(Cell(Path.GetFileName(item.From), 0, "#A6A9B4"));
            row.Children.Add(Cell("→", 1, "#6B6E79"));
            row.Children.Add(Cell(item.Problem == null ? Path.GetFileName(item.To) : item.Problem, 2,
                item.Problem != null ? "#FF8A80" : item.Changes ? "#F2F3F5" : "#6B6E79"));
            rows.Children.Add(row);
        }
        if (plan.Count > Shown) rows.Children.Add(Cell(Lang.T("… ve ") + (plan.Count - Shown) + Lang.T(" dosya daha"), 0, "#A6A9B4"));
    }

    static TextBlock Cell(string text, int column, string color)
    {
        var cell = new TextBlock { Text = text, TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = (Brush)new BrushConverter().ConvertFromString(color) };
        Grid.SetColumn(cell, column);
        return cell;
    }

    void Say(string text, bool problem)
    {
        var line = Get<TextBlock>(problem ? "Error" : "Status");
        Get<TextBlock>("Error").Visibility = Get<TextBlock>("Status").Visibility = Visibility.Collapsed;
        if (text == null) return;
        line.Text = text;
        line.Visibility = Visibility.Visible;
    }

    void Rename()
    {
        // The folder may have changed since the preview was made.
        Preview();
        if (!Get<Button>("Save").IsEnabled) return;
        var changes = plan.Where(p => p.Changes).ToList();
        try { Renamer.Apply(changes); }
        catch (Exception e)
        {
            Preview();
            Say(e.Message, true);
            return;
        }
        last = changes;
        var renamed = changes.ToDictionary(c => c.From, c => c.To);
        files = files.Select(f => renamed.ContainsKey(f) ? renamed[f] : f).ToArray();
        // Applying the same pattern again to the new names is rarely meant: wait for a change.
        Done(changes, changes.Count + Lang.T(" dosya yeniden adlandırıldı."));
        Get<Button>("Extra").Visibility = Visibility.Visible;
        Get<Button>("Extra").IsEnabled = true;
        Images.Notify(new Outcome { File = changes[0].To, Text = changes.Count + Lang.T(" dosya yeniden adlandırıldı") });
    }

    void Undo()
    {
        if (last == null) return;
        var back = Renamer.Reversed(last);
        try { Renamer.Apply(back); }
        catch (Exception e)
        {
            Say(e.Message, true);
            return;
        }
        last = null;
        var renamed = back.ToDictionary(c => c.From, c => c.To);
        files = files.Select(f => renamed.ContainsKey(f) ? renamed[f] : f).ToArray();
        Done(back, Lang.T("Eski adlar geri getirildi."));
        Get<Button>("Extra").IsEnabled = false;
    }

    void Done(List<RenameItem> moved, string message)
    {
        plan = moved;
        List();
        Get<TextBlock>("Details").Text = files.Length + Lang.T(" dosya");
        Say(message, false);
        Get<Button>("Save").IsEnabled = false;
    }

    public static void Open(string[] files)
    {
        Assets.Show(new RenameWindow(files).Window);
    }
}

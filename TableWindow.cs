// The table read from the screen, to be checked and corrected before it is copied or saved.
using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;

class TableWindow
{
    public readonly Window Window = Assets.Window("Editor.xaml");
    readonly DataGrid grid = new DataGrid();
    readonly ObservableCollection<string[]> rows = new ObservableCollection<string[]>();

    T Get<T>(string name) where T : class { return (T)Window.FindName(name); }

    public TableWindow(string[][] table, BitmapSource source)
    {
        Assets.Dress(Window);
        Window.Title = Lang.T("All In One - Tablo");
        Get<TextBlock>("Heading").Text = Lang.T("Tablo");
        Get<ScrollViewer>("Settings").Visibility = Visibility.Collapsed;
        Get<TextBlock>("Loading").Visibility = Visibility.Collapsed;
        Get<Grid>("Frame").Visibility = Visibility.Collapsed;
        foreach (var row in table) rows.Add((string[])row.Clone());

        Brush back = Paint("#1B1C23"), other = Paint("#22242D"), line = Paint("#3A3C47"), ink = Paint("#F2F3F5");
        grid.AutoGenerateColumns = false;
        grid.HeadersVisibility = DataGridHeadersVisibility.None;
        grid.CanUserAddRows = false;
        grid.SelectionUnit = DataGridSelectionUnit.Cell;
        grid.Background = Brushes.Transparent;
        grid.BorderThickness = new Thickness(0);
        grid.Foreground = ink;
        grid.RowBackground = back;
        grid.AlternatingRowBackground = other;
        grid.AlternationCount = 2;
        grid.HorizontalGridLinesBrush = grid.VerticalGridLinesBrush = line;
        grid.FontSize = 14;
        grid.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        var cell = new Style(typeof(DataGridCell));
        cell.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 5, 8, 5)));
        cell.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
        var picked = new Trigger { Property = DataGridCell.IsSelectedProperty, Value = true };
        picked.Setters.Add(new Setter(Control.BackgroundProperty, Paint("#0A84FF")));
        picked.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White));
        cell.Triggers.Add(picked);
        grid.CellStyle = cell;
        grid.ItemsSource = rows;
        Columns(table[0].Length);
        System.Windows.Automation.AutomationProperties.SetName(grid, Lang.T("Tablo"));
        Get<Grid>("Stage").Children.Add(grid);

        // The right side: the piece of the screen it came from, and what can be done.
        var side = new StackPanel { Margin = new Thickness(0, 0, 12, 12) };
        side.Children.Add(Label(Lang.T("Ekrandaki hali")));
        side.Children.Add(new Border { CornerRadius = new CornerRadius(8), Background = Paint("#101116"),
            Padding = new Thickness(8), Margin = new Thickness(0, 0, 0, 14),
            Child = new Image { Source = source, MaxHeight = 260, StretchDirection = StretchDirection.DownOnly } });
        side.Children.Add(Label(Lang.T("Hücreye çift tıklayıp (ya da F2) düzelt. Enter sonraki satıra geçer.")));
        var buttons = new WrapPanel();
        buttons.Children.Add(Action(Lang.T("Satırı sil"), () => Remove(true)));
        buttons.Children.Add(Action(Lang.T("Sütunu sil"), () => Remove(false)));
        side.Children.Add(buttons);
        ((Grid)Get<ScrollViewer>("Settings").Parent).Children.Add(new ScrollViewer {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = side });

        var copy = Get<Button>("Save");
        copy.Content = Lang.T("Kopyala");
        copy.IsEnabled = true;
        copy.IsDefault = false; // Enter belongs to the cells
        copy.Click += (s, e) => Try(() =>
        {
            Ocr.ToClipboard(TableText.Tsv(Rows()));
            Window.Close();
            Images.Notify(new Outcome { Text = Lang.T("Tablo panoya kopyalandı\n") + rows.Count + Lang.T(" satır, ") + grid.Columns.Count
                + Lang.T(" sütun · Excel'e yapıştır") });
        });
        var save = Get<Button>("Extra");
        save.Content = Lang.T("CSV kaydet");
        save.Visibility = Visibility.Visible;
        save.IsEnabled = true;
        save.Click += (s, e) => Try(SaveCsv);
        var close = Get<Button>("Cancel");
        close.Content = Lang.T("Kapat");
        close.IsCancel = false; // Esc cancels an edit first
        close.Click += (s, e) => Window.Close();
        Window.KeyDown += (s, e) => { if (e.Key == System.Windows.Input.Key.Escape) Window.Close(); };
        Count();
    }

    static Brush Paint(string hex) { return (Brush)new BrushConverter().ConvertFromString(hex); }

    TextBlock Label(string text)
    {
        var label = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        label.SetResourceReference(FrameworkElement.StyleProperty, "Label");
        return label;
    }

    Button Action(string label, Action run)
    {
        var button = new Button { Content = label, Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 8, 8) };
        button.Click += (s, e) => run();
        return button;
    }

    void Columns(int count)
    {
        grid.Columns.Clear();
        // Room around the words, shown and being corrected.
        var shown = new Style(typeof(TextBlock));
        shown.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(10, 6, 10, 6)));
        for (int i = 0; i < count; i++)
            grid.Columns.Add(new DataGridTextColumn { Binding = new Binding("[" + i + "]") { Mode = BindingMode.TwoWay },
                MaxWidth = 360, MinWidth = 90, ElementStyle = shown });
    }

    // Ends a cell being edited so that its text counts.
    public string[][] Rows()
    {
        grid.CommitEdit(DataGridEditingUnit.Row, true);
        return rows.Select(r => r.Select(c => c ?? "").ToArray()).ToArray();
    }

    // The row or the column of the chosen cell.
    void Remove(bool row)
    {
        var current = grid.CurrentCell;
        if (current.Column == null || !(current.Item is string[])) return;
        var all = Rows();
        int y = rows.IndexOf((string[])current.Item), x = current.Column.DisplayIndex;
        if (y < 0) return;
        if (row ? rows.Count < 2 : grid.Columns.Count < 2) return; // a table keeps at least one of each
        if (row) rows.RemoveAt(y);
        else
        {
            rows.Clear();
            foreach (var each in Without(all, x)) rows.Add(each);
            Columns(rows[0].Length);
        }
        Count();
    }

    public static string[][] Without(string[][] table, int column)
    {
        return table.Select(r => r.Where((c, i) => i != column).ToArray()).ToArray();
    }

    void Count()
    {
        Get<TextBlock>("Details").Text = rows.Count + Lang.T(" satır × ") + grid.Columns.Count + Lang.T(" sütun");
    }

    void SaveCsv()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = "tablo.csv",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) };
        if (dialog.ShowDialog(Window) != true) return;
        // With a byte order mark Excel reads Turkish letters correctly.
        File.WriteAllText(dialog.FileName, TableText.Csv(Rows(), CultureInfo.CurrentCulture.TextInfo.ListSeparator),
            new UTF8Encoding(true));
        Images.Notify(new Outcome { File = dialog.FileName, Text = Path.GetFileName(dialog.FileName) });
    }

    void Try(Action work)
    {
        var error = Get<TextBlock>("Error");
        error.Visibility = Visibility.Collapsed;
        try { work(); }
        catch (Exception e)
        {
            Log.Error("tablo", e);
            error.Text = e.Message;
            error.Visibility = Visibility.Visible;
        }
    }

    public static void Open(string[][] table, BitmapSource source)
    {
        Assets.Show(new TableWindow(table, source).Window);
    }
}

// Joining PDFs in a chosen order, or taking pages out of one; the first page is shown on the left.
using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Path = System.IO.Path;

class PdfWindow
{
    public readonly Window Window = Assets.Window("Editor.xaml");
    readonly string[] paths;
    bool each, nameEdited, syncing, running;
    int loading, pages;

    bool Joins { get { return paths.Length > 1; } }

    T Get<T>(string name) where T : class { return (T)Window.FindName(name); }

    public PdfWindow(string[] files)
    {
        paths = (string[])files.Clone();
        Assets.Dress(Window);
        string title = Joins ? Lang.T("PDF Birleştir") : Lang.T("PDF Sayfalarını Ayır");
        Window.Title = title + " - " + (Joins ? paths.Length + Lang.T(" dosya") : Path.GetFileName(paths[0]));
        Get<TextBlock>("Heading").Text = title;
        Get<Canvas>("Selection").Visibility = Visibility.Collapsed;
        var name = Get<TextBox>("NameBox");
        Get<TextBlock>("Extension").Text = ".pdf";
        Get<CheckBox>("UseSuffix").Visibility = Get<TextBox>("SuffixBox").Visibility = Visibility.Collapsed;
        name.TextChanged += (s, e) =>
        {
            if (syncing) return;
            nameEdited = true;
            Validate();
        };

        if (!Joins)
        {
            Get<StackPanel>("PdfSplitPanel").Visibility = Visibility.Visible;
            foreach (var choice in new[] { Tuple.Create(Lang.T("Seçtiğim sayfalar"), false), Tuple.Create(Lang.T("Her sayfa ayrı"), true) })
            {
                var chip = new RadioButton { Content = choice.Item1, GroupName = "pdf", IsChecked = choice.Item2 == each };
                chip.SetResourceReference(FrameworkElement.StyleProperty, "Chip");
                bool value = choice.Item2;
                chip.Checked += (s, e) =>
                {
                    each = value;
                    Get<StackPanel>("PdfRangePanel").Visibility = Get<TextBlock>("NameLabel").Visibility
                        = ((DockPanel)name.Parent).Visibility = each ? Visibility.Collapsed : Visibility.Visible;
                    Validate();
                };
                Get<WrapPanel>("PdfSplitChips").Children.Add(chip);
            }
            Get<TextBox>("PdfRangeBox").Text = "1";
            Get<TextBox>("PdfRangeBox").TextChanged += (s, e) => { Validate(); Show(0); };
        }

        var save = Get<Button>("Save");
        save.Content = Joins ? Lang.T("Birleştir") : Lang.T("Ayır");
        save.Click += (s, e) => Start();
        var close = Get<Button>("Cancel");
        close.Content = Lang.T("Kapat");
        close.Click += (s, e) => Window.Close();
        UpdateName();
        if (Joins) Assets.Order(Window, paths, Show, UpdateName);
        Window.Loaded += (s, e) => Show(0);
        Validate();
    }

    void UpdateName()
    {
        if (!nameEdited)
        {
            syncing = true;
            Get<TextBox>("NameBox").Text = Naming.Compose(paths[0], Joins ? "_birlestirilmis" : "_sayfalar", false, "");
            syncing = false;
        }
        Validate();
    }

    // What is wrong with what is asked for, or null.
    public string Problem()
    {
        if (!Joins && !each && pages > 0 && Pdf.Range(Get<TextBox>("PdfRangeBox").Text, pages) == null)
            return Lang.T("Sayfaları 1-3, 5 biçiminde yaz (dosyada ") + pages + Lang.T(" sayfa var).");
        return !Joins && each ? null : Naming.Problem(Get<TextBox>("NameBox").Text);
    }

    void Validate()
    {
        if (running) return;
        string problem = Problem();
        Error(problem);
        Get<Button>("Save").IsEnabled = problem == null;
    }

    void Error(string problem)
    {
        var error = Get<TextBlock>("Error");
        error.Text = problem ?? "";
        error.Visibility = problem == null ? Visibility.Collapsed : Visibility.Visible;
    }

    // The first page of a file; of a single file, the first of the chosen pages.
    void Show(int at)
    {
        int mine = ++loading;
        string path = paths[at];
        int page = 1;
        if (!Joins)
            int.TryParse(Get<TextBox>("PdfRangeBox").Text.Split(',', '-')[0].Trim(), out page);
        Images.Background(() =>
        {
            System.Windows.Media.Imaging.BitmapSource picture = null;
            string facts;
            int count = 0;
            try
            {
                var document = Pdf.Open(path);
                count = (int)document.PageCount;
                picture = Pdf.Render(document, Math.Max(0, Math.Min(count, page) - 1), 900);
                facts = count + Lang.T(" sayfa · ") + Images.Size(new FileInfo(path).Length);
            }
            catch (Exception e) { facts = e.Message; }
            Window.Dispatcher.BeginInvoke((Action)(() =>
            {
                if (mine != loading) return;
                Get<TextBlock>("Details").Text = Path.GetFileName(path) + "\n" + facts;
                var wait = Get<TextBlock>("Loading");
                wait.Text = Lang.T("Önizleme yok");
                wait.Visibility = picture == null ? Visibility.Visible : Visibility.Collapsed;
                if (picture == null) Get<Grid>("Frame").Visibility = Visibility.Hidden;
                else
                {
                    Assets.Fit(Window, picture);
                    Get<Canvas>("Selection").Visibility = Visibility.Collapsed;
                }
                if (!Joins) { pages = count; Validate(); }
            }));
        });
    }

    void Start()
    {
        if (running || Problem() != null) return;
        running = true;
        Get<Button>("Save").IsEnabled = false;
        var status = Get<TextBlock>("Status");
        status.Text = Lang.T("Hazırlanıyor…");
        status.Visibility = Visibility.Visible;
        Error(null);
        var order = (string[])paths.Clone();
        string name = Get<TextBox>("NameBox").Text, range = Get<TextBox>("PdfRangeBox").Text;
        bool all = each;
        Images.Background(() =>
        {
            Outcome outcome = null;
            string failure = null;
            try
            {
                outcome = order.Length > 1 ? Pdf.Merge(order, name) : all ? Pdf.SplitEach(order[0])
                    : Pdf.Extract(order[0], range, name);
            }
            catch (Exception e)
            {
                Log.Error("pdf: " + order[0], e);
                failure = e.Message;
            }
            Window.Dispatcher.BeginInvoke((Action)(() =>
            {
                running = false;
                status.Visibility = Visibility.Collapsed;
                if (failure == null)
                {
                    Images.Notify(outcome);
                    Window.Close();
                    return;
                }
                Get<Button>("Save").IsEnabled = true;
                Error(failure);
            }));
        });
    }

    public static void Open(string[] files)
    {
        Assets.Show(new PdfWindow(files).Window);
    }
}

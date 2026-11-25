// The application's own settings and their window.
using System;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

static class Prefs
{
    // Paths under HKEY_CURRENT_USER; the self-test points them elsewhere.
    public static string Store = @"Software\AllInOne";
    public static string RunList = @"Software\Microsoft\Windows\CurrentVersion\Run";
    // Where Windows notes the startup entries switched off in Task Manager.
    public static string Approved = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    public const int ShortestHold = 150, LongestHold = 1000, UsualHold = 350;
    const string Entry = "AllInOne";
    static int hold; // 0: not read yet

    // How long Left Alt is held before the menu opens, in milliseconds.
    public static int Hold
    {
        get
        {
            if (hold == 0)
            {
                object value = Registry.GetValue(@"HKEY_CURRENT_USER\" + Store, "HoldMs", UsualHold);
                hold = Limit(value is int ? (int)value : UsualHold);
            }
            return hold;
        }
        set
        {
            hold = Limit(value);
            Registry.SetValue(@"HKEY_CURRENT_USER\" + Store, "HoldMs", hold);
        }
    }

    static int Limit(int value)
    {
        return Math.Max(ShortestHold, Math.Min(LongestHold, value));
    }

    public static void Forget() { hold = 0; }

    public static int Number(string name, int fallback)
    {
        object value = Registry.GetValue(@"HKEY_CURRENT_USER\" + Store, name, fallback);
        return value is int ? (int)value : fallback;
    }

    public static void SetNumber(string name, int value)
    {
        Registry.SetValue(@"HKEY_CURRENT_USER\" + Store, name, value);
    }

    // The colour picker's own icon next to the clock.
    public static bool ShowPicker
    {
        get { return Number("Picker", 1) != 0; }
        set { SetNumber("Picker", value ? 1 : 0); }
    }

    // Picked colours are copied as rgb(…) instead of #RRGGBB.
    public static bool CopyRgb
    {
        get { return Number("CopyRgb", 0) != 0; }
        set { SetNumber("CopyRgb", value ? 1 : 0); }
    }

    // Whether Windows starts the app at sign-in.
    public static bool Autostart
    {
        get
        {
            using (var run = Registry.CurrentUser.OpenSubKey(RunList))
                if (run == null || run.GetValue(Entry) == null) return false;
            using (var approved = Registry.CurrentUser.OpenSubKey(Approved))
            {
                var state = approved == null ? null : approved.GetValue(Entry) as byte[];
                return state == null || state.Length == 0 || (state[0] & 1) == 0; // an odd first byte: switched off
            }
        }
        set
        {
            // kur.ps1 reads this, so installing a new version keeps the choice.
            Registry.SetValue(@"HKEY_CURRENT_USER\" + Store, "Autostart", value ? 1 : 0);
            using (var run = Registry.CurrentUser.CreateSubKey(RunList))
            {
                if (value) run.SetValue(Entry, "\"" + System.Windows.Forms.Application.ExecutablePath + "\"");
                else run.DeleteValue(Entry, false);
            }
            if (value)
                using (var approved = Registry.CurrentUser.OpenSubKey(Approved, true))
                    if (approved != null) approved.DeleteValue(Entry, false);
        }
    }
}

class Preferences
{
    static Preferences shown; // only one settings window at a time

    public readonly Window Window = Assets.Window("Editor.xaml");

    T Get<T>(string name) where T : class { return (T)Window.FindName(name); }

    public Preferences()
    {
        Assets.Dress(Window);
        Window.Title = "All In One - Ayarlar";
        Get<TextBlock>("Heading").Text = "Ayarlar";
        Get<TextBlock>("Details").Text = "Değişiklikler hemen uygulanır.";
        Get<ScrollViewer>("Settings").Visibility = Visibility.Collapsed;
        Get<ScrollViewer>("Preferences").Visibility = Visibility.Visible;
        Get<TextBlock>("Loading").Visibility = Visibility.Collapsed;
        Get<Grid>("Frame").Visibility = Visibility.Collapsed;
        Get<StackPanel>("About").Visibility = Visibility.Visible;
        Get<Image>("AboutIcon").Source = Assets.Icon();
        Get<Button>("Save").Visibility = Visibility.Collapsed;
        var close = Get<Button>("Cancel");
        close.Content = "Kapat";
        close.Click += (s, e) => Window.Close();

        var slider = Get<Slider>("HoldSlider");
        slider.Value = Prefs.Hold;
        Action showHold = () => Get<TextBlock>("HoldValue").Text = (int)slider.Value + " ms";
        showHold();
        slider.ValueChanged += (s, e) => { showHold(); Try(() => Prefs.Hold = (int)slider.Value); };

        var start = Get<CheckBox>("Autostart");
        start.IsChecked = Prefs.Autostart;
        start.Click += (s, e) =>
        {
            Try(() => Prefs.Autostart = start.IsChecked == true);
            start.IsChecked = Prefs.Autostart; // shows what really applies
        };

        var picker = Get<CheckBox>("ShowPicker");
        picker.IsChecked = Prefs.ShowPicker;
        picker.Click += (s, e) => Try(() =>
        {
            if (picker.IsChecked == true) Picker.Reopen();
            else
            {
                Prefs.ShowPicker = false;
                Picker.Refresh();
            }
        });

        var use = Get<CheckBox>("FixedSuffix");
        var suffix = Get<TextBox>("FixedSuffixBox");
        use.IsChecked = Naming.UseSuffix;
        suffix.Text = Naming.Suffix;
        Action changed = () =>
        {
            suffix.IsEnabled = use.IsChecked == true;
            string problem = Naming.Problem("x" + suffix.Text + "x");
            var hint = Get<TextBlock>("FixedSuffixHint");
            hint.Text = problem ?? (use.IsChecked == true ? "Örnek: tatil" + suffix.Text + ".jpg"
                : "Kapalıyken işleme göre ad önerilir.");
            if (problem == null) Try(() => Naming.Remember(use.IsChecked == true, suffix.Text));
        };
        use.Click += (s, e) => changed();
        suffix.TextChanged += (s, e) => changed();
        suffix.IsEnabled = use.IsChecked == true;
        Get<TextBlock>("FixedSuffixHint").Text = use.IsChecked == true ? "Örnek: tatil" + suffix.Text + ".jpg"
            : "Kapalıyken işleme göre ad önerilir.";
        Window.Closed += (s, e) => { if (shown == this) shown = null; };
    }

    // A refused registry write is told instead of ending the app.
    void Try(Action write)
    {
        var error = Get<TextBlock>("Error");
        try
        {
            write();
            error.Visibility = Visibility.Collapsed;
        }
        catch (Exception e)
        {
            error.Text = "Ayar kaydedilemedi: " + e.Message;
            error.Visibility = Visibility.Visible;
        }
    }

    public static void Open()
    {
        if (shown != null)
        {
            shown.Window.WindowState = WindowState.Normal;
            shown.Window.Activate();
            return;
        }
        shown = new Preferences();
        Assets.Show(shown.Window);
    }
}

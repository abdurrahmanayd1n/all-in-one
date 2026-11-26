// The interface in English: Turkish is written in the code and looked up in English.cs.
using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

enum Tongue { System, Turkish, English }

static partial class Lang
{
    public static Tongue Chosen
    {
        get { int value = Prefs.Number("Tongue", 0); return value == 1 ? Tongue.Turkish : value == 2 ? Tongue.English : Tongue.System; }
        set { Prefs.SetNumber("Tongue", (int)value); }
    }

    static bool? english; // decided once: a change applies at the next start

    public static bool English
    {
        get
        {
            if (english == null)
                english = Chosen == Tongue.English
                    || Chosen == Tongue.System && CultureInfo.CurrentUICulture.TwoLetterISOLanguageName != "tr";
            return english.Value;
        }
        set { english = value; } // the self-test chooses
    }

    public static string T(string turkish)
    {
        string found;
        return turkish != null && English && Words.TryGetValue(turkish, out found) ? found : turkish;
    }

    public static string F(string turkish, params object[] values)
    {
        return string.Format(T(turkish), values);
    }

    // "%50" in Turkish, "50%" in English.
    public static string Percent(int number)
    {
        return English ? number + "%" : "%" + number;
    }

    // Puts a window read from XAML into the chosen language.
    public static void Translate(DependencyObject item)
    {
        if (!English) return;
        var element = item as FrameworkElement;
        if (element != null)
        {
            if (element.ToolTip is string) element.ToolTip = T((string)element.ToolTip);
            string name = AutomationProperties.GetName(element);
            if (name != "") AutomationProperties.SetName(element, T(name));
        }
        var window = item as Window;
        if (window != null) window.Title = T(window.Title);
        var block = item as TextBlock;
        if (block != null && block.Text != "") block.Text = T(block.Text);
        var holder = item as ContentControl;
        if (holder != null && holder.Content is string) holder.Content = T((string)holder.Content);
        foreach (var child in LogicalTreeHelper.GetChildren(item).OfType<DependencyObject>().ToList()) Translate(child);
    }
}

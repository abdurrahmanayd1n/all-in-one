// Pasting without formatting, and changing the case of the copied text, by keyboard shortcut.
// The clipboard keeps the plain result.
using System;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using System.Windows.Markup;
using Wpf = System.Windows;
using Controls = System.Windows.Controls;

enum TextForm { Plain, Upper, Lower, Title, Sentence }

static class Paster
{
    static readonly CultureInfo Turkish = new CultureInfo("tr-TR"); // i and İ, ı and I

    // The test replaces these three: the real clipboard and keyboard are left alone.
    public static Func<string> Read = () => Clipboard.ContainsText() ? Clipboard.GetText() : null;
    public static Action<string> Write = Ocr.ToClipboard; // plain text only, whatever was there before
    public static Action SendPaste = () =>
    {
        const byte Control = 0x11, V = 0x56;
        Native.keybd_event(Control, 0, 0, UIntPtr.Zero);
        Native.keybd_event(V, 0, 0, UIntPtr.Zero);
        Native.keybd_event(V, 0, 2, UIntPtr.Zero);
        Native.keybd_event(Control, 0, 2, UIntPtr.Zero);
    };

    public static string Convert(string text, TextForm form)
    {
        if (form == TextForm.Plain) return text;
        if (form == TextForm.Upper) return text.ToUpper(Turkish);
        string lower = text.ToLower(Turkish);
        if (form == TextForm.Lower) return lower;
        var result = new StringBuilder(lower);
        bool start = true;
        for (int i = 0; i < result.Length; i++)
        {
            char c = result[i];
            if (char.IsLetterOrDigit(c))
            {
                if (start) result[i] = char.ToUpper(c, Turkish);
                start = false;
            }
            // A word begins after a space or an opening mark, not after the apostrophe in "Ankara'nın".
            else if (form == TextForm.Title) start |= char.IsWhiteSpace(c) || "-([{\"“‘/".IndexOf(c) >= 0;
            // A sentence begins after a line break, or after . ! ? followed by a space ("3.5" stays whole).
            else if (c == '\n' || (char.IsWhiteSpace(c) && i > 0 && ".!?…".IndexOf(lower[i - 1]) >= 0)) start = true;
        }
        return result.ToString();
    }

    // Puts the converted text on the clipboard. Returns false, having said why, when there is no text.
    public static bool Prepare(TextForm form, out string problem)
    {
        problem = null;
        try
        {
            string text = Read();
            if (string.IsNullOrEmpty(text)) problem = "Panoda yazı yok.";
            else Write(Convert(text, form));
        }
        catch (Exception e)
        {
            Log.Error("panodaki yazı alınamadı", e);
            problem = "Pano kullanılamadı: " + e.Message;
        }
        return problem == null;
    }

    // Win+Shift+Y and the entries of the case list: converts, then pastes into the window in front.
    public static void Paste(TextForm form)
    {
        string problem;
        if (!Prepare(form, out problem))
        {
            Note.Show(problem, Cursor.Position);
            return;
        }
        WhenKeysAreUp(SendPaste);
    }

    // Win and Shift from the shortcut are still held: Ctrl+V sent now would arrive as another shortcut.
    static void WhenKeysAreUp(Action action)
    {
        var wait = new Timer { Interval = 20 };
        int waited = 0;
        wait.Tick += (s, e) =>
        {
            bool held = new[] { 0x5B, 0x5C, 0x10, 0x11, 0x12 }.Any(Native.IsDown);
            if (held && (waited += wait.Interval) < 3000) return;
            wait.Dispose();
            if (!held) action(); // given up: the text is on the clipboard for an ordinary Ctrl+V
        };
        wait.Start();
    }

    // Win+Shift+B: a small list of case forms beside the pointer.
    public static void Choose()
    {
        IntPtr target = Native.GetForegroundWindow();
        var list = new CaseList(form =>
        {
            Native.SetForegroundWindow(target); // back to where the text belongs
            Paste(form);
        });
        Assets.Show(list.Window);
        list.Window.Activate();
        Native.SetForegroundWindow(new System.Windows.Interop.WindowInteropHelper(list.Window).Handle);
    }
}

class CaseList
{
    const string Xaml = @"<Window xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
        xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' Title='All In One - harf çevir'
        Width='250' SizeToContent='Height' WindowStyle='None' AllowsTransparency='True' Background='Transparent'
        ResizeMode='NoResize' ShowInTaskbar='False' Topmost='True' WindowStartupLocation='Manual'
        Foreground='#F2F3F5' FontSize='13' FontFamily='Segoe UI Variable Text, Segoe UI'>
      <Window.Resources>
        <Style TargetType='Button'>
          <Setter Property='Foreground' Value='#F2F3F5'/>
          <Setter Property='FocusVisualStyle' Value='{x:Null}'/>
          <Setter Property='Template'>
            <Setter.Value>
              <ControlTemplate TargetType='Button'>
                <Border x:Name='face' Background='Transparent' CornerRadius='8' Padding='9,6' BorderThickness='1' BorderBrush='Transparent'>
                  <ContentPresenter/>
                </Border>
                <ControlTemplate.Triggers>
                  <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='face' Property='Background' Value='#22FFFFFF'/></Trigger>
                  <Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='face' Property='BorderBrush' Value='White'/></Trigger>
                </ControlTemplate.Triggers>
              </ControlTemplate>
            </Setter.Value>
          </Setter>
        </Style>
      </Window.Resources>
      <Border Background='#F51B1C23' CornerRadius='14' BorderBrush='#40FFFFFF' BorderThickness='1' Padding='8'>
        <StackPanel x:Name='Rows'>
          <TextBlock Text='Panodaki yazıyı çevirip yapıştır' Foreground='#A6A9B4' FontSize='12' Margin='9,4,9,6'/>
        </StackPanel>
      </Border>
    </Window>";

    public readonly Wpf.Window Window = (Wpf.Window)XamlReader.Parse(Xaml);
    bool closing;

    public CaseList(Action<TextForm> chosen)
    {
        var rows = (Controls.StackPanel)Window.FindName("Rows");
        var forms = new[] { Tuple.Create(TextForm.Upper, "BÜYÜK HARF"), Tuple.Create(TextForm.Lower, "küçük harf"),
            Tuple.Create(TextForm.Title, "Her Kelime Büyük"), Tuple.Create(TextForm.Sentence, "Cümle düzeni"),
            Tuple.Create(TextForm.Plain, "Olduğu gibi (düz yazı)") };
        Action<TextForm> pick = form =>
        {
            Dismiss();
            chosen(form);
        };
        for (int i = 0; i < forms.Length; i++)
        {
            var form = forms[i].Item1;
            var row = new Controls.Button { Content = (i + 1) + "    " + forms[i].Item2 };
            System.Windows.Automation.AutomationProperties.SetName(row, forms[i].Item2);
            row.Click += (s, e) => pick(form);
            rows.Children.Add(row);
        }
        Window.PreviewKeyDown += (s, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Escape) Dismiss();
            int number = e.Key >= System.Windows.Input.Key.D1 && e.Key <= System.Windows.Input.Key.D9
                ? e.Key - System.Windows.Input.Key.D1
                : e.Key >= System.Windows.Input.Key.NumPad1 && e.Key <= System.Windows.Input.Key.NumPad9
                    ? e.Key - System.Windows.Input.Key.NumPad1 : -1;
            if (number >= 0 && number < forms.Length)
            {
                e.Handled = true;
                pick(forms[number].Item1);
            }
        };
        var near = Cursor.Position;
        Window.Loaded += (s, e) =>
        {
            Picker.Beside(Window, near);
            ((Controls.Button)rows.Children[1]).Focus(); // the first form; the heading is the first child
        };
        Window.Closing += (s, e) => closing = true;
        Window.Deactivated += (s, e) => Dismiss();
    }

    public void Dismiss()
    {
        if (!closing) Window.Close();
    }
}

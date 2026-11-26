// The keyboard shortcuts: their usual keys, the user's own choice, and registering them with Windows.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;

// A system-wide shortcut. code: modifiers (Alt 1, Ctrl 2, Shift 4, Win 8) above the virtual key.
class Hotkey : NativeWindow, IDisposable
{
    const int Message = 0x312;
    const uint NoRepeat = 0x4000;
    readonly Action pressed;
    public readonly bool Works; // false when another program owns the shortcut

    public Hotkey(int code, Action pressed)
    {
        this.pressed = pressed;
        CreateHandle(new CreateParams());
        // AltGr arrives as Ctrl+Alt, so a shortcut with Alt alone leaves Turkish AltGr+Q typing @.
        Works = Native.RegisterHotKey(Handle, 1, (uint)(code >> 16) | NoRepeat, (uint)(code & 0xFFFF));
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Message) pressed();
        else base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (Works) Native.UnregisterHotKey(Handle, 1);
        DestroyHandle();
    }
}

class Shortcut
{
    public string Id, Label;
    public int Usual;
    public Action Run;
    public Hotkey Registered; // null: switched off or not registered now

    // The keys in force; 0: switched off.
    public int Code
    {
        get { return Prefs.Number("Key." + Id, Usual); }
        set { Prefs.SetNumber("Key." + Id, value); }
    }

    public bool Works { get { return Registered != null && Registered.Works; } }
    public string Keys { get { return Shortcuts.Name(Code); } }
}

static class Shortcuts
{
    public const int Alt = 1, Ctrl = 2, Shift = 4, Win = 8;

    static int Key(int modifiers, char key) { return modifiers << 16 | key; }

    public static readonly Shortcut Snap = new Shortcut { Id = "shot", Label = "Ekran görüntüsü / kaydı",
        Usual = Key(Alt, 'S'), Run = Snip.Toggle };
    public static readonly Shortcut ScreenWords = new Shortcut { Id = "text", Label = "Ekrandan metin",
        Usual = Key(Alt, 'Q'), Run = ScreenText.Start };
    public static readonly Shortcut ScreenWordsToo = new Shortcut { Id = "text2", Label = "Ekrandan metin (ikinci)",
        Usual = Key(Win | Shift, 'O'), Run = ScreenText.Start }; // for when another program owns Alt+Q
    public static readonly Shortcut ScreenCells = new Shortcut { Id = "table", Label = "Ekrandan tablo",
        Usual = Key(Alt, 'T'), Run = ScreenText.StartTable };
    public static readonly Shortcut Pin = new Shortcut { Id = "pin", Label = "Pencereyi üstte tut",
        Usual = Key(Win | Shift, 'U'), Run = Pinner.ToggleFront };
    public static readonly Shortcut PastePlain = new Shortcut { Id = "paste", Label = "Biçimsiz yapıştır",
        Usual = Key(Win | Shift, 'Y'), Run = () => Paster.Paste(TextForm.Plain) };
    public static readonly Shortcut PasteCase = new Shortcut { Id = "case", Label = "Harf çevirip yapıştır",
        Usual = Key(Win | Shift, 'B'), Run = Paster.Choose };

    public static readonly Shortcut[] All = { Snap, ScreenWords, ScreenWordsToo, ScreenCells, Pin, PastePlain, PasteCase };

    static bool running; // only the running app holds shortcuts: the self-test and the settings window alone do not

    // Registers every shortcut anew; returns those another program holds.
    public static Shortcut[] Apply()
    {
        running = true;
        Release();
        foreach (var each in All.Where(s => s.Code != 0)) each.Registered = new Hotkey(each.Code, each.Run);
        return All.Where(s => s.Code != 0 && !s.Works).ToArray();
    }

    // Lets go of all of them, so that the settings window can hear the keys being chosen.
    public static void Release()
    {
        foreach (var each in All)
        {
            if (each.Registered != null) each.Registered.Dispose();
            each.Registered = null;
        }
    }

    public static void Resume()
    {
        if (running) Apply();
    }

    public static void Stop()
    {
        Release();
        running = false;
    }

    // "Win+Shift+O"; "" when switched off.
    public static string Name(int code)
    {
        if (code == 0) return "";
        int modifiers = code >> 16;
        var parts = new List<string>();
        if ((modifiers & Win) != 0) parts.Add("Win");
        if ((modifiers & Ctrl) != 0) parts.Add("Ctrl");
        if ((modifiers & Alt) != 0) parts.Add("Alt");
        if ((modifiers & Shift) != 0) parts.Add("Shift");
        var key = (System.Windows.Forms.Keys)(code & 0xFFFF);
        int digit = (int)key - '0';
        parts.Add(digit >= 0 && digit <= 9 ? digit.ToString() : key == System.Windows.Forms.Keys.Snapshot ? "PrtSc" : key.ToString());
        return string.Join("+", parts);
    }

    // Why these keys cannot be this shortcut, or null.
    public static string Problem(Shortcut own, int code)
    {
        if (code == 0) return null;
        int modifiers = code >> 16, key = code & 0xFFFF;
        bool alone = key >= 0x70 && key <= 0x87 || key == 0x2C; // F1…F24 and Print Screen need no modifier
        if ((modifiers & (Alt | Ctrl | Win)) == 0 && !alone) return Lang.T("Ctrl, Alt veya Win ile birlikte bir tuş seç.");
        if ((modifiers & (Alt | Ctrl)) == (Alt | Ctrl)) return Lang.T("Ctrl+Alt, AltGr ile karışır; başka bir birleşim seç.");
        if (modifiers == Alt && (key == 0x20 || key == 0x09 || key == 0x73 || key == 0x1B))
            return Lang.T("Bu birleşim Windows'a veya dosya önizlemeye ayrılmış.");
        var other = All.FirstOrDefault(s => s != own && s.Code == code);
        return other == null ? null : Lang.F("Bu tuşlar \"{0}\" için kullanılıyor.", Lang.T(other.Label));
    }
}

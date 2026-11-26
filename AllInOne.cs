// All In One: select files in Explorer or on the desktop, then hold Left Alt for a radial menu.
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Timer = System.Windows.Forms.Timer;

static class Native
{
    public delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint thread);
    [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("kernel32.dll")] public static extern IntPtr GetModuleHandle(string name);
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr hwnd, int index, int value);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder s, int max);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] public static extern bool SHObjectProperties(IntPtr hwnd, uint type, string name, string page);
    [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr icon);

    public static bool IsDown(int key) { return (GetAsyncKeyState(key) & 0x8000) != 0; }
}

// Shell COM interfaces, declared only up to the method that is used: the order is the vtable order.
[ComImport, Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
interface IShellWindows
{
    void Count(); void Item(); void NewEnum(); void Register(); void RegisterPending(); void Revoke();
    void OnNavigate(); void OnActivated();
    [return: MarshalAs(UnmanagedType.IDispatch)]
    object FindWindowSW(ref object location, ref object root, int windowClass, out int hwnd, int options);
}

[ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IShellServices
{
    [return: MarshalAs(UnmanagedType.IUnknown)]
    object QueryService(ref Guid service, ref Guid iid);
}

[ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IShellBrowser
{
    IntPtr GetWindow();
}

// The files and folders selected in an Explorer window or on the desktop.
static class Target
{
    static string ClassOf(IntPtr window)
    {
        var cls = new System.Text.StringBuilder(64);
        Native.GetClassName(window, cls, cls.Capacity);
        return cls.ToString();
    }

    // Whether files can be selected in this window: an Explorer window or the desktop.
    public static bool HoldsFiles(IntPtr window)
    {
        string c = ClassOf(window);
        return c == "CabinetWClass" || c == "Progman" || c == "WorkerW";
    }

    // Virtual items are left out; empty means the menu stays closed.
    public static string[] Selected(IntPtr window)
    {
        try
        {
            string c = ClassOf(window);
            if (c == "Progman" || c == "WorkerW") return Files(Desktop().Document);
            if (c != "CabinetWClass") return new string[0];

            // Windows 11 tabs share one window; the first tab window in z-order is the visible one.
            IntPtr tab = Native.FindWindowEx(window, IntPtr.Zero, "ShellTabWindowClass", null);
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application"));
            foreach (dynamic view in shell.Windows())
            {
                if (view == null || (long)view.HWND != window.ToInt64()) continue;
                if (tab != IntPtr.Zero && TabOf((object)view) != tab) continue;
                return Files(view.Document);
            }
        }
        catch (Exception e)
        {
            Log.Error("seçim okunamadı (menü açılmadı)", e); // no selection is normal, an exception is not
        }
        return new string[0];
    }

    static dynamic Desktop()
    {
        var windows = (IShellWindows)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39")));
        object location = 0, none = null;
        int hwnd;
        return windows.FindWindowSW(ref location, ref none, 8 /* SWC_DESKTOP */, out hwnd, 1 /* NEEDDISPATCH */);
    }

    static IntPtr TabOf(object view)
    {
        Guid service = new Guid("4C96BE40-915C-11CF-99D3-00AA004AE837"), id = typeof(IShellBrowser).GUID;
        return ((IShellBrowser)((IShellServices)view).QueryService(ref service, ref id)).GetWindow();
    }

    static string[] Files(dynamic document)
    {
        var files = new System.Collections.Generic.List<string>();
        foreach (dynamic item in document.SelectedItems())
        {
            string path = item.Path;
            if (File.Exists(path) || Directory.Exists(path)) files.Add(path);
        }
        return files.ToArray();
    }

    // The first Shell calls take seconds, so make them before the user asks.
    public static void WarmUp()
    {
        try
        {
            Files(Desktop().Document);
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application"));
            foreach (dynamic view in shell.Windows())
                if (view != null) Files(view.Document);
        }
        catch (Exception e)
        {
            Debug.WriteLine(e); // only costs speed: the first real open retries all of it
        }
    }
}

// Builds the menu for the selected files. Add new tools here.
static class Tools
{
    public static Item[] For(string[] files)
    {
        string single = files.Length == 1 ? files[0] : null;
        var copy = new Item { Label = Lang.T("Yolu\nKopyala"), Hint = Lang.T("Panoya"), Icon = "\uE8C8",
            Run = () => Clipboard.SetText(string.Join(Environment.NewLine, files)) };
        var terminal = new Item { Label = Lang.T("Terminal\nAç"), Icon = "\uE756", Run = () => OpenTerminal(files[0]) };
        var properties = new Item { Label = Lang.T("Özellikler"), Icon = "\uE946",
            Run = () => Native.SHObjectProperties(IntPtr.Zero, 2 /* SHOP_FILEPATH */, single, null) };

        var rename = new Item { Label = Lang.T("Yeniden\nAdlandır"), Icon = "\uE8AC", Run = () => RenameWindow.Open(files) };
        var settings = new Item { Label = Lang.T("Ayarlar"), Run = Preferences.Open };
        var users = new Item { Label = Lang.T("Kim\nKullanıyor?"), Icon = "\uE72E", Run = () => UsersWindow.Open(files) };
        var sum = new Item { Label = Lang.T("Doğrulama\nKodu"), Hint = "SHA-256", Icon = "\uE9D5",
            Run = () => ChecksumWindow.Open(files) };
        var screen = new Item { Label = Lang.T("Ekrandan\nMetin"), Hint = Shortcuts.ScreenWords.Keys, Run = ScreenText.Start };
        // The outer ring of "Diğer": the rarer tools for pictures and videos. Its options are text only.
        Func<Item> more = () => new Item { Label = Lang.T("Diğer"), Hint = Lang.T("Dışa kaydır"), Icon = "\uE712",
            Children = (single == null ? new[] { copy, terminal, rename, users, sum, screen, settings }
                : new[] { copy, terminal, rename, users, sum, properties, screen, settings }).Select(TextOnly).ToArray() };

        if (files.Any(Directory.Exists))
        {
            var zip = new Item { Label = Lang.T("ZIP\nYap"), Hint = Lang.T("Ayarları aç"), Icon = "\uE7B8",
                Children = new[] { new Item { Label = Lang.T("Hemen"), Hint = Lang.T("ZIP yap"), Run = () => ZipWindow.Open(files, true) } },
                Run = () => ZipWindow.Open(files, false) };
            if (!files.All(Directory.Exists)) return new[] { copy, terminal, zip }; // files and folders together
            var twins = new Item { Label = Lang.T("Kopya\nBul"), Hint = Lang.T("Aynı içerikli dosyalar"), Icon = "\uE721",
                Run = () => DuplicatesWindow.Open(files) };
            var sort = new Item { Label = Lang.T("Klasörü\nDüzenle"), Hint = Lang.T("Alt klasörlere ayır"), Icon = "\uE8CB",
                Run = () => OrganizeWindow.Open(single) };
            var folderTools = new[]
            {
                new Item { Label = Lang.T("Klasör\nBoyutu"), Hint = Lang.T("Neler yer kaplıyor"), Icon = "\uE9D9", Run = () => SizeWindow.Open(files) },
                zip,
                new Item { Label = Lang.T("İçini\nAdlandır"), Hint = Lang.T("Dosyaları topluca"), Icon = "\uE8AC",
                    Run = () => Folders.RenameContents(files) },
                sort,
                twins,
                new Item { Label = Lang.T("Diğer"), Hint = Lang.T("Dışa kaydır"), Icon = "\uE712",
                    Children = (single == null ? new[] { copy, terminal, screen, settings }
                        : new[] { copy, terminal, properties, screen, settings }).Select(TextOnly).ToArray() }
            };
            // Sorting works on one folder at a time.
            return single != null ? folderTools : folderTools.Where(item => item != sort).ToArray();
        }
        if (Video.Available && files.All(Video.IsVideo)) return ForVideos(files, more());
        if (Pdf.Available && files.All(Pdf.IsPdf)) return ForPdfs(files, new[] { copy, terminal, rename, users, sum, properties });
        if (!files.All(Images.IsImage))
        {
            if (single == null) return new[] { copy, terminal, rename, users, sum };
            var notepad = new Item { Label = Lang.T("Not\nDefteri"), Icon = "\uE70F",
                Run = () => Process.Start(Path.Combine(Environment.SystemDirectory, "notepad.exe"), "\"" + single + "\"") };
            return new[] { copy, terminal, notepad, rename, users, sum, properties };
        }

        var own = files.Select(Family).Distinct().ToArray();
        var formats = new[] { ".jpg", ".png", ".webp", ".bmp", ".tif", ".gif" }
            .Where(e => !(own.Length == 1 && own[0] == e) && (e != ".webp" || Images.Ffmpeg != null)).Select(e => new Item
        {
            Label = e == ".tif" ? "TIFF" : e.Substring(1).ToUpperInvariant(),
            Hint = Lang.T("Yeni dosya olarak"),
            Run = () =>
            {
                var confirm = Images.Once(Images.AskWhiteBackground);
                // In a mixed selection, files that already have the format are left alone.
                Images.Start(files, f => Family(f) == e ? new Outcome { Text = Lang.T("Zaten bu biçimde.") } : Images.ConvertTo(f, e, confirm));
            }
        }).ToArray();
        var sizes = new[] { 25, 50, 75, 200 }.Select(n => new Item
        {
            Label = Lang.Percent(n),
            Hint = Lang.T("Yeni boyut"),
            Run = () => Images.Start(files, f => Images.Scale(f, n))
        }).ToArray();
        Func<string, int, Item> level = (label, quality) => new Item
        {
            Label = label,
            Hint = Lang.T("Kalite ") + quality,
            Run = () => Images.Start(files, f => Images.Compress(f, quality))
        };

        return new[]
        {
            new Item { Label = Lang.T("Format\nDeğiştir"), Hint = Lang.T("Ayarları aç"), Icon = "\uE8AB", Children = formats,
                Run = () => Editor.Open(files, Mode.Format) },
            new Item { Label = Lang.T("Ölçeklendir"), Hint = Lang.T("Ayarları aç"), Icon = "\uE740", Children = sizes,
                Run = () => Editor.Open(files, Mode.Scale) },
            new Item { Label = Lang.T("Kırp"), Hint = Lang.T("Ayarları aç"), Icon = "\uE7A8", Run = () => Editor.Open(files, Mode.Crop) },
            new Item { Label = Lang.T("Sıkıştır"), Hint = Lang.T("Ayarları aç"), Icon = "\uE73F",
                Children = new[]
                {
                    level(Lang.T("Hafif"), 85), level(Lang.T("Orta"), 70), level(Lang.T("Güçlü"), 50),
                    new Item { Label = Lang.T("Akıllı"), Hint = Lang.T("Gözle aynı"), Run = () => Images.Start(files, Images.CompressSmart) },
                    new Item { Label = Lang.T("500 KB"), Hint = Lang.T("Altına indir"), Run = () => Images.Start(files, f => Images.CompressUnder(f, 500)) }
                },
                Run = () => Editor.Open(files, Mode.Compress) },
            new Item { Label = Lang.T("Metni\nAl"), Hint = Lang.T("Pencereyi aç"), Icon = "\uE8D2",
                Children = new[] { new Item { Label = Lang.T("Kopyala"), Hint = Lang.T("Metni panoya"), Run = () => Images.Start(() => Ocr.Copy(files)) } },
                Run = () => Reader.Open(files) },
            new Item { Label = Lang.T("Hazır\nAyar"), Hint = Lang.T("Ayarları aç"), Icon = "\uE945",
                Children = Presets.All().Select(preset => new Item { Label = preset.Name, Hint = Lang.T("Tek tıkla"),
                    Run = () => Images.Start(files, f => Presets.Apply(preset, f)) }).ToArray(),
                Run = () => Editor.Open(files, Mode.Preset) },
            more()
        };
    }

    static Item[] ForVideos(string[] files, Item more)
    {
        Func<string, string, VideoJob, Item> quick = (label, hint, job) =>
            new Item { Label = label, Hint = hint, Run = () => VideoWindow.Open(files, job, true) };
        Func<int, int, VideoJob> shrink = (crf, side) => new VideoJob { Task = VideoTask.Compress, Crf = crf, ShortSide = side };
        Func<int, VideoJob> gif = seconds => new VideoJob { Task = VideoTask.Gif, Length = seconds };
        Func<string, VideoJob> sound = ext => new VideoJob { Task = VideoTask.Audio, AudioExt = ext };
        // One video is cut; several are joined.
        var edit = files.Length == 1
            ? new Item { Label = Lang.T("Kes"), Hint = Lang.T("Bir bölümünü al"), Icon = "\uE8C6",
                Run = () => VideoWindow.Open(files, new VideoJob { Task = VideoTask.Trim, Length = 0 }, false) }
            : new Item { Label = Lang.T("Birleştir"), Hint = Lang.T("Tek video yap"), Icon = "\uE71B",
                Run = () => VideoWindow.Open(files, new VideoJob { Task = VideoTask.Merge }, false) };

        return new[]
        {
            new Item { Label = Lang.T("Sıkıştır"), Hint = Lang.T("Ayarları aç"), Icon = "\uE73F",
                Children = new[] { quick(Lang.T("Yüksek"), Lang.T("Kalite öncelikli"), shrink(23, 0)), quick(Lang.T("Orta"), Lang.T("Dengeli"), shrink(28, 0)),
                    quick(Lang.T("Küçük"), Lang.T("Boyut öncelikli"), shrink(33, 0)), quick("720p", Lang.T("Küçült"), shrink(28, 720)) },
                Run = () => VideoWindow.Open(files, shrink(28, 0), false) },
            edit,
            new Item { Label = Lang.T("GIF\nYap"), Hint = Lang.T("Ayarları aç"), Icon = "\uE8B9",
                Children = new[] { quick(Lang.T("İlk 5 sn"), "GIF", gif(5)), quick(Lang.T("İlk 10 sn"), "GIF", gif(10)) },
                Run = () => VideoWindow.Open(files, gif(5), false) },
            new Item { Label = Lang.T("Sesi\nAyır"), Hint = Lang.T("Ayarları aç"), Icon = "\uE8D6",
                Children = new[] { quick("MP3", Lang.T("Sesi kaydet"), sound(".mp3")), quick("M4A", Lang.T("Sesi kaydet"), sound(".m4a")),
                    quick("WAV", Lang.T("Sesi kaydet"), sound(".wav")) },
                Run = () => VideoWindow.Open(files, sound(".mp3"), false) },
            more
        };
    }

    static Item[] ForPdfs(string[] files, Item[] rest)
    {
        var own = files.Length == 1
            ? new Item { Label = Lang.T("Sayfaları\nAyır"), Hint = Lang.T("Ayarları aç"), Icon = "\uE8C6",
                Children = new[] { new Item { Label = Lang.T("Tek tek"), Hint = Lang.T("Her sayfa ayrı"),
                    Run = () => Images.Start(() => Pdf.SplitEach(files[0])) } },
                Run = () => PdfWindow.Open(files) }
            : new Item { Label = Lang.T("PDF\nBirleştir"), Hint = Lang.T("Sırayı seç"), Icon = "\uE71B",
                Run = () => PdfWindow.Open(files) };
        // The last of the rest, the properties, is for a single file.
        return new[] { own }.Concat(files.Length == 1 ? rest : rest.Take(rest.Length - 1)).ToArray();
    }

    // The extension with its aliases folded together.
    static string Family(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext == ".jpeg" || ext == ".jfif" ? ".jpg" : ext == ".tiff" ? ".tif" : ext;
    }

    // Centre of the menu: what is selected.
    public static string Title(string[] files)
    {
        // A drive such as C:\ has no name of its own.
        if (files.Length == 1) return Path.GetFileName(files[0]) == "" ? files[0] : Path.GetFileName(files[0]);
        return files.Length + (files.All(Images.IsImage) ? Lang.T(" görsel") : files.All(Video.IsVideo) ? Lang.T(" video")
            : files.All(Directory.Exists) ? Lang.T(" klasör") : files.Any(Directory.Exists) ? Lang.T(" öğe") : Lang.T(" dosya"));
    }

    public static string Describe(string[] files)
    {
        // The size of a folder takes a scan, which the menu must not wait for.
        if (files.Any(Directory.Exists)) return files.All(Directory.Exists) ? Lang.T("Klasör") : Lang.T("Dosya ve klasör");
        long total = files.Select(f => new FileInfo(f)).Where(f => f.Exists).Sum(f => f.Length);
        if (files.Length > 1) return Lang.T("Toplam ") + Images.Size(total);
        string type = Path.GetExtension(files[0]).TrimStart('.').ToUpperInvariant();
        return (type == "" ? "" : type + " · ") + Images.Size(total);
    }

    static Item TextOnly(Item item)
    {
        item.Icon = null;
        return item;
    }

    static void OpenTerminal(string path)
    {
        // Full exe path: an exe planted in the target folder must not be picked up.
        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"));
        info.WorkingDirectory = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
        Process.Start(info);
    }
}

class App : ApplicationContext
{
    enum State { Idle, Pending, Opening, Open, Cancelled }

    const int LeftAlt = 0xA4, Escape = 0x1B, Space = 0x20;
    const byte MaskKey = 0xE8;
    static readonly IntPtr Replayed = (IntPtr)0xA110; // marks keys this app sends again itself

    // The thread that owns the windows; set while the app runs.
    public static System.Windows.Threading.Dispatcher Ui;

    State state = State.Idle;
    bool spaceTaken; // its key-up belongs to us as well
    int generation;
    readonly Stopwatch held = new Stopwatch();
    readonly Timer timer = new Timer { Interval = 15 };
    readonly RadialMenu menu = new RadialMenu();
    readonly NotifyIcon tray = new NotifyIcon();
    readonly Native.HookProc proc; // field keeps the delegate alive while the hook is installed
    readonly IntPtr hook;

    public App()
    {
        timer.Tick += (s, e) => Tick();
        proc = OnKey;
        hook = Native.SetWindowsHookEx(13 /* WH_KEYBOARD_LL */, proc, Native.GetModuleHandle(null), 0);
        if (hook == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();

        using (var icon = Assets.Open("app.ico")) tray.Icon = new Icon(icon, SystemInformation.SmallIconSize);
        tray.Text = Lang.T("All In One (dosya seç, Sol Alt basılı tut)");
        Ui = menu.Dispatcher;
        foreach (var key in Shortcuts.Apply())
            Log.Error("Bir kısayol başka bir programda kayıtlı olduğu için alınamadı: " + key.Keys);
        var pins = new MenuItem(Lang.T("Sabitlenen pencereler"));
        var snap = new MenuItem("", (s, e) => Snip.Toggle());
        var words = new MenuItem("", (s, e) => ScreenText.Start());
        var cells = new MenuItem("", (s, e) => ScreenText.StartTable());
        var paste = new MenuItem("") { Enabled = false };
        var picker = new MenuItem(Lang.T("Renk seçici simgesi"), (s, e) =>
        {
            if (Picker.Shown) Picker.Close();
            else Picker.Reopen();
        });
        tray.ContextMenu = new ContextMenu(new[]
        {
            new MenuItem(Lang.T("Renk seç"), (s, e) => Picker.Pick()),
            picker,
            snap,
            words,
            cells,
            pins,
            paste,
            new MenuItem(Lang.T("Hata kaydını aç"), (s, e) => Log.Open()),
            new MenuItem(Lang.T("Ayarlar"), (s, e) => Preferences.Open()) { DefaultItem = true },
            new MenuItem(Lang.T("Çıkış"), (s, e) => ExitThread())
        });
        // The shortcuts can be changed while the app runs.
        Action named = () =>
        {
            Func<string, Shortcut, string> with = (label, key) => label + (key.Works ? "\t" + key.Keys : "");
            snap.Text = Recorder.Running ? with(Lang.T("Kaydı durdur"), Shortcuts.Snap) : with(Lang.T("Ekran görüntüsü / kaydı"), Shortcuts.Snap);
            words.Text = with(Lang.T("Ekrandan metin al"), Shortcuts.ScreenWords.Works ? Shortcuts.ScreenWords : Shortcuts.ScreenWordsToo);
            cells.Text = with(Lang.T("Ekrandan tablo al"), Shortcuts.ScreenCells);
            Func<Shortcut, string> keys = key => key.Works ? key.Keys : key.Code == 0 ? Lang.T("kapalı") : Lang.T("kısayol kullanımda");
            paste.Text = Lang.T("Düz yapıştır: ") + keys(Shortcuts.PastePlain) + Lang.T("   ·   Harf çevir: ") + keys(Shortcuts.PasteCase);
        };
        named();
        tray.ContextMenu.Popup += (s, e) =>
        {
            picker.Checked = Picker.Shown;
            named();
            ListPins(pins);
        };
        ListPins(pins); // a submenu needs entries before it can open
        tray.DoubleClick += (s, e) => Preferences.Open();
        tray.Visible = true;
        Picker.Refresh();
        Images.Notify = outcome => menu.Dispatcher.BeginInvoke((Action)(() => Toast.Show(outcome)));
        // A click on the menu chooses like releasing Alt does; the Alt that is still held then does nothing more.
        menu.Clicked += () =>
        {
            var action = state == State.Open ? menu.Choose() : null;
            if (action == null) return;
            menu.Dismiss();
            state = State.Cancelled;
            Run(action);
        };
        Uninstall.Quit = ExitThread;
        string saved = null; // the file the last icon notification is about
        Toast.Tray = outcome =>
        {
            saved = outcome.File;
            tray.ShowBalloonTip(5000, saved != null ? Lang.T("Kaydedildi") : "All In One", Toast.TrayText(outcome), ToolTipIcon.None);
        };
        tray.BalloonTipClicked += (s, e) => { if (saved != null) Toast.Reveal(saved); };
        menu.WarmUp();
        Images.Background(Target.WarmUp);
    }

    protected override void ExitThreadCore()
    {
        Native.UnhookWindowsHookEx(hook);
        Pinner.ReleaseAll(); // nothing stays stuck on top after the app has gone
        Recorder.Stop(true); // a running recording is finished, not lost
        Shortcuts.Stop();
        Picker.Remove();
        tray.Dispose();
        base.ExitThreadCore();
    }

    // The tray submenu: a click on a pinned window releases it.
    static void ListPins(MenuItem pins)
    {
        pins.MenuItems.Clear();
        var all = Pinner.Pinned();
        foreach (var each in all)
        {
            var window = each;
            pins.MenuItems.Add(new MenuItem(Pinner.Title(window).Replace("&", "&&"), (s, e) => Pinner.Toggle(window)));
        }
        if (all.Length == 0)
            pins.MenuItems.Add(new MenuItem(Shortcuts.Pin.Works ? Lang.T("Pencereyi sabitlemek için ") + Shortcuts.Pin.Keys
                : Shortcuts.Pin.Code == 0 ? Lang.T("Sabitleme kısayolu kapalı")
                : Shortcuts.Pin.Keys + Lang.T(" başka bir programda kullanılıyor")) { Enabled = false });
        else
        {
            pins.MenuItems.Add("-");
            pins.MenuItems.Add(new MenuItem(Lang.T("Tümünü bırak"), (s, e) => Pinner.ReleaseAll()));
        }
    }

    // Must return fast: Windows drops slow low-level hooks.
    IntPtr OnKey(int code, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (code >= 0 && Marshal.ReadIntPtr(lParam, 16) != Replayed && Handle(Marshal.ReadInt32(lParam), wParam.ToInt32()))
                return (IntPtr)1;
        }
        catch (Exception e) { Log.Unexpected("klavye", e); } // the key must still reach the other programs
        return Native.CallNextHookEx(hook, code, wParam, lParam);
    }

    // True when the key is swallowed.
    bool Handle(int key, int message)
    {
        bool down = message == 0x100 || message == 0x104;
        if (key == LeftAlt)
        {
            if (!down) Release();
            else if (state == State.Idle && !Native.IsDown(0x11) && !Native.IsDown(0x10) && !Native.IsDown(0x5B) && !Native.IsDown(0x5C))
            {
                state = State.Pending;
                held.Restart();
                timer.Start();
            }
        }
        else if (key == Space && !down && spaceTaken)
        {
            spaceTaken = false;
            return true;
        }
        else if (key == Space && down && spaceTaken) return true; // auto-repeat
        else if (key == Space && down && (state == State.Pending || state == State.Opening || state == State.Open)
            && Prefs.Preview && Target.HoldsFiles(Native.GetForegroundWindow()))
        {
            Cancel();
            spaceTaken = true;
            BeginPreview(Native.GetForegroundWindow());
            return true;
        }
        else if (key != MaskKey && down && state != State.Idle)
        {
            bool swallow = key == Escape && state == State.Open;
            Cancel(); // Alt+Tab, Alt+F4 and friends keep working
            return swallow;
        }
        return false;
    }

    void Tick()
    {
        if (!Native.IsDown(LeftAlt)) // key-up was lost (lock screen, UAC)
        {
            Cancel();
            state = State.Idle;
            timer.Stop();
        }
        else if (state == State.Pending)
        {
            // Alt+click / Alt+drag belongs to the app under the cursor.
            if (Native.IsDown(1) || Native.IsDown(2) || Native.IsDown(4)) Cancel();
            else if (held.ElapsedMilliseconds >= Prefs.Hold) BeginOpen();
        }
        else if (state == State.Open) menu.Track();
    }

    void BeginOpen()
    {
        state = State.Opening;
        int mine = ++generation;
        var point = Cursor.Position;
        IntPtr window = Native.GetForegroundWindow();
        // Shell objects need a single-threaded apartment: from the thread pool the tab query fails.
        Images.Background(() =>
        {
            string[] files = Target.Selected(window);
            menu.Dispatcher.BeginInvoke((Action)(() =>
            {
                if (state != State.Opening || mine != generation) return;
                if (files.Length == 0) // nothing selected: Alt stays an ordinary Alt
                {
                    state = State.Cancelled;
                    return;
                }
                // An unused key between Alt down and up stops the focused app from activating its menu bar.
                Tap(MaskKey);
                menu.Open(point, Tools.For(files), Tools.Title(files), Tools.Describe(files));
                state = State.Open;
            }));
        });
    }

    // Alt+Space on selected files shows them; with nothing selected the keys go on to whoever else wants them.
    void BeginPreview(IntPtr window)
    {
        Images.Background(() =>
        {
            string[] files = Target.Selected(window);
            menu.Dispatcher.BeginInvoke((Action)(() =>
            {
                if (files.Length > 0)
                {
                    Run(() => Preview.Open(files));
                    return;
                }
                if (!Native.IsDown(LeftAlt)) return;
                Native.keybd_event(Space, 0, 0, (UIntPtr)(ulong)Replayed.ToInt64());
                Native.keybd_event(Space, 0, 2, (UIntPtr)(ulong)Replayed.ToInt64());
            }));
        });
        // Keeps the Alt that is still held from opening the window's menu bar when it is let go.
        menu.Dispatcher.BeginInvoke((Action)(() => Tap(MaskKey)));
    }

    static void Tap(byte key)
    {
        Native.keybd_event(key, 0, 0, UIntPtr.Zero);
        Native.keybd_event(key, 0, 2, UIntPtr.Zero);
    }

    void Cancel()
    {
        menu.Dismiss();
        state = State.Cancelled;
    }

    void Release()
    {
        var action = state == State.Open ? menu.Choose() : null;
        menu.Dismiss();
        timer.Stop();
        state = State.Idle;
        if (action != null) Run(action);
    }

    void Run(Action action)
    {
        menu.Dispatcher.BeginInvoke((Action)(() =>
        {
            Tap(MaskKey); // producing the latest input lets the opened window come to the front
            try { action(); }
            catch (Exception e)
            {
                Log.Error("menü seçimi", e);
                MessageBox.Show(e.Message, "All In One");
            }
        }));
    }
}

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        Native.SetProcessDPIAware();
        Application.EnableVisualStyles();
        // Also kept in Environment.ExitCode, which is what counts if the process is ended early (see Pdf.cs).
        if (args.Length > 0 && args[0] == "--test") return Environment.ExitCode = SelfTest();

        bool first;
        using (new Mutex(true, @"Local\AllInOne", out first))
        {
            if (first)
            {
                Log.Watch();
                Log.Info("Uygulama başladı.");
                try { Toast.Register(Toast.Identity, Log.Folder); }
                catch (Exception e) { Log.Error("bildirim kimliği yazılamadı", e); } // everything else still works
                Application.Run(new App());
                Log.Info("Uygulama kapatıldı.");
            }
        }
        return 0;
    }

    static int SelfTest()
    {
        var counts = new[] { 4, 4, 0, 3, 3 };
        int c;
        bool menu = RadialMenu.Hit(0, 0, counts, -1, out c) == -1
            && RadialMenu.Hit(0, -100, counts, -1, out c) == 0 && c == -1
            && RadialMenu.Hit(95, -31, counts, -1, out c) == 1
            && RadialMenu.Hit(-95, -31, counts, -1, out c) == 4
            && RadialMenu.Hit(-139, -125, counts, 0, out c) == 0 && c == 0   // first option of the open ring
            && RadialMenu.Hit(139, -125, counts, 0, out c) == 0 && c == 3    // last option
            && RadialMenu.Hit(100, -80, counts, 0, out c) == 0 && c == -1    // heading there across a neighbour
            && RadialMenu.Hit(95, -31, counts, 0, out c) == 1;               // leaving the ring switches item
        if (!menu) return 1;

        string dir = Path.Combine(Path.GetTempPath(), "AllInOne-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string realLog = Log.Folder;
        Log.Folder = Path.Combine(dir, "kayit"); // failures provoked here must not fill the real log
        // Nor may the user's settings steer the results: a throwaway key stands in for them.
        string realStore = Prefs.Store, testStore = @"Software\AllInOne-test-" + Guid.NewGuid().ToString("N");
        Prefs.Store = testStore;
        Lang.English = false; // the expected texts are the Turkish ones, whatever Windows speaks
        try
        {
            string source = Path.Combine(dir, "a.png");
            using (var image = new Bitmap(400, 200))
            {
                var noise = new Random(1);
                for (int y = 0; y < 200; y++)
                    for (int x = 0; x < 400; x++)
                        image.SetPixel(x, y, Color.FromArgb(255, noise.Next(256), noise.Next(256), noise.Next(256)));
                image.Save(source, System.Drawing.Imaging.ImageFormat.Png);
            }
            long length = new FileInfo(source).Length;
            Func<bool> never = () => false;
            // Results are checked through the returned path, so a stored fixed suffix does not matter.
            Func<Outcome, string, int, int, bool> made = (outcome, ext, w, h) =>
            {
                if (outcome == null || outcome.File == null || Path.GetExtension(outcome.File) != ext) return false;
                var p = Images.Load(outcome.File).Pixels;
                return p.PixelWidth == w && p.PixelHeight == h;
            };
            bool ok = true;
            foreach (var ext in new[] { ".jpg", ".bmp", ".tif", ".gif" }) ok &= made(Images.ConvertTo(source, ext, never), ext, 400, 200);
            var first = Images.Scale(source, 50);
            var second = Images.Scale(source, 50);
            ok &= made(first, ".png", 200, 100) && made(second, ".png", 200, 100) && first.File != second.File;
            ok &= made(Images.Compress(source, 70), ".jpg", 400, 200);

            var picture = Images.Load(source);
            var area = Images.ToPixels(new System.Windows.Point(300, 80), new System.Windows.Point(10, -20), 0.5, 400, 200);
            var named = Images.Export(source, picture, Images.Cut(picture.Pixels, area), ".png", 92, "kesit", false, never);
            ok &= made(named, ".png", 380, 160) && Path.GetFileName(named.File) == "kesit.png";
            // The original's own name never overwrites it.
            var same = Images.Export(source, picture, Images.Resize(picture.Pixels, 40, 20), ".png", 92, "a", false, never);
            ok &= made(same, ".png", 40, 20) && Path.GetFileName(same.File) == "a (2).png" && new FileInfo(source).Length == length;

            ok &= Naming.Compose(source, "_50", false, "_web") == "a_50" && Naming.Compose(source, "_50", true, "_web") == "a_web"
                && Naming.Problem("rapor (1)") == null && Naming.Problem(@"..\x") != null && Naming.Problem("CON") != null
                && Naming.Problem("com1.yedek") != null && Naming.Problem("x.") != null && Naming.Problem("  ") != null;
            var square = Images.Centered(1, 400, 200);
            var dragged = Images.Dragged(new System.Windows.Point(100, 50), new System.Windows.Point(900, 60), 2, 400, 200);
            ok &= square.X == 100 && square.Width == 200 && square.Height == 200 && dragged.Width == 300 && dragged.Height == 150;

            // Smart quality and the size target.
            ok &= Images.Lowest(30, 95, q => q >= 61) == 61 && Images.Lowest(30, 95, q => false) == 95
                && Images.Highest(40, 95, q => q <= 72) == 72 && Images.Highest(40, 95, q => false) == 39;
            var pixels = Images.Load(source).Pixels;
            var luma = Images.Luma(pixels);
            var rough = Images.Luma(Images.Decode(Images.Encode(pixels, null, ".jpg", 20), ".jpg"));
            ok &= Images.Similarity(luma, luma, 400, 200) > 0.9999 && Images.Similarity(luma, rough, 400, 200) < Images.LooksSame(".jpg");
            var smart = Images.Smart(pixels, null, ".jpg");
            ok &= smart.Quality >= Images.SmartFloor && smart.Quality <= Images.SmartCeiling && (smart.Quality == Images.SmartCeiling
                || Images.Similarity(luma, Images.Luma(Images.Decode(smart.Data, ".jpg")), 400, 200) >= Images.LooksSame(".jpg"));
            var quality = Images.UnderSize(pixels, null, ".jpg", 70 * 1024);
            var shrunk = Images.UnderSize(pixels, null, ".jpg", 6 * 1024);
            ok &= quality.Data.Length <= 70 * 1024 && shrunk.Data.Length <= 6 * 1024 && shrunk.Resized && shrunk.Width < 400;
            ok &= Images.CompressUnder(source, 100000).File == null && Images.CompressUnder(source, 20).File != null;

            // Building the windows fails here if their XAML is broken.
            foreach (Mode mode in Enum.GetValues(typeof(Mode)))
            {
                new Editor(new[] { source }, mode).Window.Close();
                new Editor(new[] { source, source }, mode).Window.Close();
            }

            new Reader(new[] { source }).Window.Close();
            new Reader(new[] { source, source }).Window.Close();
            new Preferences().Window.Close();

            // Settings, written to a throwaway key.
            string test = @"Software\AllInOne-test-" + Guid.NewGuid().ToString("N"), store = Prefs.Store,
                run = Prefs.RunList, approved = Prefs.Approved;
            Prefs.Store = test;
            Prefs.RunList = test + @"\Run";
            Prefs.Approved = test + @"\Approved";
            Prefs.Forget();
            try
            {
                ok &= Prefs.Hold == Prefs.UsualHold && !Prefs.Autostart;
                Prefs.Hold = 20;
                Prefs.Forget();
                ok &= Prefs.Hold == Prefs.ShortestHold;
                Prefs.Autostart = true;
                ok &= Prefs.Autostart;
                using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(Prefs.Approved))
                    key.SetValue("AllInOne", new byte[] { 3, 0, 0, 0 });
                ok &= !Prefs.Autostart; // switched off in Task Manager
                Prefs.Autostart = true;
                ok &= Prefs.Autostart;
                Prefs.Autostart = false;
                ok &= !Prefs.Autostart;
            }
            finally
            {
                Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(test, false);
                Prefs.Store = store;
                Prefs.RunList = run;
                Prefs.Approved = approved;
                Prefs.Forget();
            }

            ok &= Ocr.Combined(new[] { "a.png" }, new[] { "x" }) == "x"
                && Ocr.Combined(new[] { "a.png", "b.png" }, new[] { "", "y" }) == "— b.png —" + Environment.NewLine + "y";

            // The error log: entries are appended, a full file is set aside, and an escaping thread error is only logged.
            Log.Info("bir");
            Log.Error("iki", new InvalidOperationException("uc"));
            string written = File.ReadAllText(Log.File);
            ok &= written.Contains("BİLGİ  bir") && written.Contains("HATA   iki") && written.Contains("uc");
            File.WriteAllText(Log.File, new string('x', (int)Log.Largest + 1));
            Log.Info("yeni");
            ok &= File.Exists(Log.Older) && new FileInfo(Log.File).Length < 200;
            using (var finished = new ManualResetEvent(false))
            {
                Images.Background(() =>
                {
                    try { throw new InvalidOperationException("kacan"); }
                    finally { finished.Set(); }
                });
                finished.WaitOne(5000);
                for (int i = 0; i < 100 && !File.ReadAllText(Log.File).Contains("kacan"); i++) Thread.Sleep(20);
                ok &= File.ReadAllText(Log.File).Contains("kacan");
            }

            // Text forms, with Turkish dotted and dotless i.
            ok &= Paster.Convert("ilık Süt", TextForm.Upper) == "İLIK SÜT" && Paster.Convert("IŞIK İZİ", TextForm.Lower) == "ışık izi"
                && Paster.Convert("ANKARA'NIN (iç) yolu", TextForm.Title) == "Ankara'nın (İç) Yolu"
                && Paster.Convert("BİR. iki 3.5 KM! üç\ndört", TextForm.Sentence) == "Bir. İki 3.5 km! Üç\nDört"
                && Paster.Convert("Aynen", TextForm.Plain) == "Aynen";

            // Folders: sizes, the files inside, one archive of a selection, and the menus.
            string tree = Path.Combine(dir, "agac");
            Directory.CreateDirectory(Path.Combine(tree, "alt"));
            Directory.CreateDirectory(Path.Combine(tree, "bos"));
            File.WriteAllText(Path.Combine(tree, "a.txt"), new string('a', 100));
            File.WriteAllText(Path.Combine(tree, "alt", "ç.txt"), new string('b', 50));
            File.WriteAllText(Path.Combine(tree, "gizli.ini"), "g");
            File.SetAttributes(Path.Combine(tree, "gizli.ini"), FileAttributes.Hidden);
            var scanned = Folders.Scan(tree, () => { }, () => false);
            ok &= scanned.Bytes == 151 && scanned.OwnBytes == 101 && scanned.Files == 3 && scanned.Folders == 2
                && Folders.Contents(new[] { tree }).Single().EndsWith("a.txt");
            var missed = new System.Collections.Generic.List<string>();
            var packed2 = Folders.Zip(new[] { tree }, Folders.ArchiveName(new[] { tree }),
                System.IO.Compression.CompressionLevel.Optimal, p => { }, () => false, missed);
            using (var archive = new System.IO.Compression.ZipArchive(File.OpenRead(packed2.File)))
                ok &= missed.Count == 0 && Path.GetFileName(packed2.File) == "agac.zip" && string.Join(",", archive.Entries
                    .Select(e => e.FullName).OrderBy(n => n, StringComparer.Ordinal)) == "agac/a.txt,agac/alt/ç.txt,agac/bos/,agac/gizli.ini";
            using (new FileStream(Path.Combine(tree, "a.txt"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var partial = Folders.Zip(new[] { tree }, "agac", System.IO.Compression.CompressionLevel.Fastest, p => { }, () => false, missed);
                ok &= missed.Count == 1 && Path.GetFileName(partial.File) == "agac (2).zip" && partial.Text.Contains("eklenemedi");
            }
            try
            {
                Folders.Zip(new[] { tree }, "yarim2", System.IO.Compression.CompressionLevel.Optimal, p => { }, () => true, missed);
                ok = false;
            }
            catch (OperationCanceledException) { ok &= !File.Exists(Path.Combine(dir, "yarim2.zip")); }
            ok &= !Folders.Packable(new[] { @"C:\" }) && Tools.Title(new[] { @"C:\" }) == @"C:\"
                && Tools.For(new[] { tree }).Length == 6 && Tools.For(new[] { tree, tree }).Length == 5
                && Tools.For(new[] { tree, source }).Length == 3
                && Tools.Title(new[] { tree, tree }) == "2 klasör" && Tools.Describe(new[] { tree }) == "Klasör";
            new ZipWindow(new[] { tree }, false).Window.Close();
            new SizeWindow(new[] { tree }).Window.Close();

            // Who uses a file: this process while it holds it, nobody afterwards.
            string held = Path.Combine(dir, "tutulan.txt");
            File.WriteAllText(held, "x");
            int self = Process.GetCurrentProcess().Id;
            using (new FileStream(held, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                ok &= FileUsers.Of(held).Any(h => h.Id == self) && FileUsers.Locked(held);
            ok &= FileUsers.Of(held).Count == 0 && !FileUsers.Locked(held);
            new UsersWindow(new[] { held }).Window.Close();
            // This app is never closed from its own list, nor are Explorer, services and vital processes.
            Func<int, int, bool> refused = (id, type) =>
                FileUsers.WhyNot(new Holder { Id = id, Type = type, Name = "x", Started = DateTime.Now }) != null;
            ok &= refused(self, 1) && refused(999999, 4) && refused(999999, 3) && refused(999999, 1000) && refused(4, 0)
                && !refused(999999, 1);

            // Pinning: a window goes on top, is listed, and is released; the desktop is refused.
            using (var form = new Form { ShowInTaskbar = false })
            {
                IntPtr handle = form.Handle; // never shown
                ok &= !Pinner.IsOnTop(handle) && Pinner.Toggle(handle).StartsWith("Üstte") && Pinner.IsOnTop(handle)
                    && Pinner.Pinned().Contains(handle);
                Pinner.ReleaseAll();
                ok &= !Pinner.IsOnTop(handle) && Pinner.Pinned().Length == 0 && Pinner.Toggle(IntPtr.Zero) == null
                    && !Pinner.Pinnable(Native.FindWindowEx(IntPtr.Zero, IntPtr.Zero, "Shell_TrayWnd", null));
            }

            // Screen text: the dragged rectangle in any direction, kept on the screen.
            var dragArea = ScreenText.Between(new Point(50, 80), new Point(-10, 20), new Size(100, 60));
            ok &= dragArea == new Rectangle(0, 20, 50, 40);

            // Tables: words become rows and columns by their places alone; a title above and an empty cell stay put.
            Func<string, double, double, double, Word> word = (text, x, y, width) =>
                new Word { Text = text, X = x, Y = y, Width = width, Height = 20 };
            var table = TableText.Rows(new[]
            {
                word("Aylık", 0, 0, 60), word("gider", 68, 1, 60), word("raporu", 136, 0, 300),
                word("Kalem", 0, 40, 60), word("Tutar", 300, 42, 50), word("Not", 500, 39, 40),
                word("=2+2", 0, 80, 50), word("-1.250", 310, 80, 60),
                word("Ev", 0, 120, 25), word("kirası", 33, 121, 55), word("900", 320, 120, 40), word("peşin", 505, 118, 55)
            });
            ok &= table.Length == 4 && table.All(r => r.Length == 3)
                && TableText.Tsv(table) == "Aylık gider raporu\t\t\r\nKalem\tTutar\tNot\r\n'=2+2\t-1.250\t\r\nEv kirası\t900\tpeşin"
                && TableText.Rows(new Word[0]).Length == 0
                && TableText.Rows(new[] { word("1", 0, 0, 8), word("5,00", 14, 0, 40) })[0][0] == "15,00"
                && TableText.Rows(new[] { word("4,", 0, 0, 16), word("50", 22, 0, 20) })[0][0] == "4,50";
            // Three readings vote: the odd shape is left out, the majority wins, a text beats a gap.
            var voted = TableText.Agree(new System.Collections.Generic.List<string[][]> {
                new[] { new[] { "4150", "", "x" } }, new[] { new[] { "tek" } },
                new[] { new[] { "4,50", "", "x" } }, new[] { new[] { "4,50", "3", "y" } } });
            ok &= voted.Length == 1 && string.Join("|", voted[0]) == "4,50|3|x"
                && TableText.Agree(new System.Collections.Generic.List<string[][]> { new string[0][] }).Length == 0;

            // Colours: text forms, newest first without repeats, a bounded list, and a magnifier that stays on screen.
            string colorKey = @"Software\AllInOne-test-" + Guid.NewGuid().ToString("N"), ownKey = Prefs.Store;
            Prefs.Store = colorKey;
            try
            {
                var teal = Color.FromArgb(255, 0, 128, 255);
                ok &= ColorStore.Hex(teal) == "#0080FF" && ColorStore.Rgb(teal) == "rgb(0, 128, 255)"
                    && ColorStore.Parse("#0080FF") == teal && ColorStore.Parse("0080FF") == null && ColorStore.Parse("#GG0000") == null;
                ok &= ColorStore.All().Count == 0 && Prefs.ShowPicker && ColorStore.Text(teal) == "#0080FF";
                ColorStore.Add(teal);
                ColorStore.Add(Color.FromArgb(255, 1, 2, 3));
                ColorStore.Add(teal);
                ok &= ColorStore.All().Count == 2 && ColorStore.All()[0] == teal;
                for (int i = 0; i < 50; i++) ColorStore.Add(Color.FromArgb(255, i, 0, 0));
                ok &= ColorStore.All().Count == ColorStore.Most && ColorStore.All()[0].R == 49;
                Prefs.CopyRgb = true;
                ok &= ColorStore.Text(teal) == "rgb(0, 128, 255)";
                new ColorHistory(new Point(0, 0)).Window.Close();
                using (var drawn = Picker.Draw(teal)) ok &= drawn.GetPixel(5, 16).ToArgb() == teal.ToArgb() && drawn.GetPixel(0, 0).A == 0;
            }
            finally
            {
                Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(colorKey, false);
                Prefs.Store = ownKey;
            }
            var screenSize = new Size(1000, 800);
            var lens = ColorPick.Magnifier(new Point(10, 10), screenSize);
            var flipped = ColorPick.Magnifier(new Point(995, 795), screenSize);
            ok &= lens.X > 10 && lens.Y > 10 && flipped.Right < 995 && flipped.Bottom < 795;

            // Presets: stored values survive, bad ones are refused, one save does resize, format and size limit.
            string testKey = @"Software\AllInOne-test-" + Guid.NewGuid().ToString("N"), storeKey = Prefs.Store;
            Prefs.Store = testKey;
            try
            {
                ok &= Presets.All().Count == 3;
                Presets.Put(new Preset { Name = "Dar", MaxSide = 100, Format = ".jpg", LimitKb = 6 });
                Presets.Put(new Preset { Name = "dar", MaxSide = 120, Format = ".jpg", LimitKb = 6 });
                ok &= Presets.All().Count == 4 && Presets.All()[3].MaxSide == 120;
                Presets.Remove("DAR");
                ok &= Presets.All().Count == 3 && Preset.Parse("x|abc|.jpg|0|80") == null && Preset.Parse("x|0|.exe|0|80") == null
                    && new Preset { Name = "E Posta" }.Suffix == "_e-posta" && new Preset { MaxSide = 3 }.Problem() != null;
            }
            finally
            {
                Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(testKey, false);
                Prefs.Store = storeKey;
            }
            var narrow = new Preset { Name = "Dar", MaxSide = 200, Format = ".jpg", LimitKb = 8 };
            var recipe = Images.Load(source);
            var cooked = Presets.Make(narrow, recipe, ".jpg");
            ok &= cooked.Data.Length <= 8 * 1024 && cooked.Width <= 200
                && Presets.Skip(narrow, source, recipe, ".jpg", cooked) == null
                && Presets.Extension(narrow, source, new Picture { Transparent = true }) == ".png";
            var applied = Presets.Apply(narrow, source);
            ok &= applied.File != null && Presets.Apply(narrow, applied.File).File == null;

            // Renaming: tokens, Turkish letters, conflicts, exchanged names, undo and rollback.
            var rules = new RenameOptions { Pattern = "{ad}-{n}-{tarih}", Find = "ı", Replace = "i", Start = 7, Digits = 3 };
            ok &= Renamer.Stem("IŞIK{n}", 7, new DateTime(2026, 3, 5), rules) == "iŞiK{n}-007-2026-03-05";
            rules = new RenameOptions { Pattern = "{ad}", Case = LetterCase.Upper };
            ok &= Renamer.Stem("ilık", 1, DateTime.Today, rules) == "İLIK";
            string box1 = Path.Combine(dir, "ad");
            Directory.CreateDirectory(box1);
            Func<string, string> inBox = n => Path.Combine(box1, n);
            File.WriteAllText(inBox("1.txt"), "bir");
            File.WriteAllText(inBox("2.txt"), "iki");
            File.WriteAllText(inBox("x2.txt"), "dolu");
            var pair = new[] { inBox("1.txt"), inBox("2.txt") };
            var shifted = Renamer.Plan(pair, new RenameOptions { Pattern = "{n}", Start = 2, Digits = 1 }, File.Exists);
            ok &= shifted.All(i => i.Problem == null); // 1 takes the name 2 is leaving
            ok &= Renamer.Plan(pair, new RenameOptions { Pattern = "ayni" }, File.Exists).All(i => i.Problem != null)
                && Renamer.Plan(new[] { inBox("2.txt") }, new RenameOptions { Pattern = "x{ad}" }, File.Exists)[0].Problem != null
                && Renamer.Plan(pair, new RenameOptions { Pattern = "a:b{n}" }, File.Exists)[0].Problem != null;
            Renamer.Apply(shifted);
            ok &= File.ReadAllText(inBox("2.txt")) == "bir" && File.ReadAllText(inBox("3.txt")) == "iki" && !File.Exists(inBox("1.txt"));
            Renamer.Apply(Renamer.Reversed(shifted));
            ok &= File.ReadAllText(inBox("1.txt")) == "bir" && File.ReadAllText(inBox("2.txt")) == "iki";
            try
            {
                Renamer.Apply(new[]
                {
                    new RenameItem { From = inBox("1.txt"), To = inBox("yeni.txt") },
                    new RenameItem { From = inBox("2.txt"), To = inBox("x2.txt") }
                });
                ok = false;
            }
            catch (IOException) { }
            ok &= Directory.GetFiles(box1).Length == 3 && File.ReadAllText(inBox("1.txt")) == "bir"
                && File.ReadAllText(inBox("2.txt")) == "iki" && File.ReadAllText(inBox("x2.txt")) == "dolu";
            new RenameWindow(pair).Window.Close();

            // Video: times, bit rates and the FFmpeg arguments.
            ok &= Video.Time("75") == 75 && Video.Time("1:02,5") == 62.5 && Video.Time("1:00:00") == 3600
                && Video.Time("a") == null && Video.Time("-3") == null && Video.Clock(75) == "1:15";
            int pictureRate, soundRate;
            Video.Rates(10, 60, true, out pictureRate, out soundRate);
            ok &= soundRate == 128 && (pictureRate + soundRate) * 60 / 8192.0 < 10;
            var film = new VideoInfo { Seconds = 20, Width = 640, Height = 360, VideoCodec = "h264", AudioCodec = "aac" };
            ok &= Video.Arguments(new VideoJob { Task = VideoTask.Audio, AudioExt = ".m4a" }, film, "a", "b", 0, "l").Contains("-c:a copy")
                && Video.Arguments(new VideoJob { Task = VideoTask.Compress, TargetMb = 1 }, film, "a", "b", 1, "l").EndsWith("-f null NUL")
                && Video.Arguments(new VideoJob { Task = VideoTask.Compress, Mute = true }, film, "a", "b", 0, "l").Contains(" -an")
                && Video.Span(new VideoJob { Task = VideoTask.Gif, Start = 18, Length = 5 }, film) == 2;
            if (Video.Available)
            {
                new VideoWindow(new[] { source }, new VideoJob { Task = VideoTask.Gif }, false).Window.Close();
                new VideoWindow(new[] { source, source }, new VideoJob { Task = VideoTask.Compress, TargetMb = 5 }, false).Window.Close();
            }

            // Several files: the menu, the sizes, the crop areas, the names and a batch with one bad file.
            string note = Path.Combine(dir, "n.txt");
            File.WriteAllText(note, "x");
            ok &= Tools.For(new[] { source, source }).Length == 7 && Tools.For(new[] { source, note }).Length == 5
                && Tools.For(new[] { note }).Length == 7 && Tools.Title(new[] { source, source }).StartsWith("2 ");
            int tw, th;
            Images.TargetSize(400, 200, 50, 0, 0, 0, true, out tw, out th);
            ok &= tw == 200 && th == 100;
            Images.TargetSize(400, 200, 50, 1, 100, 999, true, out tw, out th);
            ok &= tw == 100 && th == 50;
            Images.TargetSize(400, 200, 50, 2, 999, 100, true, out tw, out th);
            ok &= tw == 200 && th == 100;
            Images.TargetSize(400, 200, 50, 1, 30, 40, false, out tw, out th);
            ok &= tw == 30 && th == 40;
            var free = Images.Transfer(new System.Windows.Rect(10, 20, 50, 40), 100, 100, 200, 400, 0);
            var squared = Images.Transfer(new System.Windows.Rect(50, 0, 100, 100), 200, 100, 100, 300, 1);
            ok &= free == new System.Windows.Rect(20, 80, 100, 160) && squared.Width == 100 && squared.Height == 100 && squared.X == 0;
            ok &= Naming.BatchName(source, "", 0, "_50", false, "") == "a_50" && Naming.BatchName(source, "tatil", 2, "_50", false, "_x") == "tatil_3"
                && Naming.BatchName(source, "tatil", 0, "_50", true, "_web") == "tatil_1_web";
            var batch = Images.Batch(new[] { source, note, source }, (f, i) => Images.Scale(f, 10), null, null);
            ok &= batch.File != null && batch.Failed == 1 && batch.Saved == 2 && batch.Text.StartsWith("2 ")
                && batch.Text.Split('\n')[1].StartsWith("Toplam ");
            ok &= Images.Difference(4096, 1024) == "%75 daha küçük · 3 KB kazanç" && Images.Difference(1024, 2048) == "%100 daha büyük · +1 KB"
                && Images.Difference(5, 5) == "Boyut aynı" && Images.Kind(".JPEG") == "JPG" && Images.Kind(".webp") == "WebP";
            int asked = 0;
            var once = Images.Once(() => { asked++; return true; });
            ok &= once() && once() && asked == 1;
            // A file name with XML characters must not break the notification.
            var toast = new System.Xml.XmlDocument();
            toast.LoadXml(Toast.Xml(new Outcome { File = @"C:\x\a&b'c.png", Text = "a&b'c.png\n<1 KB>" }));
            ok &= toast.SelectNodes("//text").Count == 3 && toast.SelectNodes("//text")[1].InnerText == "a&b'c.png"
                && toast.SelectNodes("//action").Count == 1;
            toast.LoadXml(Toast.Xml(new Outcome { Text = "bilgi" }));
            ok &= toast.SelectNodes("//action").Count == 0;

            // A downloaded exe registers its own notification name and icon.
            string idKey = @"Software\AllInOne-test-" + Guid.NewGuid().ToString("N"), idFolder = Path.Combine(dir, "kimlik");
            try
            {
                Toast.Register(@"HKEY_CURRENT_USER\" + idKey, idFolder);
                var icon = Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\" + idKey, "IconUri", null) as string;
                ok &= icon == Path.Combine(idFolder, "app.ico") && new FileInfo(icon).Length > 0
                    && "All In One".Equals(Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\" + idKey, "DisplayName", null));
            }
            finally { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(idKey, false); }

            // Before Windows 11 the tray icon notifies; its text must fit and never be empty.
            string longText = Toast.TrayText(new Outcome { File = "x", Text = new string('a', 400) });
            ok &= Toast.UsesTray(19045) && !Toast.UsesTray(22000) && Toast.Build > 9600 && longText.Length == 255
                && longText.EndsWith("tıkla.") && Toast.TrayText(new Outcome { Text = "" }) == "Tamamlandı."
                && Toast.TrayText(new Outcome { Text = "bilgi" }) == "bilgi";

            // Uninstalling takes away the app's own keys, entries and files, and nothing else.
            string gone = @"Software\AllInOne-test-" + Guid.NewGuid().ToString("N"), keptStore = Prefs.Store,
                keptRun = Prefs.RunList, keptApproved = Prefs.Approved, keptLog = Log.Folder;
            Prefs.Store = gone + @"\ayar";
            Prefs.RunList = gone + @"\acilis";
            Prefs.Approved = gone + @"\onay";
            Log.Folder = Path.Combine(dir, "silinecek");
            try
            {
                Prefs.Autostart = true;
                Prefs.CopyRgb = true;
                Toast.Register(@"HKEY_CURRENT_USER\" + gone + @"\kimlik", Log.Folder);
                Log.Info("x");
                string others = Path.Combine(Log.Folder, "benim.txt");
                File.WriteAllText(others, "kalmali");
                Uninstall.Remove(gone + @"\kimlik");
                Log.Info("sonradan");
                using (var left = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(gone))
                    ok &= left != null && left.OpenSubKey("ayar") == null && left.OpenSubKey("kimlik") == null
                        && left.OpenSubKey("acilis").GetValue("AllInOne") == null
                        && Directory.GetFiles(Log.Folder).Length == 1 && File.Exists(others);
            }
            finally
            {
                Log.Off = false;
                Prefs.Store = keptStore;
                Prefs.RunList = keptRun;
                Prefs.Approved = keptApproved;
                Log.Folder = keptLog;
                Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(gone, false);
            }

            // A transparent image is not flattened without consent.
            string clear = Path.Combine(dir, "b.png");
            using (var image = new Bitmap(20, 20)) image.Save(clear, System.Drawing.Imaging.ImageFormat.Png);
            ok &= Images.ConvertTo(clear, ".jpg", never) == null && Directory.GetFiles(dir, "b*.jpg").Length == 0;

            // Every format that can hold transparency keeps it: left half clear, right half noisy colour.
            string half = Path.Combine(dir, "c.png");
            using (var image = new Bitmap(200, 100))
            {
                var noise = new Random(2);
                for (int y = 0; y < 100; y++)
                    for (int x = 100; x < 200; x++)
                        image.SetPixel(x, y, Color.FromArgb(255, 200 + noise.Next(40), noise.Next(40), noise.Next(40)));
                image.Save(half, System.Drawing.Imaging.ImageFormat.Png);
            }
            Func<Outcome, string, bool> keeps = (outcome, ext) =>
            {
                if (outcome == null || outcome.File == null || Path.GetExtension(outcome.File) != ext) return false;
                var p = Images.Load(outcome.File).Pixels;
                var left = new byte[4];
                var right = new byte[4];
                p.CopyPixels(new System.Windows.Int32Rect(p.PixelWidth / 4, p.PixelHeight / 2, 1, 1), left, 4, 0);
                p.CopyPixels(new System.Windows.Int32Rect(p.PixelWidth * 3 / 4, p.PixelHeight / 2, 1, 1), right, 4, 0);
                return left[3] == 0 && right[3] == 255 && right[2] > 150 && right[1] < 90;
            };
            foreach (var ext in new[] { ".gif", ".tif" }) ok &= keeps(Images.ConvertTo(half, ext, never), ext);
            ok &= keeps(Images.Scale(half, 50), ".png");
            var small = Images.Compress(half, 50);
            ok &= keeps(small, ".png") && new FileInfo(small.File).Length < new FileInfo(half).Length;
            var gif = Path.Combine(dir, "d.gif");
            File.Copy(Directory.GetFiles(dir, "c*.gif")[0], gif);
            ok &= keeps(Images.Scale(gif, 50), ".gif") && keeps(Images.ConvertTo(gif, ".png", never), ".png");

            // A format that cannot hold the clear areas is swapped for PNG: a wrongly named file and a BMP.
            string misnamed = Path.Combine(dir, "m.jpg");
            File.Copy(half, misnamed);
            ok &= keeps(Images.Scale(misnamed, 50), ".png") && Images.Writable("a.bmp", true) == ".png"
                && Images.Writable("a.bmp", false) == ".bmp" && Images.Writable("a.tif", true) == ".tif";

            // Crop shapes fill their area and clear only what is outside.
            foreach (CropShape shape in Enum.GetValues(typeof(CropShape)))
            {
                var bounds = Images.Shaped(shape, new System.Windows.Rect(10, 20, 100, 50), 20).Bounds;
                ok &= Math.Abs(bounds.X - 10) < 0.5 && Math.Abs(bounds.Y - 20) < 0.5 && Math.Abs(bounds.Width - 100) < 0.5
                    && Math.Abs(bounds.Height - 50) < 0.5;
            }
            var round = Images.Masked(pixels, CropShape.Circle, 0);
            byte[] corner = new byte[4], middle = new byte[4], before = new byte[4];
            round.CopyPixels(new System.Windows.Int32Rect(0, 0, 1, 1), corner, 4, 0);
            round.CopyPixels(new System.Windows.Int32Rect(200, 100, 1, 1), middle, 4, 0);
            pixels.CopyPixels(new System.Windows.Int32Rect(200, 100, 1, 1), before, 4, 0);
            ok &= corner[3] == 0 && middle.SequenceEqual(before);
            Func<System.Windows.Int32Rect, int, int, int, int, bool> at = (r, x, y, w, h) =>
                r.X == x && r.Y == y && r.Width == w && r.Height == h;
            ok &= at(Images.Fitted(390, -5, 100, 50, 0, 400, 200, false), 300, 0, 100, 50)
                && at(Images.Fitted(0, 0, 160, 10, 16.0 / 9, 400, 200, false), 0, 0, 160, 90)
                && at(Images.Fitted(0, 0, 999, 100, 1, 400, 200, true), 0, 0, 100, 100)
                && at(Images.Fitted(0, 0, 999, 999, 1, 400, 200, false), 0, 0, 200, 200);
            var box = new System.Windows.Rect(100, 50, 100, 100);
            ok &= Images.Stretched(box, 1, 0, new System.Windows.Point(900, 0), 0, 400, 200) == new System.Windows.Rect(100, 50, 300, 100)
                && Images.Stretched(box, -1, -1, new System.Windows.Point(50, 20), 0, 400, 200) == new System.Windows.Rect(50, 20, 150, 130)
                && Images.Stretched(box, 1, 0, new System.Windows.Point(300, 0), 1, 400, 200) == new System.Windows.Rect(100, 0, 200, 200);

            // Turning: the combined orientation equals the steps one after another, and a selection follows its content.
            Func<System.Windows.Media.Imaging.BitmapSource, byte[]> bytes = b =>
            {
                var data = new byte[b.PixelWidth * b.PixelHeight * 4];
                b.CopyPixels(data, b.PixelWidth * 4, 0);
                return data;
            };
            var stepped = pixels;
            int turns = 0;
            bool mirrored = false;
            var part = new System.Windows.Rect(0.1, 0.2, 0.3, 0.4);
            foreach (var step in new[] { Turn.Right, Turn.Mirror, Turn.Right, Turn.Flip, Turn.Left, Turn.Mirror, Turn.Left })
            {
                int own = step == Turn.Right ? 1 : step == Turn.Left ? 3 : step == Turn.Flip ? 2 : 0;
                var next = Images.Oriented(stepped, own, step == Turn.Mirror || step == Turn.Flip);
                var moved = Images.Moved(part, step);
                Func<System.Windows.Media.Imaging.BitmapSource, System.Windows.Rect, System.Windows.Media.Imaging.BitmapSource> cut =
                    (b, r) => Images.Cut(b, new System.Windows.Int32Rect((int)Math.Round(r.X * b.PixelWidth),
                        (int)Math.Round(r.Y * b.PixelHeight), (int)Math.Round(r.Width * b.PixelWidth),
                        (int)Math.Round(r.Height * b.PixelHeight)));
                ok &= bytes(cut(next, moved)).SequenceEqual(bytes(Images.Oriented(cut(stepped, part), own,
                    step == Turn.Mirror || step == Turn.Flip)));
                Images.Compose(step, ref turns, ref mirrored);
                ok &= bytes(Images.Oriented(pixels, turns, mirrored)).SequenceEqual(bytes(next));
                stepped = next;
                part = moved;
            }

            // A batch into one archive: equal names are numbered, an unfinished archive is removed.
            Func<Bundle, Outcome> packed = bundle => Images.Batch(new[] { source, source }, (f, i) =>
                Images.SaveEncoded(f, new byte[] { 1, 2, 3 }, ".png", "ayni", false, bundle), null, null);
            using (var bundle = new Bundle(dir, "paket"))
            {
                var report = packed(bundle);
                bundle.Finish(report);
                ok &= report.File == Path.Combine(dir, "paket.zip") && report.Text.Split('\n')[0].EndsWith("paket.zip")
                    && Directory.GetFiles(dir, "ayni*").Length == 0;
            }
            using (var zip = new System.IO.Compression.ZipArchive(File.OpenRead(Path.Combine(dir, "paket.zip"))))
                ok &= string.Join(",", zip.Entries.Select(entry => entry.Name)) == "ayni.png,ayni (2).png" && zip.Entries[1].Length == 3;
            using (var bundle = new Bundle(dir, "yarim")) packed(bundle);
            ok &= !File.Exists(Path.Combine(dir, "yarim.zip"));

            if (Images.Ffmpeg != null)
            {
                // WebP is written, read back, keeps transparency and stays WebP when scaled or compressed.
                var webp = Images.ConvertTo(half, ".webp", never);
                ok &= keeps(webp, ".webp");
                ok &= keeps(Images.Scale(webp.File, 50), ".webp");
                var tighter = Images.Compress(webp.File, 20);
                ok &= keeps(tighter, ".webp") && new FileInfo(tighter.File).Length < new FileInfo(webp.File).Length;
            }
            ok &= Tests.Run(dir);
            return ok ? 0 : 2;
        }
        finally
        {
            Log.Folder = realLog;
            Prefs.Store = realStore;
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(testStore, false);
            Directory.Delete(dir, true);
        }
    }
}

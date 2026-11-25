// "Why can't I delete this file?": asks Windows which programs hold the selected files.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Path = System.IO.Path;
using FileTime = System.Runtime.InteropServices.ComTypes.FILETIME;

class Holder
{
    public int Id, Type;
    public DateTime Started; // tells the process apart from a later one that got the same number
    public string Name, Exe, Kind; // Exe "": Windows does not let us see it
}

// The Restart Manager, which installers use to find the programs that are in their way.
static class FileUsers
{
    [StructLayout(LayoutKind.Sequential)] struct UniqueProcess { public int Id; public FileTime Started; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct ProcessInfo
    {
        public UniqueProcess Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string AppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string Service;
        public int Type;
        public uint Status, Session;
        [MarshalAs(UnmanagedType.Bool)] public bool Restartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    static extern int RmStartSession(out uint session, int flags, StringBuilder key);
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    static extern int RmRegisterResources(uint session, uint files, string[] names, uint processes, IntPtr ids, uint services, IntPtr list);
    [DllImport("rstrtmgr.dll")]
    static extern int RmGetList(uint session, out uint needed, ref uint count, [In, Out] ProcessInfo[] processes, ref uint reboot);
    [DllImport("rstrtmgr.dll")] static extern int RmEndSession(uint session);

    const int MoreData = 234;

    static void Check(int error)
    {
        if (error != 0) throw new Win32Exception(error);
    }

    public static List<Holder> Of(string file)
    {
        uint session;
        Check(RmStartSession(out session, 0, new StringBuilder(33)));
        try
        {
            Check(RmRegisterResources(session, 1, new[] { file }, 0, IntPtr.Zero, 0, IntPtr.Zero));
            uint needed, count = 0, reboot = 0;
            ProcessInfo[] found = null;
            // The list can grow between asking for its size and reading it.
            for (int attempt = 0; ; attempt++)
            {
                int error = RmGetList(session, out needed, ref count, found, ref reboot);
                if (error == 0) break;
                if (error != MoreData || attempt > 5) throw new Win32Exception(error);
                count = needed + 2;
                found = new ProcessInfo[count];
            }
            return (found ?? new ProcessInfo[0]).Take((int)count).Select(Describe).ToList();
        }
        finally { RmEndSession(session); }
    }

    static Holder Describe(ProcessInfo info)
    {
        long started = ((long)info.Process.Started.dwHighDateTime << 32) | (uint)info.Process.Started.dwLowDateTime;
        var holder = new Holder { Id = info.Process.Id, Type = info.Type, Started = DateTime.FromFileTime(started),
            Name = info.AppName, Exe = "",
            Kind = info.Type == 3 ? "Windows hizmeti" : info.Type == 4 ? "Windows Gezgini" : info.Type == 5 ? "komut penceresi"
                : info.Type == 1000 ? "kritik sistem işlemi" : "program" };
        try
        {
            using (var process = Process.GetProcessById(info.Process.Id))
            {
                if (string.IsNullOrWhiteSpace(holder.Name)) holder.Name = process.ProcessName;
                holder.Exe = process.MainModule.FileName;
            }
        }
        catch (Exception) { } // it has ended, or belongs to a protected or elevated program
        return holder;
    }

    // Programs Windows cannot do without.
    static readonly string[] Vital = { "system", "registry", "smss", "csrss", "wininit", "winlogon", "services", "lsass",
        "svchost", "dwm", "fontdrvhost" };

    // The running process the holder stands for; null when it has ended.
    static Process Running(Holder holder)
    {
        Process process = null;
        try
        {
            process = Process.GetProcessById(holder.Id);
            if (Math.Abs((process.StartTime - holder.Started).TotalSeconds) < 1) return process;
        }
        catch (ArgumentException) { } // no process with this number
        catch (InvalidOperationException) { } // it ended meanwhile
        if (process != null) process.Dispose();
        return null;
    }

    // Why this program is not closed from here, or null when it may be.
    public static string WhyNot(Holder holder)
    {
        if (holder.Id == Process.GetCurrentProcess().Id) return "Bu, All In One'ın kendisi.";
        if (holder.Type == 4) return "Windows Gezgini buradan kapatılmaz: ilgili klasör penceresini veya önizleme bölmesini kapat.";
        if (holder.Type == 3) return "Windows hizmetleri buradan kapatılmaz.";
        bool vital = holder.Type == 1000 || holder.Id <= 4;
        try
        {
            using (var process = Running(holder)) vital |= process != null && Vital.Contains(process.ProcessName.ToLowerInvariant());
        }
        catch (Exception) { return "Windows bu programa erişime izin vermiyor (yönetici olarak çalışıyor olabilir)."; }
        return vital ? "Sistem için gerekli bir işlem; kapatılmaz." : null;
    }

    public static bool HasWindow(Holder holder)
    {
        try
        {
            using (var process = Running(holder)) return process != null && process.MainWindowHandle != IntPtr.Zero;
        }
        catch (Exception) { return false; }
    }

    // Asks the program to close, as its close button would, or with force ends it at once.
    // Returns what went wrong, null when the program is gone.
    public static string End(Holder holder, bool force, int patience = 5000)
    {
        string why = WhyNot(holder);
        if (why != null) return why;
        try
        {
            using (var process = Running(holder))
            {
                if (process == null) return null;
                if (force) process.Kill();
                else if (!process.CloseMainWindow()) return "Programın kapatılacak bir penceresi yok.";
                return process.WaitForExit(patience) ? null
                    : "Program hâlâ açık. Kaydetmek isteyip istemediğini soruyor olabilir; penceresine bak.";
            }
        }
        catch (InvalidOperationException) { return null; } // it ended in the same instant
        catch (Exception e) { return "Kapatılamadı: " + e.Message; }
    }

    // Whether the file refuses to be opened alone, even if no holder can be named.
    public static bool Locked(string file)
    {
        try
        {
            using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None)) return false;
        }
        catch (IOException) { return File.Exists(file); }
        catch (UnauthorizedAccessException) { return false; } // no permission is a different matter
    }
}

class UsersWindow
{
    const int Most = 200; // files asked about
    public readonly Window Window = Assets.Window("Editor.xaml");
    readonly string[] files;
    bool busy;
    string problem; // of the last attempt to close a program

    // Asks before a program is ended without a chance to save; the test replaces it.
    public static Func<Window, string, bool> Confirm = (owner, name) => MessageBox.Show(owner,
        name + " hemen sonlandırılacak.\n\nKaydedilmemiş çalışma kaybolur. Devam edilsin mi?", "Zorla kapat",
        MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;

    T Get<T>(string name) where T : class { return (T)Window.FindName(name); }

    public UsersWindow(string[] chosen)
    {
        files = chosen;
        Assets.Dress(Window);
        Window.Title = "Kim Kullanıyor? - " + (files.Length > 1 ? files.Length + " dosya" : Path.GetFileName(files[0]));
        Get<TextBlock>("Heading").Text = "Kim Kullanıyor?";
        Get<ScrollViewer>("Settings").Visibility = Visibility.Collapsed;
        Get<TextBlock>("Loading").Visibility = Visibility.Collapsed;
        Get<Grid>("Frame").Visibility = Visibility.Collapsed;
        Get<ScrollViewer>("RenameList").Visibility = Visibility.Visible;
        Get<TextBlock>("Details").TextWrapping = TextWrapping.Wrap;
        var again = Get<Button>("Save");
        again.Content = "Yenile";
        again.Click += (s, e) => Look();
        Get<Button>("Cancel").Content = "Kapat";
        Get<Button>("Cancel").Click += (s, e) => Window.Close();
        Window.Loaded += (s, e) => Look();
    }

    class Finding
    {
        public string File, Problem;
        public List<Holder> Holders = new List<Holder>();
        public bool Locked;
    }

    void Look()
    {
        if (busy) return;
        Busy("Bakılıyor…");
        Images.Background(() =>
        {
            var findings = files.Take(Most).Select(file =>
            {
                var finding = new Finding { File = file };
                try
                {
                    if (!File.Exists(file)) finding.Problem = "Dosya artık yok.";
                    else
                    {
                        finding.Holders = FileUsers.Of(file);
                        finding.Locked = finding.Holders.Count == 0 && FileUsers.Locked(file);
                    }
                }
                catch (Exception e) { finding.Problem = "Sorulamadı: " + e.Message; }
                return finding;
            }).ToList();
            Window.Dispatcher.BeginInvoke((Action)(() =>
            {
                busy = false;
                Get<Button>("Save").IsEnabled = true;
                Get<StackPanel>("Rows").IsEnabled = true;
                var error = Get<TextBlock>("Error");
                error.Text = problem ?? "";
                error.Visibility = problem == null ? Visibility.Collapsed : Visibility.Visible;
                problem = null;
                Show(findings);
            }));
        });
    }

    void Busy(string doing)
    {
        busy = true;
        Get<Button>("Save").IsEnabled = false;
        Get<StackPanel>("Rows").IsEnabled = false;
        Get<TextBlock>("Details").Text = doing;
    }

    void End(Holder holder, bool force)
    {
        if (busy || (force && !Confirm(Window, holder.Name))) return;
        Busy(holder.Name + " kapatılıyor…");
        Images.Background(() =>
        {
            string failed = FileUsers.End(holder, force);
            Window.Dispatcher.BeginInvoke((Action)(() =>
            {
                busy = false;
                problem = failed;
                Look(); // shows whether the file is free now
            }));
        });
    }

    // The ways to close a holder, or why there is none.
    void Controls(Panel rows, Holder holder)
    {
        string why = FileUsers.WhyNot(holder);
        if (why != null)
        {
            Line(rows, why, "#A6A9B4", 11.5, 12, false);
            return;
        }
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 4, 0, 6) };
        Action<string, string, bool> add = (label, hint, force) =>
        {
            var button = new Button { Content = label, ToolTip = hint, FontSize = 12, Padding = new Thickness(12, 4, 12, 4),
                Margin = new Thickness(0, 0, 8, 0) };
            button.Click += (s, e) => End(holder, force);
            buttons.Children.Add(button);
        };
        if (FileUsers.HasWindow(holder)) add("Programı kapat", "Pencerenin kapatma düğmesi gibi: program kaydetmeyi sorabilir", false);
        add("Zorla kapat", "Hemen sonlandırır; kaydedilmemiş çalışma kaybolur", true);
        rows.Children.Add(buttons);
    }

    void Show(List<Finding> findings)
    {
        var rows = Get<StackPanel>("Rows");
        rows.Children.Clear();
        // The files in use come first.
        foreach (var finding in findings.OrderBy(f => f.Holders.Count > 0 || f.Locked ? 0 : 1))
        {
            Line(rows, Path.GetFileName(finding.File), "#F2F3F5", 14, 0, true);
            if (finding.Problem != null) Line(rows, finding.Problem, "#FF8A80", 12.5, 12, false);
            else if (finding.Locked)
                Line(rows, "Kilitli, ama tutan program görünmüyor. Yönetici olarak çalışan bir program veya antivirüs olabilir.",
                    "#FFB86B", 12.5, 12, false);
            else if (finding.Holders.Count == 0) Line(rows, "Hiçbir program tutmuyor.", "#7EE2A8", 12.5, 12, false);
            foreach (var holder in finding.Holders)
            {
                Line(rows, holder.Name + "  ·  " + holder.Kind + "  ·  işlem " + holder.Id, "#FFB86B", 13, 12, false);
                if (holder.Exe != "") Line(rows, holder.Exe, "#A6A9B4", 11.5, 12, false);
                Controls(rows, holder);
            }
            rows.Children.Add(new Border { Height = 14 });
        }
        int used = findings.Count(f => f.Holders.Count > 0 || f.Locked);
        Get<TextBlock>("Details").Text = (used == 0 ? "Seçilen dosyaları tutan program bulunamadı."
            : used + " dosya kullanımda. Programı soldan veya kendi penceresinden kapat.")
            + (files.Length > Most ? "\nİlk " + Most + " dosyaya bakıldı." : "")
            + "\n\nBazı programlar dosyayı okuyup hemen bırakır; açık görünseler de listede çıkmazlar.";
    }

    static void Line(Panel rows, string text, string color, double size, double indent, bool bold)
    {
        rows.Children.Add(new TextBlock
        {
            Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(indent, 0, 0, 3),
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = (Brush)new BrushConverter().ConvertFromString(color)
        });
    }

    public static void Open(string[] files)
    {
        Assets.Show(new UsersWindow(files).Window);
    }
}

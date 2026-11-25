// The error log: what went wrong and when, in a text file on this computer only.
using System;
using System.IO;
using System.Text;
using System.Threading;
using Path = System.IO.Path;

static class Log
{
    // The self-test and the tests write somewhere else.
    public static string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AllInOne");
    public const long Largest = 1048576; // then the file is set aside and a new one begins
    public static bool Off; // after uninstalling
    static readonly object gate = new object();
    static DateTime told = DateTime.MinValue;

    public static string File { get { return Path.Combine(Folder, "hata.log"); } }
    public static string Older { get { return Path.Combine(Folder, "hata.eski.log"); } }

    public static void Info(string what) { Write("BİLGİ", what, null); }

    public static void Error(string what, Exception e = null) { Write("HATA ", what, e); }

    // For failures nobody handled: logged, and told at most once a minute so a repeating one cannot flood.
    public static void Unexpected(string where, Exception e)
    {
        Error("beklenmeyen hata: " + where, e);
        lock (gate)
        {
            if ((DateTime.Now - told).TotalSeconds < 60) return;
            told = DateTime.Now;
        }
        try { Images.Notify(new Outcome { Text = "Beklenmeyen bir hata oldu; hata kaydına yazıldı." }); }
        catch (Exception x) { Error("hata bildirilemedi", x); }
    }

    // Writing the log must never be the thing that fails.
    static void Write(string kind, string what, Exception e)
    {
        if (Off) return;
        try
        {
            lock (gate)
            {
                Directory.CreateDirectory(Folder);
                var current = new FileInfo(File);
                if (current.Exists && current.Length > Largest)
                {
                    System.IO.File.Delete(Older);
                    System.IO.File.Move(File, Older);
                }
                Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + kind + "  " + what
                    + Environment.NewLine + (e == null ? "" : e + Environment.NewLine + Environment.NewLine));
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // A program that is reading the file at this instant (a virus scanner) must not cost the entry.
    static void Append(string text)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                System.IO.File.AppendAllText(File, text, Encoding.UTF8);
                return;
            }
            catch (IOException)
            {
                if (attempt >= 20) throw;
                Thread.Sleep(15);
            }
        }
    }

    // Catches what would otherwise end the app or pass unnoticed.
    public static void Watch()
    {
        System.Windows.Forms.Application.SetUnhandledExceptionMode(System.Windows.Forms.UnhandledExceptionMode.CatchException);
        System.Windows.Forms.Application.ThreadException += (s, e) => Unexpected("arayüz", e.Exception);
        System.Windows.Threading.Dispatcher.CurrentDispatcher.UnhandledException += (s, e) =>
        {
            Unexpected("pencere", e.Exception);
            e.Handled = true;
        };
        // This one cannot be survived, only recorded.
        AppDomain.CurrentDomain.UnhandledException += (s, e) => Error("uygulama kapanıyor", e.ExceptionObject as Exception);
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s, e) =>
        {
            Error("sahipsiz görev hatası", e.Exception);
            e.SetObserved();
        };
    }

    public static void Open()
    {
        if (!System.IO.File.Exists(File)) Info("Kayıt açıldı.");
        // Notepad by its full path: nothing planted elsewhere can run in its place.
        System.Diagnostics.Process.Start(Path.Combine(Environment.SystemDirectory, "notepad.exe"), "\"" + File + "\"");
    }
}

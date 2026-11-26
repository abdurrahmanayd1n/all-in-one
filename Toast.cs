// Native Windows notification; clicking it or its button opens the folder with the new file selected.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

static class Toast
{
    const string AppId = "AllInOne.App";
    public const string IdentityKey = @"Software\Classes\AppUserModelId\" + AppId; // under HKEY_CURRENT_USER
    public const string Identity = @"HKEY_CURRENT_USER\" + IdentityKey;

    // Name and icon under which Windows shows the notifications. The app writes them itself, so a downloaded
    // exe works without kur.ps1.
    public static void Register(string key, string folder)
    {
        string icon = Path.Combine(folder, "app.ico");
        if (!File.Exists(icon))
        {
            Directory.CreateDirectory(folder);
            using (var from = Assets.Open("app.ico"))
            using (var to = File.Create(icon)) from.CopyTo(to);
        }
        Microsoft.Win32.Registry.SetValue(key, "DisplayName", "All In One");
        Microsoft.Win32.Registry.SetValue(key, "IconUri", icon);
    }

    // Click events reach only notification objects that are still alive.
    static readonly Queue<ToastNotification> recent = new Queue<ToastNotification>();

    public static string Xml(Outcome outcome)
    {
        var lines = new[] { outcome.File != null ? Lang.T("Kaydedildi") : "All In One" }.Concat(outcome.Text.Split('\n'));
        string texts = string.Concat(lines.Select(line => "<text>" + SecurityElement.Escape(line) + "</text>"));
        string button = outcome.File == null ? ""
            : "<actions><action content=\"" + SecurityElement.Escape(Lang.T("Klasörde göster"))
                + "\" arguments=\"reveal\" activationType=\"foreground\"/></actions>";
        return "<toast><visual><binding template=\"ToastGeneric\">" + texts + "</binding></visual>" + button + "</toast>";
    }

    // Windows 10 can drop, without saying so, the notifications of a program that has no Start menu shortcut,
    // so there the icon next to the clock raises them.
    public static Action<Outcome> Tray; // set by the running app

    public static bool UsesTray(int build) { return build < 22000; } // before Windows 11

    // The true build number: Environment.OSVersion answers 6.2 to a program without a manifest.
    public static int Build
    {
        get
        {
            int build;
            var value = Microsoft.Win32.Registry.GetValue(
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "CurrentBuildNumber", null) as string;
            return int.TryParse(value, out build) ? build : 0;
        }
    }

    // The icon's notification takes at most 255 characters and no empty text.
    public static string TrayText(Outcome outcome)
    {
        string tail = outcome.File != null ? Lang.T("\nKlasörde göstermek için tıkla.") : "";
        string text = string.IsNullOrWhiteSpace(outcome.Text) ? Lang.T("Tamamlandı.") : outcome.Text;
        int room = 255 - tail.Length;
        return (text.Length > room ? text.Substring(0, room - 1) + "…" : text) + tail;
    }

    public static void Show(Outcome outcome)
    {
        if (Tray != null && UsesTray(Build))
        {
            Tray(outcome);
            return;
        }
        try
        {
            var xml = new XmlDocument();
            xml.LoadXml(Xml(outcome));
            var toast = new ToastNotification(xml);
            if (outcome.File != null) toast.Activated += (sender, args) => Reveal(outcome.File);
            recent.Enqueue(toast);
            if (recent.Count > 20) recent.Dequeue();
            ToastNotificationManager.CreateToastNotifier(AppId).Show(toast);
        }
        catch (Exception e) // the result must still reach the user when the notification platform fails
        {
            Log.Error("bildirim gösterilemedi", e);
            System.Windows.Forms.MessageBox.Show(outcome.Text + Lang.T("\n\n(Bildirim gösterilemedi: ") + e.Message + ")", "All In One");
        }
    }

    public static void Reveal(string file)
    {
        try
        {
            string explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            // A file that was moved or deleted meanwhile still opens its folder.
            string arguments = File.Exists(file) ? "/select,\"" + file + "\"" : "\"" + Path.GetDirectoryName(file) + "\"";
            Process.Start(explorer, arguments);
        }
        catch (Exception e) // runs on a system thread: an escaping error would end the app
        {
            System.Windows.Forms.MessageBox.Show(e.Message, "All In One");
        }
    }
}

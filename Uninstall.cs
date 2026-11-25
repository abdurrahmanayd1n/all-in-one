// Removes what the app has written on this computer. The exe itself is the user's to delete: a program
// that deletes its own file looks like malware to security software.
using System;
using System.IO;
using System.Linq;
using System.Windows;
using Microsoft.Win32;
using Path = System.IO.Path;

static class Uninstall
{
    public static Action Quit = () => { }; // set by the running app

    // Registry paths are under HKEY_CURRENT_USER; identity is the notification name's key.
    public static void Remove(string identity)
    {
        Prefs.Autostart = false; // takes the startup entry away
        using (var approved = Registry.CurrentUser.OpenSubKey(Prefs.Approved, true))
            if (approved != null) approved.DeleteValue("AllInOne", false);
        Registry.CurrentUser.DeleteSubKeyTree(Prefs.Store, false);
        Registry.CurrentUser.DeleteSubKeyTree(identity, false);
        Log.Off = true; // nothing may be written again on the way out
        // Only the app's own files: the folder may hold the installed exe, or something of the user's.
        foreach (var file in new[] { Log.File, Log.Older, Path.Combine(Log.Folder, "app.ico") }) File.Delete(file);
        if (Directory.Exists(Log.Folder) && !Directory.EnumerateFileSystemEntries(Log.Folder).Any())
            Directory.Delete(Log.Folder);
    }

    // From the settings window.
    public static void Ask()
    {
        var answer = MessageBox.Show("Ayarlar, hazır ayarlar, renk geçmişi, hata kaydı, bildirim kaydı ve otomatik başlatma"
            + " silinecek, uygulama kapanacak. Dosyaların silinmez.\n\nKaldırılsın mı?", "All In One - kaldır",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;
        try { Remove(Toast.IdentityKey); }
        catch (Exception e)
        {
            Log.Off = false;
            Log.Error("kaldırma", e);
            MessageBox.Show("Tamamı kaldırılamadı: " + e.Message, "All In One");
            return;
        }
        string exe = System.Windows.Forms.Application.ExecutablePath;
        MessageBox.Show("Kaldırıldı. Son adım: açılan klasörde seçili duran dosyayı sil.\n\n" + exe, "All In One");
        System.Diagnostics.Process.Start(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "explorer.exe"), "/select,\"" + exe + "\"");
        Quit();
    }
}

// Keeps a window above the others until it is released again.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

static class Pinner
{
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr window, StringBuilder text, int max);
    [StructLayout(LayoutKind.Sequential)] struct Edges { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr window, out Edges edges);

    const int ExtendedStyle = -20, TopmostStyle = 0x8;
    const uint KeepPlace = 0x1 | 0x2 | 0x10; // no size, no move, no activation

    // The windows pinned here, so that they can be listed and released.
    static readonly List<IntPtr> pinned = new List<IntPtr>();

    public static bool IsOnTop(IntPtr window)
    {
        return (Native.GetWindowLong(window, ExtendedStyle) & TopmostStyle) != 0;
    }

    public static string Title(IntPtr window)
    {
        var text = new StringBuilder(200);
        GetWindowText(window, text, text.Capacity);
        string title = text.ToString().Trim();
        return title == "" ? Lang.T("(adsız pencere)") : title.Length > 60 ? title.Substring(0, 60) + "…" : title;
    }

    // Still open and still on top.
    public static IntPtr[] Pinned()
    {
        pinned.RemoveAll(w => !IsWindow(w) || !IsOnTop(w));
        return pinned.ToArray();
    }

    // The desktop and the taskbar are not windows to pin.
    public static bool Pinnable(IntPtr window)
    {
        if (window == IntPtr.Zero || !IsWindow(window)) return false;
        var cls = new StringBuilder(64);
        Native.GetClassName(window, cls, cls.Capacity);
        return !new[] { "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd" }.Contains(cls.ToString());
    }

    // Pins a window or releases it; returns what to tell, null when there is nothing to do.
    public static string Toggle(IntPtr window)
    {
        if (!Pinnable(window)) return null;
        bool release = IsOnTop(window);
        bool done = Native.SetWindowPos(window, new IntPtr(release ? -2 : -1), 0, 0, 0, 0, KeepPlace)
            && IsOnTop(window) != release;
        if (!done) return Lang.T("Bu pencere değiştirilemiyor (yönetici olarak çalışıyor olabilir).");
        pinned.Remove(window);
        if (!release) pinned.Add(window);
        return (release ? Lang.T("Sabitleme kaldırıldı: ") : Lang.T("Üstte sabitlendi: ")) + Title(window);
    }

    // The shortcut: acts on the window in front and says so at its top edge.
    public static void ToggleFront()
    {
        IntPtr window = Native.GetForegroundWindow();
        string report = Toggle(window);
        if (report == null) return;
        Edges e;
        GetWindowRect(window, out e);
        Note.Show(report, new Point((e.Left + e.Right) / 2, Math.Max(0, e.Top) + 40));
    }

    public static void ReleaseAll()
    {
        foreach (var window in Pinned()) Toggle(window);
    }
}

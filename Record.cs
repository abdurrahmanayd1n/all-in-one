// Recording a part of the screen: FFmpeg takes the picture, SoundTrack the sound, and the two are put together
// when it ends. A small bar shows the time and ends it, as does Alt+S.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Path = System.IO.Path;

// A coloured frame around a part of the screen and a bar with a line of text and one button, below the part or
// above it. Neither appears in pictures of the screen.
class Marker : IDisposable
{
    [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr window, uint affinity);

    readonly List<Form> windows = new List<Form>();
    readonly Label line;

    public Marker(Rectangle area, Color colour, string button, Action pressed)
    {
        IntPtr awareness = Native.SetThreadDpiAwarenessContext(new IntPtr(-4));
        try
        {
            double scale = Snip.ScaleAt(new Point(area.X + area.Width / 2, area.Y + area.Height / 2));
            int edge = Math.Max(2, (int)(2 * scale));
            foreach (var side in new[]
            {
                new Rectangle(area.X - edge, area.Y - edge, area.Width + 2 * edge, edge),
                new Rectangle(area.X - edge, area.Bottom, area.Width + 2 * edge, edge),
                new Rectangle(area.X - edge, area.Y, edge, area.Height), new Rectangle(area.Right, area.Y, edge, area.Height)
            })
                windows.Add(Floating(side, colour));

            var screen = Screen.FromRectangle(area).WorkingArea;
            int w = (int)(250 * scale), h = (int)(44 * scale), gap = (int)(10 * scale);
            int x = Math.Max(screen.Left, Math.Min(area.X + (area.Width - w) / 2, screen.Right - w));
            // Below the part, else above it, else inside at the bottom.
            int y = area.Bottom + gap + h <= screen.Bottom ? area.Bottom + gap
                : area.Y - gap - h >= screen.Top ? area.Y - gap - h : area.Bottom - gap - h;
            var bar = Floating(new Rectangle(x, y, w, h), Color.FromArgb(27, 28, 35));
            line = new Label { AutoSize = false, Bounds = new Rectangle(gap, 0, (int)(135 * scale), h), ForeColor = Color.White,
                Font = new Font("Segoe UI", (float)(14 * scale), GraphicsUnit.Pixel), TextAlign = ContentAlignment.MiddleLeft };
            var stop = new Button { Text = button, FlatStyle = FlatStyle.Flat, BackColor = colour, ForeColor = Color.White,
                Font = line.Font, Bounds = new Rectangle(w - (int)(94 * scale) - 6, 6, (int)(94 * scale), h - 12), Cursor = Cursors.Hand };
            stop.FlatAppearance.BorderSize = 0;
            stop.Click += (s, e) => pressed();
            bar.Controls.Add(line);
            bar.Controls.Add(stop);
            windows.Add(bar);
        }
        finally { Native.SetThreadDpiAwarenessContext(awareness); }
    }

    public string Text { set { line.Text = value; } }

    class Quiet : Form
    {
        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                var all = base.CreateParams;
                all.ExStyle |= 0x08000000 | 0x80; // never active, not in Alt+Tab
                return all;
            }
        }
    }

    static Form Floating(Rectangle bounds, Color colour)
    {
        var form = new Quiet { FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual, ShowInTaskbar = false,
            TopMost = true, AutoScaleMode = AutoScaleMode.None, BackColor = colour, MinimumSize = new Size(1, 1) };
        form.HandleCreated += (s, e) => SetWindowDisplayAffinity(form.Handle, 0x11); // left out of captures
        form.Show();
        form.Bounds = bounds;
        return form;
    }

    public void Dispose()
    {
        foreach (var window in windows) window.Close();
        windows.Clear();
    }
}

static class Recorder
{
    static Process process;
    static string file;
    static readonly StringBuilder errors = new StringBuilder();
    static readonly List<SoundTrack> sounds = new List<SoundTrack>();
    static Marker marker;
    static Timer tick;
    static double began; // when the first picture was taken, in seconds since 1970; 0: not told

    public static bool Running { get { return process != null; } }

    // What is recorded along with the picture.
    public static bool WithSound
    {
        get { return Prefs.Number("RecordSound", 1) != 0; }
        set { Prefs.SetNumber("RecordSound", value ? 1 : 0); }
    }

    public static bool WithMicrophone
    {
        get { return Prefs.Number("RecordMic", 0) != 0; }
        set { Prefs.SetNumber("RecordMic", value ? 1 : 0); }
    }

    // The encoder wants even sides.
    public static Rectangle Even(Rectangle area)
    {
        return new Rectangle(area.X, area.Y, area.Width / 2 * 2, area.Height / 2 * 2);
    }

    public static string Arguments(Rectangle area, string output)
    {
        // "info" makes FFmpeg tell when the capture began.
        return "-hide_banner -loglevel info -nostats -y -f gdigrab -framerate 30 -draw_mouse 1 -offset_x " + area.X + " -offset_y "
            + area.Y + " -video_size " + area.Width + "x" + area.Height + " -i desktop -c:v libx264 -preset veryfast -crf 23"
            + " -pix_fmt yuv420p -movflags +faststart \"" + output + "\"";
    }

    // The moment named in the line "Duration: N/A, start: 1795679292.144457, …"; 0 in any other line.
    public static double CaptureStart(string line)
    {
        var found = System.Text.RegularExpressions.Regex.Match(line, @"Duration: N/A, start: (\d{9,}\.\d+)");
        return found.Success ? double.Parse(found.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
    }

    // Puts the sound files beside the finished picture. skip: seconds each began before the picture did.
    public static string Mixing(string video, string[] tracks, double[] skip, double seconds, string output)
    {
        Func<double, string> number = v => v.ToString("0.###", CultureInfo.InvariantCulture);
        var a = new StringBuilder("-hide_banner -nostdin -loglevel error -y -i \"" + video + "\"");
        for (int i = 0; i < tracks.Length; i++)
            a.Append((skip[i] > 0 ? " -ss " + number(skip[i]) : "") + " -i \"" + tracks[i] + "\"");
        a.Append(tracks.Length == 1 ? " -map 0:v -map 1:a"
            : " -filter_complex \"" + string.Concat(Enumerable.Range(1, tracks.Length).Select(i => "[" + i + ":a]"))
                + "amix=inputs=" + tracks.Length + ":duration=longest:normalize=0,alimiter=limit=0.95[a]\" -map 0:v -map \"[a]\"");
        a.Append(" -c:v copy -c:a aac -b:a 192k" + (seconds > 0 ? " -t " + number(seconds) : "") + " -movflags +faststart \""
            + output + "\"");
        return a.ToString();
    }

    public static void Start(Rectangle area)
    {
        if (Running) return;
        area = Even(area);
        var refused = new List<string>();
        try
        {
            errors.Length = 0;
            began = 0;
            file = Captures.Free(Captures.Videos, Lang.T("Kayıt"), ".mp4");
            // The sound starts first: what it has too much at the beginning is cut off afterwards.
            if (WithSound) Listen(false, Lang.T("bilgisayar sesi"), refused);
            if (WithMicrophone) Listen(true, Lang.T("mikrofon"), refused);
            process = new Process { StartInfo = new ProcessStartInfo(Images.Ffmpeg, Arguments(area, file))
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardError = true } };
            process.ErrorDataReceived += (s, e) =>
            {
                if (e.Data == null) return;
                if (began == 0) began = CaptureStart(e.Data);
                if (errors.Length > 600) errors.Remove(0, errors.Length - 300); // what went wrong is said last
                errors.AppendLine(e.Data);
            };
            process.Start();
            process.BeginErrorReadLine();
        }
        catch (Exception e)
        {
            process = null;
            Discard(sounds.ToArray());
            sounds.Clear();
            Log.Error("ekran kaydı başlatılamadı", e);
            MessageBox.Show(Lang.T("Ekran kaydı başlatılamadı: ") + e.Message, "All In One");
            return;
        }
        marker = new Marker(area, Color.FromArgb(255, 69, 58), Lang.T("■  Durdur"), () => Stop());
        var clock = Stopwatch.StartNew();
        string heard = (sounds.Count > 0 ? "  ♪" : "") + (refused.Count > 0 ? "  " + Lang.T("ses yok!") : "");
        Action show = () => marker.Text = "●  " + Video.Clock(clock.Elapsed.TotalSeconds) + heard;
        show();
        tick = new Timer { Interval = 250 };
        tick.Tick += (s, e) =>
        {
            show();
            if (process != null && process.HasExited) Stop(); // it failed: say why
        };
        tick.Start();
        if (refused.Count > 0)
            Images.Notify(new Outcome { Text = Lang.T("Kayıt sürüyor, ama şu alınamıyor: ") + string.Join(", ", refused) });
    }

    static void Listen(bool microphone, string name, List<string> refused)
    {
        try
        {
            sounds.Add(new SoundTrack(microphone, Path.Combine(Path.GetTempPath(), "AllInOne-" + Guid.NewGuid().ToString("N") + ".wav")));
        }
        catch (IOException) { refused.Add(name); } // the picture is still recorded
    }

    static void Discard(SoundTrack[] tracks)
    {
        foreach (var track in tracks)
        {
            track.Dispose();
            try { File.Delete(track.File); }
            catch (IOException) { }
        }
    }

    // Asks FFmpeg to finish the file, then says where it is. wait: before the app ends.
    public static void Stop(bool wait = false)
    {
        if (!Running) return;
        var ending = process;
        string made = file;
        var tracks = sounds.ToArray();
        sounds.Clear();
        process = null;
        if (tick != null) tick.Dispose();
        if (marker != null) marker.Dispose();
        marker = null;
        Func<Outcome> finish = () => Finish(ending, made, tracks);
        if (wait) finish();
        else Images.Start(finish);
    }

    static Outcome Finish(Process ending, string made, SoundTrack[] tracks)
    {
        try
        {
            using (ending)
            {
                try
                {
                    ending.StandardInput.Write('q');
                    ending.StandardInput.Flush();
                }
                catch (IOException) { } // it has ended by itself
                foreach (var track in tracks) track.Signal(); // picture and sound end together
                if (!ending.WaitForExit(15000)) ending.Kill();
                ending.WaitForExit();
                var info = new FileInfo(made);
                if (ending.ExitCode != 0 || !info.Exists || info.Length == 0)
                {
                    if (info.Exists && info.Length == 0) info.Delete();
                    throw new IOException(Lang.T("Ekran kaydedilemedi: ") + errors.ToString().Trim());
                }
            }
            string note = "";
            if (tracks.Length > 0)
            {
                foreach (var track in tracks) track.Dispose();
                var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                var early = began == 0 ? null
                    : tracks.Select(t => Math.Max(0, began - (t.Started - epoch).TotalSeconds)).ToArray();
                try { AddSound(made, tracks.Select(t => t.File).ToArray(), early); }
                catch (Exception e)
                {
                    Log.Error("kayda ses eklenemedi", e);
                    note = " · " + Lang.T("ses eklenemedi");
                }
            }
            long size = new FileInfo(made).Length;
            return new Outcome { File = made, Bytes = size, Text = Path.GetFileName(made) + "\n" + Images.Size(size) + note };
        }
        finally { Discard(tracks); }
    }

    // Replaces the silent video by one with the sound; the silent one stays if that fails.
    // early: how long each sound began before the picture; null: judged by their lengths.
    public static void AddSound(string video, string[] tracks, double[] early = null)
    {
        double seconds = Video.Available ? Video.Probe(video).Seconds : 0;
        // Failing the starting times: both ended together, so what a sound has more than the picture came before it.
        var skip = early ?? tracks.Select(t => seconds <= 0 ? 0
            : Math.Max(0, (new FileInfo(t).Length - 44) / (double)(SoundTrack.Rate * SoundTrack.FrameBytes) - seconds)).ToArray();
        string joined = Path.Combine(Path.GetDirectoryName(video), "~aio-" + Guid.NewGuid().ToString("N") + ".mp4");
        try
        {
            var errors = new StringBuilder();
            using (var mixer = new Process { StartInfo = new ProcessStartInfo(Images.Ffmpeg,
                Mixing(video, tracks, skip, seconds, joined)) { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardError = true } })
            {
                mixer.Start();
                errors.Append(mixer.StandardError.ReadToEnd());
                mixer.WaitForExit();
                if (mixer.ExitCode != 0 || !File.Exists(joined)) throw new IOException(errors.ToString().Trim());
            }
            File.Replace(joined, video, null);
        }
        finally { File.Delete(joined); }
    }
}

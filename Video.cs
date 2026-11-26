// Video tools through FFmpeg: compress, make a GIF, take the sound out, cut a part out, join several. Originals
// are never modified or overwritten.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Media.Imaging;
using Path = System.IO.Path;

enum VideoTask { Compress, Gif, Audio, Trim, Merge }

class VideoJob
{
    public VideoTask Task;
    // Compress
    public int Crf = 28, TargetMb, ShortSide; // TargetMb 0: by quality; ShortSide 0: as it is
    public bool Mute;
    // Gif
    public double Start, Length = 5; // seconds; Length 0: to the end
    public int Width = 480, Rate = 15; // Width 0: as it is
    // Audio
    public string AudioExt = ".mp3";
    // Trim uses Start and this; 0: to the end. Exact: encoded anew, so that it begins on the very frame asked for.
    public double End;
    public bool Exact;

    public string Extension { get { return Task == VideoTask.Gif ? ".gif" : Task == VideoTask.Audio ? AudioExt : ".mp4"; } }

    // A part copied without encoding stays in the kind of file it came from.
    public string ExtensionFor(string input)
    {
        return Task == VideoTask.Trim && !Exact ? Path.GetExtension(input).ToLowerInvariant() : Extension;
    }

    public string Suffix
    {
        get
        {
            return Task == VideoTask.Compress ? "_sikistirilmis" : Task == VideoTask.Gif ? "_gif" : Task == VideoTask.Audio ? "_ses"
                : Task == VideoTask.Trim ? "_kesit" : "_birlestirilmis";
        }
    }
}

class VideoInfo
{
    public double Seconds;
    public int Width, Height; // as shown, so a phone's rotation is already counted
    public bool Turned; // the file carries a rotation
    public string VideoCodec, AudioCodec; // null: there is none

    public string Describe
    {
        get
        {
            return (VideoCodec == null ? "" : Width + " × " + Height + " · ") + Video.Clock(Seconds)
                + " · " + (VideoCodec ?? Lang.T("görüntü yok")) + (AudioCodec == null ? Lang.T(" · sessiz") : " / " + AudioCodec);
        }
    }
}

static class Video
{
    static readonly string[] Types = { ".mp4", ".mkv", ".mov", ".avi", ".webm", ".m4v", ".wmv", ".flv", ".mpg", ".mpeg",
        ".mts", ".m2ts", ".3gp" };

    public static readonly string Ffprobe = Images.Ffmpeg == null ? null
        : new[] { Path.Combine(Path.GetDirectoryName(Images.Ffmpeg), "ffprobe.exe") }.FirstOrDefault(File.Exists);

    public static bool Available { get { return Ffprobe != null; } }

    public static bool IsVideo(string path)
    {
        return path != null && File.Exists(path) && Types.Contains(Path.GetExtension(path).ToLowerInvariant());
    }

    static string Number(double value)
    {
        return value.ToString("0.###", CultureInfo.InvariantCulture);
    }

    // Seconds from "75", "1:15" or "1:02:03,5"; null when it is not a time.
    public static double? Time(string text)
    {
        var parts = (text ?? "").Trim().Replace(',', '.').Split(':');
        if (parts.Length > 3) return null;
        double total = 0;
        foreach (var part in parts)
        {
            double value;
            if (!double.TryParse(part, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value)) return null;
            total = total * 60 + value;
        }
        return total;
    }

    public static string Clock(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }

    static ProcessStartInfo Command(string exe, string arguments)
    {
        return new ProcessStartInfo(exe, arguments)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
    }

    // Runs a tool to its end and returns what it printed.
    static string Ask(string arguments)
    {
        using (var process = Process.Start(Command(Ffprobe, arguments)))
        {
            var errors = process.StandardError.ReadToEndAsync();
            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0) throw new IOException(Lang.T("Video okunamadı: ") + errors.Result.Trim());
            return output;
        }
    }

    public static VideoInfo Probe(string path)
    {
        if (!Available) throw new NotSupportedException(Lang.T("Video için FFmpeg bulunamadı."));
        var info = new VideoInfo();
        string video = Ask("-v error -select_streams V:0 -show_entries stream=codec_name,width,height:stream_side_data=rotation"
            + ":format=duration -of default=noprint_wrappers=1 \"" + path + "\"");
        int rotation = 0;
        foreach (var line in video.Split('\n').Select(l => l.Trim().Split('=')).Where(l => l.Length == 2))
        {
            if (line[0] == "codec_name") info.VideoCodec = line[1];
            else if (line[0] == "width") int.TryParse(line[1], out info.Width);
            else if (line[0] == "height") int.TryParse(line[1], out info.Height);
            else if (line[0] == "rotation") int.TryParse(line[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out rotation);
            else if (line[0] == "duration") double.TryParse(line[1], NumberStyles.Float, CultureInfo.InvariantCulture, out info.Seconds);
        }
        info.Turned = rotation % 360 != 0;
        if (Math.Abs(rotation) % 180 == 90) { int w = info.Width; info.Width = info.Height; info.Height = w; }
        string audio = Ask("-v error -select_streams a:0 -show_entries stream=codec_name -of default=noprint_wrappers=1:nokey=1 \""
            + path + "\"").Trim();
        info.AudioCodec = audio == "" ? null : audio.Split('\n')[0].Trim();
        return info;
    }

    // One picture of the video, for the preview; null when none can be taken there.
    public static BitmapSource Frame(string path, double seconds)
    {
        var start = Command(Images.Ffmpeg, "-nostdin -v error -ss " + Number(seconds) + " -i \"" + path
            + "\" -map 0:V:0 -frames:v 1 -vf \"scale='min(1280,iw)':-2\" -f image2pipe -c:v png pipe:1");
        start.StandardOutputEncoding = null;
        using (var process = Process.Start(start))
        {
            var errors = process.StandardError.ReadToEndAsync();
            var memory = new MemoryStream();
            process.StandardOutput.BaseStream.CopyTo(memory);
            process.WaitForExit();
            errors.Wait();
            return memory.Length == 0 ? null : Images.Decode(memory.ToArray(), ".png");
        }
    }

    // How long the part that gets processed is.
    public static double Span(VideoJob job, VideoInfo info)
    {
        if (job.Task == VideoTask.Trim)
            return Math.Max(0, (job.End > 0 && (info.Seconds <= 0 || job.End < info.Seconds) ? job.End : info.Seconds) - job.Start);
        if (job.Task != VideoTask.Gif) return info.Seconds;
        double rest = Math.Max(0, info.Seconds - job.Start);
        return job.Length > 0 && (info.Seconds <= 0 || job.Length < rest) ? job.Length : rest;
    }

    // The picture and sound bit rates, in kbit/s, that keep a video under the target size.
    public static void Rates(int targetMb, double seconds, bool sound, out int picture, out int audio)
    {
        if (seconds <= 0) throw new NotSupportedException(Lang.T("Videonun süresi okunamadığı için hedef boyut hesaplanamıyor."));
        double total = targetMb * 8192.0 * 0.95 / seconds; // 5% for the container and the encoder's drift
        audio = !sound ? 0 : total > 600 ? 128 : 64;
        picture = (int)Math.Min(100000, total - audio);
        if (picture < 50)
            throw new NotSupportedException(Lang.T("Bu uzunlukta video için hedef çok küçük; en az ")
                + Math.Ceiling((50 + audio) * seconds / (8192 * 0.95)) + Lang.T(" MB gerekir."));
    }

    // The FFmpeg arguments of one pass; pass 0 means a single pass.
    public static string Arguments(VideoJob job, VideoInfo info, string input, string output, int pass, string log)
    {
        var a = new StringBuilder("-hide_banner -nostdin -v error -y -progress pipe:1 -nostats");
        if ((job.Task == VideoTask.Gif || job.Task == VideoTask.Trim) && job.Start > 0) a.Append(" -ss " + Number(job.Start));
        a.Append(" -i \"" + input + "\"");
        if (job.Task == VideoTask.Trim)
        {
            if (job.End > 0) a.Append(" -t " + Number(Math.Max(0, job.End - job.Start)));
            // Copying is instant and loses nothing, but can only begin at a key frame, so a little earlier.
            a.Append(" -map 0:V:0? -map 0:a? -sn -dn");
            a.Append(job.Exact ? " -c:v libx264 -preset medium -crf 18 -pix_fmt yuv420p -vf \"scale=trunc(iw/2)*2:trunc(ih/2)*2\""
                + " -c:a aac -b:a 192k -movflags +faststart" : " -c copy -avoid_negative_ts make_zero");
        }
        else if (job.Task == VideoTask.Gif)
        {
            if (job.Length > 0) a.Append(" -t " + Number(job.Length));
            string width = job.Width > 0 ? "scale='min(" + job.Width + ",iw)':-1:flags=lanczos," : "";
            a.Append(" -map 0:V:0 -an -vf \"fps=" + job.Rate + "," + width
                + "split[a][b];[a]palettegen=stats_mode=diff[p];[b][p]paletteuse=dither=bayer\" -loop 0");
        }
        else if (job.Task == VideoTask.Audio)
        {
            a.Append(" -map 0:a:0 -vn -sn");
            a.Append(job.AudioExt == ".mp3" ? " -c:a libmp3lame -q:a 2" : job.AudioExt == ".wav" ? " -c:a pcm_s16le"
                : info.AudioCodec == "aac" ? " -c:a copy" : " -c:a aac -b:a 192k");
        }
        else
        {
            // H.264 needs even sides; the short side is only ever reduced.
            string n = job.ShortSide.ToString();
            string scale = job.ShortSide == 0 ? "scale=trunc(iw/2)*2:trunc(ih/2)*2"
                : "scale=w='if(gt(iw,ih),-2,trunc(min(" + n + ",iw)/2)*2)':h='if(gt(iw,ih),trunc(min(" + n + ",ih)/2)*2,-2)'";
            a.Append(" -map 0:V:0 -sn -c:v libx264 -preset medium -pix_fmt yuv420p -vf \"" + scale + "\"");
            bool sound = !job.Mute && info.AudioCodec != null;
            int picture = 0, audio = 128;
            if (job.TargetMb > 0)
            {
                Rates(job.TargetMb, info.Seconds, sound, out picture, out audio);
                // The cap holds the size: without it grainy video ran to four times the target.
                a.Append(" -b:v " + picture + "k -maxrate " + picture + "k -bufsize " + 2 * picture + "k -pass " + pass
                    + " -passlogfile \"" + log + "\"");
            }
            else a.Append(" -crf " + job.Crf);
            if (pass == 1) return a.Append(" -an -f null NUL").ToString(); // the first pass only measures
            a.Append(sound ? " -map 0:a:0 -c:a aac -b:a " + audio + "k" : " -an");
            a.Append(" -movflags +faststart");
        }
        return a.Append(" \"" + output + "\"").ToString();
    }

    static void Encode(string arguments, double seconds, Action<double> progress, Func<bool> cancelled)
    {
        var errors = new StringBuilder();
        using (var process = new Process { StartInfo = Command(Images.Ffmpeg, arguments) })
        {
            process.OutputDataReceived += (s, e) =>
            {
                long done;
                if (e.Data != null && e.Data.StartsWith("out_time_us=") && long.TryParse(e.Data.Substring(12), out done) && seconds > 0)
                    progress(Math.Max(0, Math.Min(1, done / 1000000.0 / seconds)));
            };
            process.ErrorDataReceived += (s, e) => { if (e.Data != null && errors.Length < 600) errors.AppendLine(e.Data); };
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            while (!process.WaitForExit(100))
                if (cancelled())
                {
                    try { process.Kill(); }
                    catch (InvalidOperationException) { } // it ended in the same instant
                    process.WaitForExit();
                    throw new OperationCanceledException();
                }
            process.WaitForExit(); // lets the last lines arrive
            if (process.ExitCode != 0) throw new IOException(Lang.T("FFmpeg hata verdi: ") + errors.ToString().Trim());
        }
    }

    // Runs the job on one file. The result is written under a temporary name and renamed when it is complete.
    public static Outcome Run(VideoJob job, string input, string name, Action<double> progress, Func<bool> cancelled)
    {
        string problem = Naming.Problem(name);
        if (problem != null) throw new ArgumentException(problem);
        var info = Probe(input);
        if (job.Task == VideoTask.Audio && info.AudioCodec == null) return new Outcome { Text = Lang.T("Bu videoda ses yok.") };
        if (job.Task != VideoTask.Audio && info.VideoCodec == null) throw new NotSupportedException(Lang.T("Dosyada görüntü bulunamadı."));
        long before = new FileInfo(input).Length;
        bool targeted = job.Task == VideoTask.Compress && job.TargetMb > 0;
        if (targeted && before <= job.TargetMb * 1048576L) return new Outcome { Text = Lang.T("Dosya zaten ") + job.TargetMb + Lang.T(" MB altında.") };
        if ((job.Task == VideoTask.Gif || job.Task == VideoTask.Trim) && info.Seconds > 0 && job.Start >= info.Seconds)
            throw new ArgumentException(Lang.T("Başlangıç videonun süresinden (") + Clock(info.Seconds) + Lang.T(") sonra."));
        if (job.Task == VideoTask.Trim && job.End > 0 && job.End <= job.Start)
            throw new ArgumentException(Lang.T("Bitiş başlangıçtan sonra olmalı."));

        string folder = Path.GetDirectoryName(input), id = Guid.NewGuid().ToString("N"), ext = job.ExtensionFor(input);
        string temp = Path.Combine(folder, "~aio-" + id + ext), log = Path.Combine(Path.GetTempPath(), "AllInOne-" + id);
        try
        {
            double span = Span(job, info);
            if (targeted)
            {
                Encode(Arguments(job, info, input, temp, 1, log), span, p => progress(p / 2), cancelled);
                Encode(Arguments(job, info, input, temp, 2, log), span, p => progress(0.5 + p / 2), cancelled);
            }
            else Encode(Arguments(job, info, input, temp, 0, log), span, progress, cancelled);

            long after = new FileInfo(temp).Length;
            if (after == 0) throw new IOException(Lang.T("FFmpeg boş dosya üretti."));
            if (job.Task == VideoTask.Compress && after >= before)
                return new Outcome { Text = Lang.T("Video küçülmediği için kaydedilmedi. Daha düşük kalite veya çözünürlük dene.") };
            string target = Move(temp, folder, name, ext);
            string text = Path.GetFileName(target) + "\n" + (job.Task == VideoTask.Compress
                ? Images.Size(before) + " → " + Images.Size(after) + " · " + Images.Difference(before, after) : Images.Size(after));
            if (targeted && after > job.TargetMb * 1048576L) text += Lang.T(" · hedefi biraz aştı");
            return new Outcome { File = target, Bytes = after, Text = text };
        }
        finally
        {
            File.Delete(temp);
            foreach (string left in new[] { "-0.log", "-0.log.mbtree", "-0.log.temp", "-0.log.mbtree.temp" }) File.Delete(log + left);
        }
    }

    // Videos made alike (same kind of file, picture and sound) are joined as they are; others are encoded anew.
    public static bool Alike(string[] inputs, VideoInfo[] all)
    {
        return inputs.Select(i => Path.GetExtension(i).ToLowerInvariant()).Distinct().Count() == 1
            && all.Select(i => i.VideoCodec + " " + i.AudioCodec + " " + i.Width + "x" + i.Height + " " + i.Turned).Distinct().Count() == 1;
    }

    // The list file that names the videos to be copied one after another.
    public static string List(string[] inputs)
    {
        return string.Concat(inputs.Select(i => "file '" + i.Replace("'", @"'\''") + "'\n"));
    }

    // Every picture is fitted into the first one's size and every sound into one form; a video without sound
    // gets silence.
    public static string Joining(string[] inputs, VideoInfo[] all, string output)
    {
        var a = new StringBuilder("-hide_banner -nostdin -v error -y -progress pipe:1 -nostats");
        foreach (var input in inputs) a.Append(" -i \"" + input + "\"");
        bool sound = all.Any(i => i.AudioCodec != null);
        var silent = new int[all.Length]; // the extra input that stands in for a missing sound
        for (int i = 0, next = all.Length; i < all.Length; i++)
            if (sound && all[i].AudioCodec == null)
            {
                a.Append(" -f lavfi -t " + Number(all[i].Seconds) + " -i anullsrc=r=48000:cl=stereo");
                silent[i] = next++;
            }
        string w = (all[0].Width / 2 * 2).ToString(), h = (all[0].Height / 2 * 2).ToString();
        var graph = new StringBuilder();
        var joined = new StringBuilder();
        for (int i = 0; i < all.Length; i++)
        {
            graph.Append("[" + i + ":V:0]scale=" + w + ":" + h + ":force_original_aspect_ratio=decrease,pad=" + w + ":" + h
                + ":(ow-iw)/2:(oh-ih)/2,setsar=1,fps=30,format=yuv420p[v" + i + "];");
            joined.Append("[v" + i + "]");
            if (!sound) continue;
            graph.Append("[" + (all[i].AudioCodec == null ? silent[i] : i) + ":a:0]aresample=48000,aformat=channel_layouts=stereo[a"
                + i + "];");
            joined.Append("[a" + i + "]");
        }
        graph.Append(joined + "concat=n=" + all.Length + ":v=1:a=" + (sound ? "1[v][a]" : "0[v]"));
        a.Append(" -filter_complex \"" + graph + "\" -map \"[v]\"" + (sound ? " -map \"[a]\" -c:a aac -b:a 192k" : ""));
        return a.Append(" -c:v libx264 -preset medium -crf 20 -movflags +faststart \"" + output + "\"").ToString();
    }

    // Joins the videos in this order into one new file next to the first.
    public static Outcome Merge(string[] inputs, string name, Action<double> progress, Func<bool> cancelled)
    {
        string problem = Naming.Problem(name);
        if (problem != null) throw new ArgumentException(problem);
        if (inputs.Length < 2) throw new ArgumentException(Lang.T("Birleştirmek için en az iki video gerekli."));
        var all = inputs.Select(Probe).ToArray();
        if (all.Any(i => i.VideoCodec == null)) throw new NotSupportedException(Lang.T("Dosyada görüntü bulunamadı."));
        bool copy = Alike(inputs, all);
        string folder = Path.GetDirectoryName(inputs[0]), id = Guid.NewGuid().ToString("N");
        string ext = copy ? Path.GetExtension(inputs[0]).ToLowerInvariant() : ".mp4";
        string temp = Path.Combine(folder, "~aio-" + id + ext), list = Path.Combine(Path.GetTempPath(), "AllInOne-" + id + ".txt");
        try
        {
            double seconds = all.Sum(i => i.Seconds);
            if (copy)
            {
                File.WriteAllText(list, List(inputs), new UTF8Encoding(false));
                Encode("-hide_banner -nostdin -v error -y -progress pipe:1 -nostats -f concat -safe 0 -i \"" + list
                    + "\" -map 0:V:0 -map 0:a? -c copy \"" + temp + "\"", seconds, progress, cancelled);
            }
            else Encode(Joining(inputs, all, temp), seconds, progress, cancelled);
            long after = new FileInfo(temp).Length;
            if (after == 0) throw new IOException(Lang.T("FFmpeg boş dosya üretti."));
            string target = Move(temp, folder, name, ext);
            return new Outcome { File = target, Bytes = after, Text = Path.GetFileName(target) + "\n" + inputs.Length
                + Lang.T(" video · ") + Images.Size(after) + (copy ? Lang.T(" · kalite kaybı yok") : "") };
        }
        finally
        {
            File.Delete(temp);
            File.Delete(list);
        }
    }

    // Renames the finished file to a free name: an existing one is never overwritten.
    static string Move(string temp, string folder, string name, string ext)
    {
        for (int i = 1; ; i++)
        {
            string target = Path.Combine(folder, name + (i == 1 ? "" : " (" + i + ")") + ext);
            if (File.Exists(target) || Directory.Exists(target)) continue;
            try
            {
                File.Move(temp, target);
                return target;
            }
            catch (IOException) { if (!File.Exists(target)) throw; }
        }
    }
}

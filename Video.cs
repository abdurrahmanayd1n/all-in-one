// Video tools through FFmpeg: compress, make a GIF, take the sound out. Originals are never modified or overwritten.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Media.Imaging;
using Path = System.IO.Path;

enum VideoTask { Compress, Gif, Audio }

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

    public string Extension { get { return Task == VideoTask.Compress ? ".mp4" : Task == VideoTask.Gif ? ".gif" : AudioExt; } }

    public string Suffix { get { return Task == VideoTask.Compress ? "_sikistirilmis" : Task == VideoTask.Gif ? "_gif" : "_ses"; } }
}

class VideoInfo
{
    public double Seconds;
    public int Width, Height; // as shown, so a phone's rotation is already counted
    public string VideoCodec, AudioCodec; // null: there is none

    public string Describe
    {
        get
        {
            return (VideoCodec == null ? "" : Width + " × " + Height + " · ") + Video.Clock(Seconds)
                + " · " + (VideoCodec ?? "görüntü yok") + (AudioCodec == null ? " · sessiz" : " / " + AudioCodec);
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
            if (process.ExitCode != 0) throw new IOException("Video okunamadı: " + errors.Result.Trim());
            return output;
        }
    }

    public static VideoInfo Probe(string path)
    {
        if (!Available) throw new NotSupportedException("Video için FFmpeg bulunamadı.");
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
        if (job.Task != VideoTask.Gif) return info.Seconds;
        double rest = Math.Max(0, info.Seconds - job.Start);
        return job.Length > 0 && (info.Seconds <= 0 || job.Length < rest) ? job.Length : rest;
    }

    // The picture and sound bit rates, in kbit/s, that keep a video under the target size.
    public static void Rates(int targetMb, double seconds, bool sound, out int picture, out int audio)
    {
        if (seconds <= 0) throw new NotSupportedException("Videonun süresi okunamadığı için hedef boyut hesaplanamıyor.");
        double total = targetMb * 8192.0 * 0.95 / seconds; // 5% for the container and the encoder's drift
        audio = !sound ? 0 : total > 600 ? 128 : 64;
        picture = (int)Math.Min(100000, total - audio);
        if (picture < 50)
            throw new NotSupportedException("Bu uzunlukta video için hedef çok küçük; en az "
                + Math.Ceiling((50 + audio) * seconds / (8192 * 0.95)) + " MB gerekir.");
    }

    // The FFmpeg arguments of one pass; pass 0 means a single pass.
    public static string Arguments(VideoJob job, VideoInfo info, string input, string output, int pass, string log)
    {
        var a = new StringBuilder("-hide_banner -nostdin -v error -y -progress pipe:1 -nostats");
        if (job.Task == VideoTask.Gif && job.Start > 0) a.Append(" -ss " + Number(job.Start));
        a.Append(" -i \"" + input + "\"");
        if (job.Task == VideoTask.Gif)
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
            if (process.ExitCode != 0) throw new IOException("FFmpeg hata verdi: " + errors.ToString().Trim());
        }
    }

    // Runs the job on one file. The result is written under a temporary name and renamed when it is complete.
    public static Outcome Run(VideoJob job, string input, string name, Action<double> progress, Func<bool> cancelled)
    {
        string problem = Naming.Problem(name);
        if (problem != null) throw new ArgumentException(problem);
        var info = Probe(input);
        if (job.Task == VideoTask.Audio && info.AudioCodec == null) return new Outcome { Text = "Bu videoda ses yok." };
        if (job.Task != VideoTask.Audio && info.VideoCodec == null) throw new NotSupportedException("Dosyada görüntü bulunamadı.");
        long before = new FileInfo(input).Length;
        bool targeted = job.Task == VideoTask.Compress && job.TargetMb > 0;
        if (targeted && before <= job.TargetMb * 1048576L) return new Outcome { Text = "Dosya zaten " + job.TargetMb + " MB altında." };
        if (job.Task == VideoTask.Gif && info.Seconds > 0 && job.Start >= info.Seconds)
            throw new ArgumentException("Başlangıç videonun süresinden (" + Clock(info.Seconds) + ") sonra.");

        string folder = Path.GetDirectoryName(input), id = Guid.NewGuid().ToString("N");
        string temp = Path.Combine(folder, "~aio-" + id + job.Extension), log = Path.Combine(Path.GetTempPath(), "AllInOne-" + id);
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
            if (after == 0) throw new IOException("FFmpeg boş dosya üretti.");
            if (job.Task == VideoTask.Compress && after >= before)
                return new Outcome { Text = "Video küçülmediği için kaydedilmedi. Daha düşük kalite veya çözünürlük dene." };
            string target = Move(temp, folder, name, job.Extension);
            string text = Path.GetFileName(target) + "\n" + (job.Task == VideoTask.Compress
                ? Images.Size(before) + " → " + Images.Size(after) + " · " + Images.Difference(before, after) : Images.Size(after));
            if (targeted && after > job.TargetMb * 1048576L) text += " · hedefi biraz aştı";
            return new Outcome { File = target, Bytes = after, Text = text };
        }
        finally
        {
            File.Delete(temp);
            foreach (string left in new[] { "-0.log", "-0.log.mbtree", "-0.log.temp", "-0.log.mbtree.temp" }) File.Delete(log + left);
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

// Image operations: convert, scale, crop, compress. Originals are never modified or overwritten.
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Path = System.IO.Path;

enum CropShape { Rectangle, Rounded, Circle, Ellipse, Triangle, Diamond, Hexagon, Star, Heart }

// One step of turning a picture: a quarter turn, or mirroring left-right (Mirror) or top-bottom (Flip).
enum Turn { Left, Right, Mirror, Flip }

class Picture
{
    public BitmapSource Pixels; // always Bgra32 and frozen
    public ReadOnlyCollection<ColorContext> Colors;
    public bool Transparent;
}

// What to tell the user after a job; File is set when something was saved.
class Outcome
{
    public string Text, File;
    public long Bytes; // size of the saved result
    public int Saved, Failed; // filled in for batches
}

// A new ZIP next to the originals that receives the results of a batch; it appears with the first entry.
// An archive that was not finished is removed.
class Bundle : IDisposable
{
    readonly string folder, name;
    readonly HashSet<string> taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    ZipArchive zip;
    bool broken, finished;
    public string File; // null until something was added

    public Bundle(string folder, string name)
    {
        this.folder = folder;
        this.name = name;
    }

    // Returns the name the entry got.
    public string Add(string entryName, string ext, byte[] data)
    {
        string problem = Naming.Problem(entryName);
        if (problem != null) throw new ArgumentException(problem);
        if (zip == null)
        {
            var stream = Images.CreateFree(folder, name, ".zip", out File);
            zip = new ZipArchive(stream, ZipArchiveMode.Create, false, System.Text.Encoding.UTF8);
        }
        string entry = entryName + ext;
        for (int i = 2; !taken.Add(entry); i++) entry = entryName + " (" + i + ")" + ext;
        try
        {
            using (var target = zip.CreateEntry(entry, CompressionLevel.Fastest).Open()) target.Write(data, 0, data.Length);
        }
        catch { broken = true; throw; }
        return entry;
    }

    // Closes the archive and makes the batch report point at it.
    public void Finish(Outcome report)
    {
        if (zip == null) return;
        if (broken) throw new IOException("ZIP dosyası yazılamadı.");
        zip.Dispose();
        finished = true;
        var lines = report.Text.Split('\n');
        lines[0] += " · " + Path.GetFileName(File);
        report.Text = string.Join("\n", lines);
        report.File = File;
    }

    public void Dispose()
    {
        if (zip == null || finished) return;
        try { zip.Dispose(); }
        finally { System.IO.File.Delete(File); }
    }
}

// A compressed result found by trying several settings.
class Candidate
{
    public byte[] Data;
    public int Quality, Width, Height;
    public bool Resized;

    public string Note { get { return "kalite " + Quality + (Resized ? ", boyut " + Width + " × " + Height : ""); } }
}

// Output file names, including the fixed suffix the user can choose to add to every saved file.
static class Naming
{
    static string RegistryKey { get { return @"HKEY_CURRENT_USER\" + Prefs.Store; } }

    public static bool UseSuffix
    {
        get { return Equals(Microsoft.Win32.Registry.GetValue(RegistryKey, "UseSuffix", 0), 1); }
    }

    public static string Suffix
    {
        get { return Microsoft.Win32.Registry.GetValue(RegistryKey, "Suffix", "") as string ?? ""; }
    }

    public static void Remember(bool use, string suffix)
    {
        Microsoft.Win32.Registry.SetValue(RegistryKey, "UseSuffix", use ? 1 : 0);
        Microsoft.Win32.Registry.SetValue(RegistryKey, "Suffix", suffix);
    }

    // Name without extension. The fixed suffix, when on, replaces the per-operation one.
    public static string Compose(string original, string operationSuffix, bool use, string suffix)
    {
        return Path.GetFileNameWithoutExtension(original) + (use ? suffix : operationSuffix);
    }

    public static string Default(string original, string operationSuffix)
    {
        string name = Compose(original, operationSuffix, UseSuffix, Suffix);
        // A hand-edited registry value must not steer the file somewhere else.
        return Problem(name) == null ? name : Compose(original, operationSuffix, false, "");
    }

    // Name for one file of a batch: a common name gets a running number, otherwise each keeps its own.
    public static string BatchName(string original, string common, int index, string operationSuffix, bool use, string suffix)
    {
        if (common == "") return Compose(original, operationSuffix, use, suffix);
        return common + "_" + (index + 1) + (use ? suffix : "");
    }

    // Why the name cannot be used, or null when it is fine.
    public static string Problem(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Dosya adı boş olamaz.";
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return "Adda \\ / : * ? \" < > | kullanılamaz.";
        if (name.EndsWith(".") || name.EndsWith(" ")) return "Ad nokta veya boşlukla bitemez.";
        string stem = name.Split('.')[0].TrimEnd().ToUpperInvariant();
        bool device = stem == "CON" || stem == "PRN" || stem == "AUX" || stem == "NUL"
            || (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && char.IsDigit(stem[3]));
        if (device) return "Bu ad Windows tarafından ayrılmış.";
        return name.Length > 200 ? "Ad çok uzun." : null;
    }
}

static class Images
{
    static readonly string[] Readable = { ".jpg", ".jpeg", ".jfif", ".png", ".bmp", ".gif", ".tif", ".tiff", ".webp", ".heic" };
    public const long MaxPixels = 100000000;

    public static Action<Outcome> Notify = outcome => { };

    public static bool IsImage(string path)
    {
        return path != null && File.Exists(path) && Readable.Contains(Path.GetExtension(path).ToLowerInvariant());
    }

    // WPF imaging needs an STA thread; the UI thread must stay free for the keyboard hook.
    public static void Background(Action work)
    {
        var thread = new Thread(() =>
        {
            // An error that escapes a thread would end the whole app.
            try { work(); }
            catch (Exception e) { Log.Unexpected("arka plan işi", e); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    // Runs a one-click job and reports the outcome.
    public static void Start(Func<Outcome> job)
    {
        Background(() =>
        {
            try
            {
                var outcome = job();
                if (outcome != null) Notify(outcome);
            }
            catch (Exception e)
            {
                Log.Error("tek tıkla işlem", e);
                Show("İşlem yapılamadı:\n" + e.Message, MessageBoxButton.OK);
            }
        });
    }

    // A one-click job for the selection: one file reports itself, several are summed up in one message.
    public static void Start(string[] files, Func<string, Outcome> job)
    {
        if (files.Length == 1) Start(() => job(files[0]));
        else Start(() => Batch(files, (file, index) => job(file), null, null));
    }

    // Runs the files one after another; one failure does not stop the rest.
    public static Outcome Batch(string[] files, Func<string, int, Outcome> job, Action<int> progress, Func<bool> cancelled)
    {
        int saved = 0, skipped = 0, failed = 0;
        string first = null, problem = null;
        long before = 0, after = 0; // of the saved files
        for (int i = 0; i < files.Length; i++)
        {
            if (cancelled != null && cancelled()) break;
            if (progress != null) progress(i);
            try
            {
                var outcome = job(files[i], i);
                if (outcome == null || outcome.File == null) { skipped++; continue; }
                saved++;
                first = first ?? outcome.File;
                before += new FileInfo(files[i]).Length;
                after += outcome.Bytes;
            }
            catch (Exception e)
            {
                failed++;
                Log.Error("toplu işlem: " + files[i], e);
                problem = problem ?? Path.GetFileName(files[i]) + ": " + e.Message;
            }
        }
        string text = saved + " dosya kaydedildi" + (skipped > 0 ? ", " + skipped + " atlandı" : "")
            + (failed > 0 ? ", " + failed + " hata" : "")
            + (saved > 0 ? "\nToplam " + Size(before) + " → " + Size(after) + " · " + Difference(before, after) : "")
            + (failed > 0 ? "\n" + problem : "");
        return new Outcome { File = first, Saved = saved, Failed = failed, Text = text };
    }

    // Asks a question once per batch and reuses the answer.
    public static Func<bool> Once(Func<bool> ask)
    {
        bool? answer = null;
        return () => (answer ?? (answer = ask())).Value;
    }

    static MessageBoxResult Show(string text, MessageBoxButton buttons)
    {
        return MessageBox.Show(text, "All In One", buttons, MessageBoxImage.Warning, MessageBoxResult.No,
            MessageBoxOptions.DefaultDesktopOnly);
    }

    public static bool AskWhiteBackground()
    {
        return Show("Bu görselde saydam alanlar var. Yeni dosyada beyaz olacaklar. Devam edilsin mi?",
            MessageBoxButton.YesNo) == MessageBoxResult.Yes;
    }

    public static Picture Load(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var decoder = BitmapDecoder.Create(stream,
                BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count > 1 && ext != ".heic")
                throw new NotSupportedException("Animasyonlu veya çok sayfalı görseller desteklenmiyor.");
            var frame = decoder.Frames[0];

            ReadOnlyCollection<ColorContext> colors = null;
            var f = frame.Format;
            // A profile only stays valid while the pixels remain in an RGB space.
            if (f == PixelFormats.Bgr24 || f == PixelFormats.Bgr32 || f == PixelFormats.Bgra32 || f == PixelFormats.Rgb24
                || f == PixelFormats.Rgb48 || f == PixelFormats.Rgba64)
                colors = frame.ColorContexts;

            BitmapSource pixels = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
            // Phone photos store rotation as metadata; bake it in because the metadata is not copied.
            if (ext == ".jpg" || ext == ".jpeg" || ext == ".jfif" || ext == ".tif" || ext == ".tiff")
                pixels = Upright(pixels, frame.Metadata as BitmapMetadata);
            var copy = new WriteableBitmap(pixels);
            copy.Freeze();
            return new Picture { Pixels = copy, Colors = colors, Transparent = HasTransparency(copy) };
        }
    }

    static BitmapSource Upright(BitmapSource source, BitmapMetadata metadata)
    {
        object value = metadata == null ? null : metadata.GetQuery("System.Photo.Orientation");
        int o = value == null ? 1 : Convert.ToInt32(value);
        if (o < 2 || o > 8) return source;
        var t = new TransformGroup();
        if (o == 2 || o == 5 || o == 7) t.Children.Add(new ScaleTransform(-1, 1));
        if (o == 4) t.Children.Add(new ScaleTransform(1, -1));
        int angle = o == 3 ? 180 : o == 6 || o == 7 ? 90 : o == 8 || o == 5 ? 270 : 0;
        if (angle != 0) t.Children.Add(new RotateTransform(angle));
        return new TransformedBitmap(source, t);
    }

    // Windows has no WebP encoder, so an installed FFmpeg does it.
    // shortcut: relies on FFmpeg being on PATH; ship libwebp with the app if it has to run on other PCs.
    public static readonly string Ffmpeg = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';')
        .Select(d => d.Trim().Trim('"'))
        // Only absolute folders: a planted ffmpeg.exe next to a picture must never run.
        .Where(d => d.Length > 2 && d[1] == ':' && d[2] == '\\' && d.IndexOfAny(Path.GetInvalidPathChars()) < 0)
        .Select(d => Path.Combine(d, "ffmpeg.exe"))
        .FirstOrDefault(File.Exists);

    // The format a picture keeps when it is scaled or cropped; what cannot be written (heic) becomes PNG.
    public static string Writable(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".jfif") return ".jpg";
        return ext == ".heic" || (ext == ".webp" && Ffmpeg == null) ? ".png" : ext;
    }

    // Same, for a result with clear areas: what cannot hold them (a BMP, a PNG wrongly named .jpg) becomes PNG.
    public static string Writable(string path, bool transparent)
    {
        string ext = Writable(path);
        return transparent && (ext == ".jpg" || ext == ".jpeg" || ext == ".bmp") ? ".png" : ext;
    }

    static byte[] EncodeWebp(BitmapSource source, int quality)
    {
        if (Ffmpeg == null) throw new NotSupportedException("WebP için FFmpeg bulunamadı.");
        string stem = Path.Combine(Path.GetTempPath(), "AllInOne-" + Guid.NewGuid().ToString("N"));
        string input = stem + ".png", output = stem + ".webp";
        try
        {
            File.WriteAllBytes(input, Encode(source, null, ".png", 100)); // lossless hand-over, alpha included
            var info = new System.Diagnostics.ProcessStartInfo(Ffmpeg,
                "-nostdin -v error -i \"" + input + "\" -c:v libwebp -quality " + quality + " -compression_level 6 \"" + output + "\"")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            using (var process = System.Diagnostics.Process.Start(info))
            {
                string errors = process.StandardError.ReadToEnd();
                process.WaitForExit();
                if (process.ExitCode != 0) throw new IOException("WebP oluşturulamadı: " + errors.Trim());
            }
            return File.ReadAllBytes(output);
        }
        finally
        {
            File.Delete(input);
            File.Delete(output);
        }
    }

    static byte[] Pixels(BitmapSource source)
    {
        var data = new byte[checked(source.PixelWidth * source.PixelHeight * 4)];
        source.CopyPixels(data, source.PixelWidth * 4, 0);
        return data;
    }

    static bool HasTransparency(BitmapSource source)
    {
        var data = Pixels(source);
        for (int i = 3; i < data.Length; i += 4) if (data[i] != 255) return true;
        return false;
    }

    static BitmapSource OnWhite(BitmapSource source)
    {
        var data = Pixels(source);
        for (int i = 0; i < data.Length; i += 4)
        {
            int a = data[i + 3];
            for (int c = 0; c < 3; c++) data[i + c] = (byte)((data[i + c] * a + 255 * (255 - a)) / 255);
        }
        var flat = BitmapSource.Create(source.PixelWidth, source.PixelHeight, 96, 96, PixelFormats.Bgr32, null,
            data, source.PixelWidth * 4);
        return new FormatConvertedBitmap(flat, PixelFormats.Bgr24, null, 0);
    }

    // GIF knows only fully clear or fully opaque pixels: keep one palette entry for the clear ones.
    static BitmapSource WithClearColor(BitmapSource source)
    {
        var colors = new BitmapPalette(source, 255).Colors.Where(c => c.A == 255).Take(255).ToList();
        colors.Add(Color.FromArgb(0, 0, 0, 0));
        return new FormatConvertedBitmap(source, PixelFormats.Indexed8, new BitmapPalette(colors), 50);
    }

    // Compressing keeps WebP as WebP; otherwise transparent pictures stay PNG and the rest become JPEG.
    public static string CompressedFormat(string path, Picture picture)
    {
        if (Writable(path) == ".webp") return ".webp";
        return picture.Transparent ? ".png" : ".jpg";
    }

    // PNG is lossless, so it shrinks only after the colours are rounded to fewer levels. Alpha stays exact.
    public static BitmapSource Simplify(BitmapSource source, int quality)
    {
        int step = 1 + (100 - quality) / 4;
        var data = Pixels(source);
        for (int i = 0; i < data.Length; i += 4)
            for (int c = 0; c < 3; c++)
                data[i + c] = data[i + 3] == 0 ? (byte)0 : (byte)Math.Min(255, (data[i + c] + step / 2) / step * step);
        var result = BitmapSource.Create(source.PixelWidth, source.PixelHeight, 96, 96, PixelFormats.Bgra32, null,
            data, source.PixelWidth * 4);
        result.Freeze();
        return result;
    }

    public static byte[] Encode(BitmapSource source, ReadOnlyCollection<ColorContext> colors, string ext, int quality)
    {
        if (ext == ".webp") return EncodeWebp(source, quality);
        BitmapEncoder encoder;
        switch (ext)
        {
            case ".jpg": case ".jpeg": encoder = new JpegBitmapEncoder { QualityLevel = quality }; break;
            case ".png": encoder = new PngBitmapEncoder(); break;
            case ".bmp": encoder = new BmpBitmapEncoder(); colors = null; break;
            case ".gif":
                encoder = new GifBitmapEncoder();
                colors = null;
                if (HasTransparency(source)) source = WithClearColor(source);
                break;
            case ".tif": case ".tiff": encoder = new TiffBitmapEncoder { Compression = TiffCompressOption.Zip }; break;
            default: throw new NotSupportedException("Bu biçime kaydedilemiyor: " + ext);
        }
        if (encoder is JpegBitmapEncoder || encoder is BmpBitmapEncoder) source = OnWhite(source);
        encoder.Frames.Add(BitmapFrame.Create(source, null, null, colors));
        using (var memory = new MemoryStream())
        {
            encoder.Save(memory);
            return memory.ToArray();
        }
    }

    // What an encoded result looks like; null when Windows has no decoder for it.
    public static BitmapSource Decode(byte[] data, string ext)
    {
        try
        {
            using (var memory = new MemoryStream(data))
            {
                var frame = BitmapDecoder.Create(memory, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
                frame.Freeze();
                return frame;
            }
        }
        catch (NotSupportedException) { if (ext == ".webp") return null; throw; }
    }

    public static BitmapSource Resize(BitmapSource source, int w, int h)
    {
        if (w < 1 || h < 1 || (long)w * h > MaxPixels) throw new NotSupportedException("Geçersiz boyut: " + w + " × " + h);
        if (w == source.PixelWidth && h == source.PixelHeight) return source;
        var group = new DrawingGroup();
        RenderOptions.SetBitmapScalingMode(group, BitmapScalingMode.HighQuality);
        group.Children.Add(new ImageDrawing(source, new Rect(0, 0, w, h)));
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen()) context.DrawDrawing(group);
        var rendered = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rendered.Render(visual);
        var result = new FormatConvertedBitmap(rendered, PixelFormats.Bgra32, null, 0);
        result.Freeze();
        return result;
    }

    public static BitmapSource Cut(BitmapSource source, Int32Rect area)
    {
        var result = new CroppedBitmap(source, area);
        result.Freeze();
        return result;
    }

    // Opens a new file under a free name: an existing one is never overwritten.
    public static FileStream CreateFree(string folder, string name, string ext, out string target)
    {
        string problem = Naming.Problem(name);
        if (problem != null) throw new ArgumentException(problem);
        string stem = Path.Combine(folder, name);
        for (int i = 1; ; i++)
        {
            target = stem + (i == 1 ? "" : " (" + i + ")") + ext;
            if (File.Exists(target) || Directory.Exists(target)) continue;
            try { return new FileStream(target, FileMode.CreateNew, FileAccess.Write); }
            catch (IOException) { if (File.Exists(target)) continue; throw; }
        }
    }

    // Writes next to the original.
    static string SaveNew(string original, string name, string ext, byte[] data)
    {
        string target;
        var stream = CreateFree(Path.GetDirectoryName(original), name, ext, out target);
        try { using (stream) stream.Write(data, 0, data.Length); }
        catch { File.Delete(target); throw; }
        return target;
    }

    public static string Size(long bytes)
    {
        return bytes >= 1048576 ? (bytes / 1048576.0).ToString("0.0") + " MB" : Math.Max(1, bytes / 1024) + " KB";
    }

    // How the new file size compares to the old one.
    public static string Difference(long before, long after)
    {
        if (after == before) return "Boyut aynı";
        int percent = (int)Math.Round(Math.Abs(after - before) * 100.0 / before);
        return (percent == 0 ? "" : "%" + percent + " ") + (after < before
            ? "daha küçük · " + Size(before - after) + " kazanç" : "daha büyük · +" + Size(after - before));
    }

    // The name of a format as shown to the user.
    public static string Kind(string ext)
    {
        string e = ext.ToLowerInvariant();
        return e == ".jpeg" || e == ".jfif" || e == ".jpg" ? "JPG" : e == ".tif" || e == ".tiff" ? "TIFF"
            : e == ".webp" ? "WebP" : e.TrimStart('.').ToUpperInvariant();
    }

    // Encodes and saves next to the original. Returns null when the user declines losing transparency.
    public static Outcome Export(string original, Picture picture, BitmapSource pixels, string ext, int quality,
        string name, bool onlyIfSmaller, Func<bool> confirm, Bundle bundle = null)
    {
        if ((ext == ".jpg" || ext == ".jpeg" || ext == ".bmp") && HasTransparency(pixels) && !confirm()) return null;
        return SaveEncoded(original, Encode(pixels, picture.Colors, ext, quality), ext, name, onlyIfSmaller, bundle);
    }

    // Saves next to the original, or into the bundle when one is given.
    public static Outcome SaveEncoded(string original, byte[] data, string ext, string name, bool onlyIfSmaller,
        Bundle bundle = null)
    {
        long before = new FileInfo(original).Length;
        if (onlyIfSmaller && data.Length >= before)
            return new Outcome { Text = "Dosya küçülmediği için kaydedilmedi. Kaliteyi düşürmeyi dene." };
        string target = bundle == null ? SaveNew(original, name, ext, data) : bundle.Add(name, ext, data);
        return new Outcome { File = target, Bytes = data.Length, Text = Path.GetFileName(target) + "\n" + Size(before)
            + " → " + Size(data.Length) + " · " + Difference(before, data.Length) };
    }

    // Smart quality keeps at least this structural similarity (1 = identical) to the original.
    // Calibrated on photographs; WebP smooths grain, which costs score without a visible loss.
    public static double LooksSame(string ext)
    {
        return ext == ".webp" ? 0.985 : 0.99;
    }

    public const int SmartFloor = 50, SmartCeiling = 92;

    static byte[] TryQuality(BitmapSource source, ReadOnlyCollection<ColorContext> colors, string ext, int quality)
    {
        return Encode(ext == ".png" ? Simplify(source, quality) : source, colors, ext, quality);
    }

    // The lowest value in low..high that passes, assuming higher values keep passing; high when none does.
    public static int Lowest(int low, int high, Func<int, bool> passes)
    {
        while (low < high)
        {
            int middle = (low + high) / 2;
            if (passes(middle)) high = middle; else low = middle + 1;
        }
        return low;
    }

    // The highest value in low..high that passes, assuming lower values keep passing; low - 1 when none does.
    public static int Highest(int low, int high, Func<int, bool> passes)
    {
        if (!passes(low)) return low - 1;
        while (low < high)
        {
            int middle = (low + high + 1) / 2;
            if (passes(middle)) low = middle; else high = middle - 1;
        }
        return low;
    }

    // The smallest file that still looks like the original.
    public static Candidate Smart(BitmapSource source, ReadOnlyCollection<ColorContext> colors, string ext)
    {
        var original = Luma(source);
        var tried = new Dictionary<int, byte[]>();
        Func<int, byte[]> encode = q =>
        {
            if (!tried.ContainsKey(q)) tried[q] = TryQuality(source, colors, ext, q);
            return tried[q];
        };
        int quality = Lowest(SmartFloor, SmartCeiling, q =>
        {
            var look = Decode(encode(q), ext);
            if (look == null) throw new NotSupportedException("Bu biçim geri okunamadığı için kalite ölçülemiyor.");
            return Similarity(original, Luma(look), source.PixelWidth, source.PixelHeight) >= LooksSame(ext);
        });
        return new Candidate { Data = encode(quality), Quality = quality, Width = source.PixelWidth, Height = source.PixelHeight };
    }

    // The best looking file of at most limit bytes: lowers the quality first, the dimensions only if that is not enough.
    public static Candidate UnderSize(BitmapSource source, ReadOnlyCollection<ColorContext> colors, string ext, long limit)
    {
        const int Floor = 40; // below this the artefacts are worse than a smaller picture
        double scale = 1;
        var current = source;
        for (int round = 0; round < 12; round++)
        {
            var tried = new Dictionary<int, byte[]>();
            Func<int, byte[]> encode = q =>
            {
                if (!tried.ContainsKey(q)) tried[q] = TryQuality(current, colors, ext, q);
                return tried[q];
            };
            int quality = Highest(Floor, 95, q => encode(q).Length <= limit);
            if (quality >= Floor)
                return new Candidate { Data = encode(quality), Quality = quality, Width = current.PixelWidth,
                    Height = current.PixelHeight, Resized = current != source };
            // Size grows roughly with the pixel count; aim a little under it at a decent quality.
            scale *= Math.Min(0.9, Math.Sqrt((double)limit / encode(60).Length) * 0.97);
            int w = (int)(source.PixelWidth * scale), h = (int)(source.PixelHeight * scale);
            if (w < 16 || h < 16) break;
            current = Resize(source, w, h);
        }
        throw new NotSupportedException("Bu görsel " + Size(limit) + " altına indirilemedi.");
    }

    // Brightness of each pixel, transparent areas seen against white.
    public static byte[] Luma(BitmapSource source)
    {
        if (source.Format != PixelFormats.Bgra32) source = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var data = Pixels(source);
        var luma = new byte[data.Length / 4];
        for (int i = 0; i < luma.Length; i++)
        {
            int y = (29 * data[i * 4] + 150 * data[i * 4 + 1] + 77 * data[i * 4 + 2]) >> 8, a = data[i * 4 + 3];
            luma[i] = (byte)((y * a + 255 * (255 - a)) / 255);
        }
        return luma;
    }

    // Mean structural similarity (SSIM) over 8 × 8 blocks.
    public static double Similarity(byte[] a, byte[] b, int width, int height)
    {
        const double C1 = 6.5025, C2 = 58.5225; // (0.01 * 255)² and (0.03 * 255)²
        int block = Math.Min(8, Math.Min(width, height));
        double total = 0;
        int blocks = 0;
        for (int top = 0; top + block <= height; top += block)
            for (int left = 0; left + block <= width; left += block)
            {
                long sa = 0, sb = 0, saa = 0, sbb = 0, sab = 0;
                for (int y = top; y < top + block; y++)
                    for (int x = left; x < left + block; x++)
                    {
                        int va = a[y * width + x], vb = b[y * width + x];
                        sa += va; sb += vb; saa += va * va; sbb += vb * vb; sab += va * vb;
                    }
                double n = block * block, ma = sa / n, mb = sb / n;
                double varA = saa / n - ma * ma, varB = sbb / n - mb * mb, covariance = sab / n - ma * mb;
                total += (2 * ma * mb + C1) * (2 * covariance + C2) / ((ma * ma + mb * mb + C1) * (varA + varB + C2));
                blocks++;
            }
        return total / blocks;
    }

    public static Outcome CompressSmart(string path)
    {
        var picture = Load(path);
        string ext = CompressedFormat(path, picture);
        var found = Smart(picture.Pixels, picture.Colors, ext);
        return Noted(SaveEncoded(path, found.Data, ext, Naming.Default(path, "_sikistirilmis"), true), found);
    }

    public static Outcome CompressUnder(string path, int kilobytes)
    {
        if (new FileInfo(path).Length <= kilobytes * 1024L) return new Outcome { Text = "Dosya zaten " + kilobytes + " KB altında." };
        var picture = Load(path);
        string ext = CompressedFormat(path, picture);
        var found = UnderSize(picture.Pixels, picture.Colors, ext, kilobytes * 1024L);
        return Noted(SaveEncoded(path, found.Data, ext, Naming.Default(path, "_sikistirilmis"), true), found);
    }

    public static Outcome Noted(Outcome outcome, Candidate found)
    {
        if (outcome.File != null) outcome.Text += " · " + found.Note;
        else outcome.Text = "Dosya zaten verimli: küçülmediği için kaydedilmedi.";
        return outcome;
    }

    // The one-click options of the outer ring.
    public static Outcome ConvertTo(string path, string ext, Func<bool> confirm)
    {
        var picture = Load(path);
        return Export(path, picture, picture.Pixels, ext, 92, Naming.Default(path, ""), false, confirm);
    }

    public static Outcome Scale(string path, int percent)
    {
        var picture = Load(path);
        var result = Resize(picture.Pixels, Percent(picture.Pixels.PixelWidth, percent), Percent(picture.Pixels.PixelHeight, percent));
        return Export(path, picture, result, Writable(path, picture.Transparent), 92, Naming.Default(path, "_" + percent), false,
            AskWhiteBackground);
    }

    public static Outcome Compress(string path, int quality)
    {
        var picture = Load(path);
        string ext = CompressedFormat(path, picture);
        var pixels = ext == ".png" ? Simplify(picture.Pixels, quality) : picture.Pixels;
        return Export(path, picture, pixels, ext, quality, Naming.Default(path, "_sikistirilmis"), true, AskWhiteBackground);
    }

    public static int Percent(int length, double percent)
    {
        return Math.Max(1, (int)Math.Round(length * percent / 100.0));
    }

    // Output size of one picture. typed: 0 = by percent, 1 = width was typed, 2 = height was typed.
    public static void TargetSize(int w, int h, int percent, int typed, int width, int height, bool keepRatio,
        out int resultWidth, out int resultHeight)
    {
        if (typed == 0) { resultWidth = Percent(w, percent); resultHeight = Percent(h, percent); }
        else if (!keepRatio) { resultWidth = width; resultHeight = height; }
        else if (typed == 1) { resultWidth = width; resultHeight = Percent(h, width * 100.0 / w); }
        else { resultHeight = height; resultWidth = Percent(w, height * 100.0 / h); }
    }

    // The picture mirrored left to right (if asked) and then turned clockwise by quarter turns.
    public static BitmapSource Oriented(BitmapSource source, int turns, bool mirrored)
    {
        if (turns == 0 && !mirrored) return source;
        var transform = new TransformGroup();
        if (mirrored) transform.Children.Add(new ScaleTransform(-1, 1));
        if (turns != 0) transform.Children.Add(new RotateTransform(turns * 90));
        var result = new WriteableBitmap(new TransformedBitmap(source, transform));
        result.Freeze();
        return result;
    }

    // The orientation after one more step on top of what is shown.
    public static void Compose(Turn step, ref int turns, ref bool mirrored)
    {
        int own = step == Turn.Right ? 1 : step == Turn.Left ? 3 : step == Turn.Flip ? 2 : 0;
        bool mirrors = step == Turn.Mirror || step == Turn.Flip;
        turns = (mirrors ? own - turns + 4 : own + turns) % 4; // mirroring reverses the earlier turns
        mirrored ^= mirrors;
    }

    // Where an area, given as fractions of the picture, lies after the step.
    public static Rect Moved(Rect area, Turn step)
    {
        if (step == Turn.Right) return new Rect(Math.Max(0, 1 - area.Bottom), area.X, area.Height, area.Width);
        if (step == Turn.Left) return new Rect(area.Y, Math.Max(0, 1 - area.Right), area.Height, area.Width);
        if (step == Turn.Mirror) return new Rect(Math.Max(0, 1 - area.Right), area.Y, area.Width, area.Height);
        return new Rect(area.X, Math.Max(0, 1 - area.Bottom), area.Width, area.Height);
    }

    // The outline of a crop shape filling the given area; roundness is the corner radius in percent of the short side.
    public static Geometry Shaped(CropShape shape, Rect area, double roundness)
    {
        if (shape == CropShape.Rectangle) return new RectangleGeometry(area);
        if (shape == CropShape.Rounded)
        {
            double radius = Math.Min(area.Width, area.Height) * Math.Max(0, Math.Min(50, roundness)) / 100;
            return new RectangleGeometry(area, radius, radius);
        }
        if (shape == CropShape.Circle || shape == CropShape.Ellipse) return new EllipseGeometry(area);
        Geometry unit; // drawn in a 0..100 square, then stretched over the area
        if (shape == CropShape.Heart)
            unit = Geometry.Parse("M50,100 C20,75 0,55 0,30 C0,12 13,0 28,0 C38,0 46,6 50,15 C54,6 62,0 72,0 "
                + "C87,0 100,12 100,30 C100,55 80,75 50,100 Z").Clone();
        else
        {
            Point[] points;
            if (shape == CropShape.Triangle) points = new[] { new Point(50, 0), new Point(100, 100), new Point(0, 100) };
            else if (shape == CropShape.Diamond)
                points = new[] { new Point(50, 0), new Point(100, 50), new Point(50, 100), new Point(0, 50) };
            else if (shape == CropShape.Hexagon)
                points = new[] { new Point(25, 0), new Point(75, 0), new Point(100, 50), new Point(75, 100),
                    new Point(25, 100), new Point(0, 50) };
            else
            {
                var star = Enumerable.Range(0, 10).Select(i =>
                {
                    double angle = (i * 36 - 90) * Math.PI / 180, r = i % 2 == 0 ? 1 : 0.42;
                    return new Point(r * Math.Cos(angle), r * Math.Sin(angle));
                }).ToArray();
                double left = star.Min(p => p.X), top = star.Min(p => p.Y);
                double wide = star.Max(p => p.X) - left, high = star.Max(p => p.Y) - top;
                points = star.Select(p => new Point((p.X - left) / wide * 100, (p.Y - top) / high * 100)).ToArray();
            }
            var figure = new PathFigure { StartPoint = points[0], IsClosed = true };
            figure.Segments.Add(new PolyLineSegment(points.Skip(1), true));
            unit = new PathGeometry(new[] { figure });
        }
        var place = new TransformGroup();
        place.Children.Add(new ScaleTransform(area.Width / 100, area.Height / 100));
        place.Children.Add(new TranslateTransform(area.X, area.Y));
        unit.Transform = place;
        return unit;
    }

    // Everything outside the shape becomes clear; the colours inside stay exactly as they were.
    public static BitmapSource Masked(BitmapSource source, CropShape shape, double roundness)
    {
        int w = source.PixelWidth, h = source.PixelHeight;
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
            context.DrawGeometry(Brushes.White, null, Shaped(shape, new Rect(0, 0, w, h), roundness));
        var mask = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        mask.Render(visual);
        byte[] cover = Pixels(mask), data = Pixels(source);
        for (int i = 0; i < data.Length; i += 4)
        {
            data[i + 3] = (byte)((data[i + 3] * cover[i + 3] + 127) / 255);
            if (data[i + 3] == 0) data[i] = data[i + 1] = data[i + 2] = 0;
        }
        var result = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, data, w * 4);
        result.Freeze();
        return result;
    }

    // A crop area given in pixels, made to fit the picture and a fixed ratio (0: free). heightLed: the height decides.
    public static Int32Rect Fitted(int x, int y, int w, int h, double ratio, int width, int height, bool heightLed)
    {
        w = Math.Max(1, Math.Min(width, w));
        h = Math.Max(1, Math.Min(height, h));
        if (ratio > 0)
        {
            double wide = heightLed ? h * ratio : w;
            wide = Math.Min(wide, Math.Min(width, height * ratio));
            w = Math.Max(1, (int)Math.Round(wide));
            h = Math.Max(1, Math.Min(height, (int)Math.Round(wide / ratio)));
        }
        return new Int32Rect(Math.Max(0, Math.Min(width - w, x)), Math.Max(0, Math.Min(height - h, y)), w, h);
    }

    // The selection after pulling one of its handles to a point; hx and hy say which one (-1, 0 or 1 on each axis).
    public static Rect Stretched(Rect from, int hx, int hy, Point to, double ratio, double width, double height)
    {
        double x = Math.Max(0, Math.Min(width, to.X)), y = Math.Max(0, Math.Min(height, to.Y));
        if (hx != 0 && hy != 0) // a corner: the opposite one stays
            return Dragged(new Point(hx < 0 ? from.Right : from.Left, hy < 0 ? from.Bottom : from.Top), new Point(x, y),
                ratio, width, height);
        if (ratio <= 0)
            return hx != 0
                ? new Rect(new Point(hx < 0 ? from.Right : from.Left, from.Top), new Point(x, from.Bottom))
                : new Rect(new Point(from.Left, hy < 0 ? from.Bottom : from.Top), new Point(from.Right, y));
        // A side with a fixed ratio: the opposite side stays and the other axis grows around its middle.
        if (hx != 0)
        {
            double anchor = hx < 0 ? from.Right : from.Left, middle = from.Top + from.Height / 2;
            double w = Math.Min(Math.Abs(x - anchor), Math.Min(middle, height - middle) * 2 * ratio), h = w / ratio;
            return new Rect(x < anchor ? anchor - w : anchor, Math.Max(0, middle - h / 2), w, h);
        }
        else
        {
            double anchor = hy < 0 ? from.Bottom : from.Top, middle = from.Left + from.Width / 2;
            double h = Math.Min(Math.Abs(y - anchor), Math.Min(middle, width - middle) * 2 / ratio), w = h * ratio;
            return new Rect(Math.Max(0, middle - w / 2), y < anchor ? anchor - h : anchor, w, h);
        }
    }

    // Carries a crop selection over to a picture of another size: same relative place, same ratio if one is fixed.
    public static Rect Transfer(Rect selection, double fromWidth, double fromHeight, double toWidth, double toHeight, double ratio)
    {
        double x = selection.X / fromWidth, y = selection.Y / fromHeight;
        if (ratio <= 0)
            return new Rect(x * toWidth, y * toHeight, selection.Width / fromWidth * toWidth, selection.Height / fromHeight * toHeight);
        double share = Math.Min(1, selection.Width / Centered(ratio, fromWidth, fromHeight).Width);
        double w = Centered(ratio, toWidth, toHeight).Width * share, h = w / ratio;
        double cx = (selection.X + selection.Width / 2) / fromWidth * toWidth;
        double cy = (selection.Y + selection.Height / 2) / fromHeight * toHeight;
        return new Rect(Math.Max(0, Math.Min(toWidth - w, cx - w / 2)), Math.Max(0, Math.Min(toHeight - h, cy - h / 2)), w, h);
    }

    // Maps a selection on the shrunken preview back to image pixels, clipped to the image.
    public static Int32Rect ToPixels(Point a, Point b, double shown, int width, int height)
    {
        int left = (int)Math.Round(Math.Max(0, Math.Min(a.X, b.X) / shown));
        int top = (int)Math.Round(Math.Max(0, Math.Min(a.Y, b.Y) / shown));
        int right = (int)Math.Round(Math.Min(width, Math.Max(a.X, b.X) / shown));
        int bottom = (int)Math.Round(Math.Min(height, Math.Max(a.Y, b.Y) / shown));
        return new Int32Rect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }

    // Largest centred rectangle of the given width/height ratio (0 means free: everything).
    public static Rect Centered(double ratio, double width, double height)
    {
        if (ratio <= 0) return new Rect(0, 0, width, height);
        double w = Math.Min(width, height * ratio), h = w / ratio;
        return new Rect((width - w) / 2, (height - h) / 2, w, h);
    }

    // The rectangle dragged from start towards end, kept inside the bounds and, if asked, at a fixed ratio.
    public static Rect Dragged(Point start, Point end, double ratio, double width, double height)
    {
        end = new Point(Math.Max(0, Math.Min(width, end.X)), Math.Max(0, Math.Min(height, end.Y)));
        if (ratio <= 0) return new Rect(start, end);
        double dx = end.X < start.X ? -1 : 1, dy = end.Y < start.Y ? -1 : 1;
        double w = Math.Max(Math.Abs(end.X - start.X), Math.Abs(end.Y - start.Y) * ratio);
        w = Math.Min(w, Math.Min(dx > 0 ? width - start.X : start.X, (dy > 0 ? height - start.Y : start.Y) * ratio));
        return new Rect(start, new Point(start.X + dx * w, start.Y + dy * w / ratio));
    }
}

// PDF files: pages are shown with the reader built into Windows, joined and taken apart with qpdf (optional,
// like FFmpeg). Originals are never modified or overwritten.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;
using Windows.Data.Pdf;
using Windows.Foundation;
using Windows.Storage.Streams;
using Path = System.IO.Path;

static class Pdf
{
    public static readonly string Qpdf = Find();

    static string Find()
    {
        // On the search path, or where its installer puts it: "qpdf 12.4.2\bin" under Program Files.
        var folders = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';').Select(d => d.Trim().Trim('"')).ToList();
        string programs = Environment.GetEnvironmentVariable("ProgramW6432")
            ?? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (Directory.Exists(programs))
            folders.AddRange(Directory.GetDirectories(programs, "qpdf*").OrderByDescending(d => d)
                .Select(d => Path.Combine(d, "bin")));
        // Only absolute folders: nothing planted next to the user's files may run.
        return folders.Where(d => d.Length > 2 && d[1] == ':' && d[2] == '\\' && d.IndexOfAny(Path.GetInvalidPathChars()) < 0)
            .Select(d => Path.Combine(d, "qpdf.exe")).FirstOrDefault(File.Exists);
    }

    public static bool Available { get { return Qpdf != null; } }

    public static bool IsPdf(string path)
    {
        return path != null && File.Exists(path) && string.Equals(Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase);
    }

    // Names are handed over in a file, one argument a line, as qpdf takes Turkish letters reliably that way.
    static string Run(params string[] arguments)
    {
        if (!Available) throw new NotSupportedException(Lang.T("PDF için qpdf bulunamadı."));
        string list = Path.Combine(Path.GetTempPath(), "AllInOne-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            File.WriteAllLines(list, arguments, new UTF8Encoding(false));
            var start = new ProcessStartInfo(Qpdf, "\"@" + list + "\"") { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8 };
            using (var process = Process.Start(start))
            {
                var errors = process.StandardError.ReadToEndAsync();
                string output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();
                // 3: done, with warnings about oddities in the file.
                if (process.ExitCode != 0 && process.ExitCode != 3) throw new IOException(Lang.T("PDF işlenemedi: ") + errors.Result.Trim());
                return output;
            }
        }
        finally { File.Delete(list); }
    }

    public static int Pages(string path)
    {
        return int.Parse(Run("--show-npages", path).Trim());
    }

    // "1-3, 5" as qpdf takes it ("1-3,5"), or null when it does not name pages of a file this long.
    public static string Range(string text, int pages)
    {
        var parts = (text ?? "").Replace(" ", "").Split(',');
        foreach (var part in parts)
        {
            var match = Regex.Match(part, @"^(\d{1,6})(?:-(\d{1,6}))?$");
            if (!match.Success) return null;
            int from = int.Parse(match.Groups[1].Value), to = match.Groups[2].Success ? int.Parse(match.Groups[2].Value) : from;
            if (from < 1 || to < 1 || from > pages || to > pages) return null;
        }
        return string.Join(",", parts);
    }

    // Writes into a file that did not exist; an unfinished one is removed.
    static Outcome Make(string folder, string name, Func<string, string[]> arguments)
    {
        string target;
        Images.CreateFree(folder, name, ".pdf", out target).Dispose(); // claims the name
        bool made = false;
        try
        {
            Run(arguments(target));
            made = true;
        }
        finally { if (!made) File.Delete(target); }
        var size = new FileInfo(target).Length;
        return new Outcome { File = target, Bytes = size, Text = Path.GetFileName(target) + "\n" + Pages(target) + Lang.T(" sayfa · ")
            + Images.Size(size) };
    }

    // All pages of the files, in this order, as one new file next to the first.
    public static Outcome Merge(string[] files, string name)
    {
        if (files.Length < 2) throw new ArgumentException(Lang.T("Birleştirmek için en az iki PDF gerekli."));
        return Make(Path.GetDirectoryName(files[0]), name,
            target => new[] { "--empty", "--pages" }.Concat(files).Concat(new[] { "--", target }).ToArray());
    }

    // The chosen pages as one new file.
    public static Outcome Extract(string file, string range, string name)
    {
        string pages = Range(range, Pages(file));
        if (pages == null) throw new ArgumentException(Lang.T("Sayfaları 1-3, 5 biçiminde ve dosyanın içinde kalacak şekilde yaz."));
        return Make(Path.GetDirectoryName(file), name,
            target => new[] { "--empty", "--pages", file, pages, "--", target });
    }

    // Every page as a file of its own, in a new folder next to the original.
    public static Outcome SplitEach(string file)
    {
        int pages = Pages(file);
        string stem = Path.GetFileNameWithoutExtension(file), parent = Path.GetDirectoryName(file), folder;
        for (int i = 1; ; i++)
        {
            folder = Path.Combine(parent, stem + "_sayfalar" + (i == 1 ? "" : " (" + i + ")"));
            if (!Directory.Exists(folder) && !File.Exists(folder)) break;
        }
        Directory.CreateDirectory(folder);
        bool made = false;
        try
        {
            // %d is where the page number goes; a % in the file's own name would be read the same way.
            Run("--split-pages", file, Path.Combine(folder, stem.Replace("%", "_") + "-%d.pdf"));
            made = true;
        }
        finally { if (!made) Directory.Delete(folder, true); }
        string first = Directory.GetFiles(folder, "*.pdf").OrderBy(f => f).First();
        return new Outcome { File = first, Text = Path.GetFileName(folder) + "\n" + pages + Lang.T(" sayfa ayrı dosyalara ayrıldı") };
    }

    // The .NET helpers for WinRT tasks do not accept the metadata files that ship with Windows, so wait by hand.
    static void Wait(IAsyncInfo work)
    {
        while (work.Status == AsyncStatus.Started) System.Threading.Thread.Sleep(5);
        if (work.Status != AsyncStatus.Completed) throw new IOException(Lang.T("PDF açılamadı (parolalı veya bozuk olabilir)."), work.ErrorCode);
    }

    public static PdfDocument Open(string path)
    {
        var data = File.ReadAllBytes(path);
        var stream = new InMemoryRandomAccessStream();
        var writer = new DataWriter(stream);
        writer.WriteBytes(data);
        var stored = writer.StoreAsync();
        Wait(stored);
        writer.DetachStream();
        stream.Seek(0);
        var load = PdfDocument.LoadFromStreamAsync(stream);
        Wait(load);
        return load.GetResults();
    }

    // One page as a picture of the given width.
    public static BitmapSource Render(PdfDocument document, int page, int width)
    {
        using (var sheet = document.GetPage((uint)page))
        using (var output = new InMemoryRandomAccessStream())
        {
            var work = sheet.RenderToStreamAsync(output, new PdfPageRenderOptions { DestinationWidth = (uint)width });
            Wait(work);
            var reader = new DataReader(output.GetInputStreamAt(0));
            var load = reader.LoadAsync((uint)output.Size);
            Wait(load);
            var data = new byte[output.Size];
            reader.ReadBytes(data);
            return Images.Decode(data, ".png");
        }
    }
}

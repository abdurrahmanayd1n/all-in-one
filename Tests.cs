// The self-test of the screen capture, shortcuts, video cutting and joining, PDF, table and preview tools.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Path = System.IO.Path;

static class Tests
{
    static bool ok;
    static string report;

    // A failed check is named in the report file, as a windowed program has nowhere to print.
    static void Expect(bool held, string name)
    {
        if (held) return;
        ok = false;
        File.AppendAllText(report, name + Environment.NewLine);
    }

    public static bool Run(string folder)
    {
        ok = true;
        report = Path.Combine(Path.GetTempPath(), "AllInOne-test.txt");
        File.Delete(report);
        string dir = Path.Combine(folder, "yeni");
        Directory.CreateDirectory(dir);
        try
        {
            // Each part is noted before it runs: after a crash the last note says where it happened.
            Note("tuslar"); Keys();
            Note("goruntu"); Capture(dir);
            Note("video"); Videos(dir);
            Note("pdf"); Pdfs(dir);
            Note("tablo"); Tables();
            Note("onizleme"); Previews(dir);
            Note("kayan"); Scrolling();
            Note("ses"); Sounds(dir);
            Note("dosya araclari"); FileTools(dir);
            Note("ingilizce"); InEnglish(dir);
            Note("bitti");
        }
        catch (Exception e) { Expect(false, e.ToString()); }
        return ok;
    }

    static void Note(string part)
    {
        File.AppendAllText(report, "> " + part + Environment.NewLine);
    }

    // Texts of a window that still carry letters English does not have.
    static void Turkish(System.Windows.DependencyObject item, List<string> found)
    {
        var block = item as System.Windows.Controls.TextBlock;
        var holder = item as System.Windows.Controls.ContentControl;
        var element = item as System.Windows.FrameworkElement;
        var window = item as System.Windows.Window;
        foreach (var text in new object[] { block == null ? null : block.Text, holder == null ? null : holder.Content,
            element == null ? null : element.ToolTip, window == null ? null : window.Title }.OfType<string>())
            if ((text.IndexOfAny("çğışÇĞİŞ".ToCharArray()) >= 0 || Lang.T(text) != text) && !text.Contains("Türkçe") && !text.Contains(Path.GetTempPath()))
                found.Add(text);
        foreach (var child in System.Windows.LogicalTreeHelper.GetChildren(item).OfType<System.Windows.DependencyObject>())
            Turkish(child, found);
    }

    static void InEnglish(string dir)
    {
        string folder = Path.Combine(dir, "en");
        Directory.CreateDirectory(folder);
        string picture = Path.Combine(folder, "a.png"), second = Path.Combine(folder, "b.png"), pdf = Path.Combine(folder, "a.pdf");
        using (var drawn = new Bitmap(40, 30)) drawn.Save(picture, System.Drawing.Imaging.ImageFormat.Png);
        File.Copy(picture, second);
        MakePdf(pdf, 2);
        Lang.English = true;
        try
        {
            Expect(Lang.T("Ayarlar") == "Settings" && Lang.T("bilinmeyen") == "bilinmeyen" && Lang.Percent(5) == "5%", "ceviri");
            var windows = new List<System.Windows.Window>();
            foreach (Mode mode in Enum.GetValues(typeof(Mode))) windows.Add(new Editor(new[] { picture, second }, mode).Window);
            windows.Add(new Reader(new[] { picture, second }).Window);
            windows.Add(new Preferences().Window);
            windows.Add(new RenameWindow(new[] { picture, second }).Window);
            windows.Add(new ZipWindow(new[] { folder }, false).Window);
            windows.Add(new SizeWindow(new[] { folder }).Window);
            windows.Add(new UsersWindow(new[] { picture }).Window);
            windows.Add(new PdfWindow(new[] { pdf }).Window);
            windows.Add(new PdfWindow(new[] { pdf, pdf }).Window);
            windows.Add(new TableWindow(new[] { new[] { "a" } }, Images.Load(picture).Pixels).Window);
            windows.Add(new Preview(new[] { picture, second }).Window);
            var twins = new DuplicatesWindow(new[] { folder });
            twins.List(Duplicates.Find(new[] { folder }, text => { }, () => false));
            windows.Add(twins.Sheet.Window);
            windows.Add(new OrganizeWindow(folder).Sheet.Window);
            var sums = new ChecksumWindow(new[] { picture });
            sums.Add(Checksum.Of(picture, p => { }, () => false));
            windows.Add(sums.Sheet.Window);
            if (Video.Available)
                foreach (VideoTask task in Enum.GetValues(typeof(VideoTask)))
                    windows.Add(new VideoWindow(new[] { picture, second }, new VideoJob { Task = task }, false).Window);
            var left = new List<string>();
            foreach (var window in windows)
            {
                Turkish(window, left);
                window.Close();
            }
            var items = new List<Item>();
            foreach (var selection in new[] { new[] { picture }, new[] { folder }, new[] { pdf }, new[] { pdf, pdf } })
                items.AddRange(Tools.For(selection));
            for (int i = 0; i < items.Count; i++)
            {
                if (items[i].Children != null) items.AddRange(items[i].Children);
                left.AddRange(new[] { items[i].Label, items[i].Hint }.Where(t => t != null
                    && t.IndexOfAny("çğışÇĞİŞ".ToCharArray()) >= 0));
            }
            Expect(left.Count == 0, "cevrilmemis: " + string.Join(" | ", left.Distinct()));
        }
        finally { Lang.English = false; }
    }

    // A tall page seen through a low window with a fixed header and footer, scrolled in uneven steps.
    static void Scrolling()
    {
        const int Width = 240, View = 300, Top = 28, Bottom = 18, Tall = 1400;
        var random = new Random(7);
        Func<int, Bitmap> noise = height =>
        {
            var made = new Bitmap(Width, height);
            using (var g = Graphics.FromImage(made))
            {
                g.Clear(Color.White);
                for (int y = 0; y < height; y += 4)
                    for (int x = 0; x < Width; x += 12)
                    {
                        int grey = random.Next(256);
                        using (var brush = new SolidBrush(Color.FromArgb(grey, grey, grey))) g.FillRectangle(brush, x, y, 12, 4);
                    }
            }
            return made;
        };
        using (var page = noise(Tall))
        using (var header = noise(Top))
        using (var footer = noise(Bottom))
        using (var result = new LongPicture())
        {
            int body = View - Top - Bottom;
            Func<int, Bitmap> at = scrolled =>
            {
                var frame = new Bitmap(Width, View);
                using (var g = Graphics.FromImage(frame))
                {
                    g.DrawImage(header, new Rectangle(0, 0, Width, Top), new Rectangle(0, 0, Width, Top), GraphicsUnit.Pixel);
                    g.DrawImage(page, new Rectangle(0, Top, Width, body), new Rectangle(0, scrolled, Width, body), GraphicsUnit.Pixel);
                    g.DrawImage(footer, new Rectangle(0, View - Bottom, Width, Bottom), new Rectangle(0, 0, Width, Bottom),
                        GraphicsUnit.Pixel);
                }
                return frame;
            };
            var latest = at(0);
            result.Add(latest, 0, View);
            var stitch = new Stitch(Scroller.Rows(latest));
            bool moved = true;
            int offset = 0, drop, from, to;
            foreach (int step in new[] { 90, 90, 120, 37, 90, 150, 90 })
            {
                offset += step;
                var next = at(offset);
                moved &= stitch.Add(Scroller.Rows(next), out drop, out from, out to) == Stitch.Found.Moved;
                result.Drop(drop);
                result.Add(next, from, to);
                latest.Dispose();
                latest = next;
            }
            Expect(moved && stitch.Add(Scroller.Rows(latest), out drop, out from, out to) == Stitch.Found.Still, "kaydirma bulunur");
            using (var other = noise(View))
                Expect(stitch.Add(Scroller.Rows(other), out drop, out from, out to) == Stitch.Found.Lost, "ilgisiz kare");
            result.Add(latest, stitch.Rest, View);
            latest.Dispose();
            using (var whole = result.Compose())
            {
                bool same = whole.Height == Top + body + offset + Bottom;
                for (int y = 0; same && y < whole.Height; y += 3)
                    for (int x = 0; same && x < Width; x += 7)
                    {
                        var wanted = y < Top ? header.GetPixel(x, y) : y >= whole.Height - Bottom
                            ? footer.GetPixel(x, y - (whole.Height - Bottom)) : page.GetPixel(x, y - Top);
                        same = whole.GetPixel(x, y).ToArgb() == wanted.ToArgb();
                    }
                Expect(same, "uzun goruntu " + whole.Height);
            }
        }
        var now = new Rectangle(100, 100, 200, 100);
        Expect(PinShot.Zoomed(now, new Size(200, 100), 2) == new Rectangle(0, 50, 400, 200)
            && PinShot.Zoomed(now, new Size(200, 100), 0.01).Size == new Size(40, 30), "sabit goruntu boyutu");
    }

    static void Sounds(string dir)
    {
        var header = SoundTrack.Header(192000);
        Expect(header.Length == 44 && BitConverter.ToInt32(header, 4) == 192036 && BitConverter.ToInt32(header, 24) == 48000
            && BitConverter.ToInt32(header, 40) == 192000, "wav basligi");
        Expect(SoundTrack.Missing(48000, 1.05) == 0 && SoundTrack.Missing(0, 1) == 45600, "sessizlik doldurma");
        string one = Recorder.Mixing("v.mp4", new[] { "a.wav" }, new[] { 0.25 }, 4, "o.mp4");
        string two = Recorder.Mixing("v.mp4", new[] { "a.wav", "b.wav" }, new[] { 0.0, 0 }, 0, "o.mp4");
        Expect(one.Contains(" -ss 0.25 -i \"a.wav\"") && one.Contains("-map 0:v -map 1:a") && one.Contains(" -t 4 ")
            && two.Contains("[1:a][2:a]amix=inputs=2") && !two.Contains(" -t ") && !two.Contains(" -ss "), "ses karistirma komutu");
        Expect(Recorder.CaptureStart("  Duration: N/A, start: 1795679292.144457, bitrate: 3945 kb/s") == 1795679292.144457
            && Recorder.CaptureStart("  Duration: 00:00:02.00, start: 0.000000, bitrate: 1 kb/s") == 0, "yakalama ani");
        if (!Video.Available) return;
        string video = Path.Combine(dir, "sessiz.mp4"), tone = Path.Combine(dir, "ton.wav"), hum = Path.Combine(dir, "ugultu.wav");
        Ffmpeg("-f lavfi -i testsrc2=size=160x120:rate=30:duration=2 -c:v libx264 \"" + video + "\"");
        Ffmpeg("-f lavfi -i sine=duration=3:sample_rate=48000 -ac 2 -c:a pcm_s16le \"" + tone + "\"");
        Ffmpeg("-f lavfi -i sine=frequency=220:duration=2:sample_rate=48000 -ac 2 -c:a pcm_s16le \"" + hum + "\"");
        Recorder.AddSound(video, new[] { tone, hum });
        var made = Video.Probe(video);
        Expect(made.AudioCodec == "aac" && Math.Abs(made.Seconds - 2) < 0.2, "kayda ses eklenir");
    }

    static void FileTools(string dir)
    {
        // Copies: equal content is found whatever the names, a different byte late in a large file is not.
        string root = Path.Combine(dir, "kopyalar");
        Directory.CreateDirectory(Path.Combine(root, "alt"));
        var large = Enumerable.Range(0, 200000).Select(i => (byte)(i * 31)).ToArray();
        File.WriteAllBytes(Path.Combine(root, "a.bin"), large);
        File.WriteAllBytes(Path.Combine(root, "alt", "b.dat"), large);
        large[199000]++;
        File.WriteAllBytes(Path.Combine(root, "c.bin"), large);
        File.WriteAllText(Path.Combine(root, "x.txt"), "ayni");
        File.WriteAllText(Path.Combine(root, "alt", "y.txt"), "ayni");
        File.WriteAllText(Path.Combine(root, "bos1"), "");
        File.WriteAllText(Path.Combine(root, "bos2"), "");
        File.SetLastWriteTimeUtc(Path.Combine(root, "x.txt"), new DateTime(2020, 1, 1));
        var found = Duplicates.Find(new[] { root, Path.Combine(root, "alt") }, text => { }, () => false);
        Expect(found.Count == 2 && found[0].Files.Length == 2 && found[0].Size == 200000
            && found[1].Files[0].EndsWith("x.txt") && found[1].Files.Length == 2, "kopya bulma");
        var window = new DuplicatesWindow(new[] { root });
        window.List(found);
        string problem;
        Expect(window.Marked(out problem).Length == 0 && problem == null, "kopya isaretsiz");
        window.Sheet.Get<System.Windows.Controls.Button>("Extra").RaiseEvent(
            new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        var marked = window.Marked(out problem);
        Expect(marked.Length == 2 && problem == null && !marked.Any(f => f.EndsWith("x.txt")), "fazlaliklar");
        window.Sheet.Window.Close();

        // Sorting: by kind, nothing overwritten, hidden files and shortcuts stay, and all of it can be undone.
        string box = Path.Combine(dir, "daginik");
        Directory.CreateDirectory(Path.Combine(box, "Görseller"));
        foreach (var name in new[] { "a.jpg", "b.PDF", "c.mp3", "d.qqq", "e", "kisayol.lnk", "gizli.txt" })
            File.WriteAllText(Path.Combine(box, name), name);
        File.WriteAllText(Path.Combine(box, "Görseller", "a.jpg"), "eski");
        File.SetAttributes(Path.Combine(box, "gizli.txt"), FileAttributes.Hidden);
        Func<string> listing = () => string.Join("|", Directory.GetFileSystemEntries(box, "*", SearchOption.AllDirectories)
            .Select(p => p.Substring(box.Length + 1)).OrderBy(p => p, StringComparer.Ordinal));
        string before = listing();
        var plan = Organizer.Plan(box, false);
        Expect(plan.Count == 5 && plan.All(p => p.Problem == null)
            && plan.Single(p => p.From.EndsWith("a.jpg")).To == Path.Combine(box, "Görseller", "a (2).jpg"), "duzenleme plani");
        var made = Organizer.Apply(plan);
        Expect(File.ReadAllText(Path.Combine(box, "Belgeler", "b.PDF")) == "b.PDF" && made.Count == 3
            && File.ReadAllText(Path.Combine(box, "Görseller", "a.jpg")) == "eski"
            && File.Exists(Path.Combine(box, "Diğer dosyalar", "e")) && File.Exists(Path.Combine(box, "kisayol.lnk")), "duzenleme");
        Organizer.Undo(plan, made);
        Expect(listing() == before, "duzenleme geri alma");
        File.SetLastWriteTime(Path.Combine(box, "c.mp3"), new DateTime(2024, 3, 9));
        Expect(Organizer.Plan(box, true).Single(p => p.From.EndsWith("c.mp3")).To == Path.Combine(box, "2024-03", "c.mp3"),
            "aya gore");
        File.WriteAllText(Path.Combine(box, "Belgeler"), "dosya");
        Expect(Organizer.Plan(box, false).Any(p => p.Problem != null), "klasor yerine dosya");
        new OrganizeWindow(box).Sheet.Window.Close();

        // Checksums against the published values for "abc".
        string abc = Path.Combine(dir, "abc.txt");
        File.WriteAllText(abc, "abc", new UTF8Encoding(false));
        var sum = Checksum.Of(abc, p => { }, () => false);
        Expect(sum.Sha256 == "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"
            && sum.Sha1 == "a9993e364706816aba3e25717850c26c9cd0d89d" && sum.Md5 == "900150983cd24fb0d6963f7d28e17f72"
            && sum.Matches("  BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD  abc.txt") == "SHA-256"
            && sum.Matches("MD5: 900150983cd24fb0d6963f7d28e17f72") == "MD5" && sum.Matches("900150983cd24fb0") == null
            && sum.Matches("") == null, "dogrulama kodu");
        new ChecksumWindow(new[] { abc }).Sheet.Window.Close();
    }

    static void Keys()
    {
        int altS = Shortcuts.Alt << 16 | 'S';
        Expect(Shortcuts.Name(altS) == "Alt+S" && Shortcuts.Name((Shortcuts.Win | Shortcuts.Shift) << 16 | 'O') == "Win+Shift+O"
            && Shortcuts.Name(Shortcuts.Ctrl << 16 | '5') == "Ctrl+5" && Shortcuts.Name(0) == "", "tus adlari");
        var snap = Shortcuts.Snap;
        Expect(snap.Usual == altS && Shortcuts.Problem(snap, altS) == null && Shortcuts.Problem(snap, 0) == null
            && Shortcuts.Problem(snap, 'S') != null && Shortcuts.Problem(snap, Shortcuts.Shift << 16 | 'S') != null
            && Shortcuts.Problem(snap, Shortcuts.Alt << 16 | 0x20) != null
            && Shortcuts.Problem(snap, (Shortcuts.Ctrl | Shortcuts.Alt) << 16 | 'Q') != null
            && Shortcuts.Problem(snap, Shortcuts.ScreenWords.Usual) != null && Shortcuts.Problem(snap, 0x78) == null, "tus kurallari");
        Expect(snap.Code == altS, "tus varsayilan");
        snap.Code = 0;
        Expect(snap.Code == 0 && snap.Keys == "", "tus kapatma");
        snap.Code = altS;
        // An unlikely combination: registered once, refused the second time, free again after letting go.
        int rare = (Shortcuts.Ctrl | Shortcuts.Shift) << 16 | 0x86;
        using (var first = new Hotkey(rare, () => { }))
        using (var second = new Hotkey(rare, () => { }))
            Expect(first.Works && !second.Works, "tus kaydi");
        using (var again = new Hotkey(rare, () => { })) Expect(again.Works, "tus birakma");
    }

    static void Capture(string dir)
    {
        Rectangle bar;
        var monitor = new Rectangle(1920, 0, 2560, 1440);
        var buttons = Snip.Buttons(monitor, 1.5, out bar);
        var all = buttons.Values.ToArray();
        Expect(buttons.Count == 6 && monitor.Contains(bar) && all.All(bar.Contains)
            && all.SelectMany((a, i) => all.Skip(i + 1).Select(b => a.IntersectsWith(b))).All(x => !x)
            && Math.Abs((bar.Left - monitor.Left) - (monitor.Right - bar.Right)) <= 1, "arac cubugu");
        Expect(Recorder.Even(new Rectangle(-7, 3, 101, 55)) == new Rectangle(-7, 3, 100, 54), "cift kenar");
        string args = Recorder.Arguments(new Rectangle(-1920, 10, 640, 480), @"C:\a b\k.mp4");
        Expect(args.Contains("-offset_x -1920 -offset_y 10 -video_size 640x480 -i desktop") && args.EndsWith("\"C:\\a b\\k.mp4\""),
            "kayit komutu");
        using (var picture = new Bitmap(30, 20))
        {
            var one = Snip.Save(picture, dir, false);
            var two = Snip.Save(picture, dir, false);
            using (var saved = new Bitmap(one.File))
                Expect(saved.Width == 30 && saved.Height == 20 && one.File != two.File && File.Exists(two.File), "goruntu kaydi");
        }
    }

    static void Ffmpeg(string arguments)
    {
        using (var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Images.Ffmpeg,
            "-v error -y -nostdin " + arguments) { UseShellExecute = false, CreateNoWindow = true }))
            process.WaitForExit();
    }

    static void Videos(string dir)
    {
        var film = new VideoInfo { Seconds = 20, Width = 640, Height = 360, VideoCodec = "h264", AudioCodec = "aac" };
        var fast = new VideoJob { Task = VideoTask.Trim, Start = 5, End = 12 };
        var exact = new VideoJob { Task = VideoTask.Trim, Start = 5, Exact = true };
        string quick = Video.Arguments(fast, film, "a.mkv", "b.mkv", 0, "l");
        Expect(quick.Contains(" -ss 5 -i \"a.mkv\" -t 7 ") && quick.Contains(" -c copy") && fast.ExtensionFor("a.MKV") == ".mkv"
            && exact.ExtensionFor("a.mkv") == ".mp4" && Video.Arguments(exact, film, "a", "b", 0, "l").Contains("libx264")
            && Video.Span(fast, film) == 7 && Video.Span(exact, film) == 15, "kesme komutu");
        Expect(Video.List(new[] { @"C:\a\it's.mp4" }) == "file 'C:\\a\\it'\\''s.mp4'\n", "liste dosyasi");
        var silent = new VideoInfo { Seconds = 3, Width = 320, Height = 240, VideoCodec = "h264" };
        Expect(Video.Alike(new[] { "a.mp4", "b.MP4" }, new[] { film, film }) && !Video.Alike(new[] { "a.mp4", "b.mkv" },
            new[] { film, film }) && !Video.Alike(new[] { "a.mp4", "b.mp4" }, new[] { film, silent }), "benzer videolar");
        string joined = Video.Joining(new[] { "a", "b" }, new[] { film, silent }, "c.mp4");
        Expect(joined.Contains("anullsrc") && joined.Contains("concat=n=2:v=1:a=1[v][a]") && joined.Contains("[2:a:0]")
            && !Video.Joining(new[] { "a", "b" }, new[] { silent, silent }, "c.mp4").Contains("[a]"), "birlestirme komutu");

        if (!Video.Available) return;
        // Real files: a part is cut out, and two unlike videos, one of them silent, become one.
        string a = Path.Combine(dir, "bir ç.mp4"), b = Path.Combine(dir, "iki.mp4");
        Ffmpeg("-f lavfi -i testsrc2=size=320x240:rate=30:duration=3 -f lavfi -i sine=duration=3 -c:v libx264 -g 15 -c:a aac "
            + "-shortest \"" + a + "\"");
        Ffmpeg("-f lavfi -i testsrc=size=200x120:rate=25:duration=2 -c:v libx264 \"" + b + "\"");
        var cut = Video.Run(new VideoJob { Task = VideoTask.Trim, Start = 1, End = 2, Exact = true }, a, "kesik", p => { },
            () => false);
        Expect(Math.Abs(Video.Probe(cut.File).Seconds - 1) < 0.2, "kesme");
        var copied = Video.Run(new VideoJob { Task = VideoTask.Trim, Start = 1, End = 2 }, a, "hizli", p => { }, () => false);
        Expect(copied.File != null && Video.Probe(copied.File).Seconds > 0.5, "hizli kesme");
        var same = Video.Merge(new[] { a, a }, "ayni", p => { }, () => false);
        Expect(Math.Abs(Video.Probe(same.File).Seconds - 6) < 0.4 && same.Text.Contains("kalite"), "kayipsiz birlestirme");
        var mixed = Video.Merge(new[] { a, b }, "karisik", p => { }, () => false);
        var made = Video.Probe(mixed.File);
        Expect(Math.Abs(made.Seconds - 5) < 0.4 && made.Width == 320 && made.Height == 240 && made.AudioCodec != null,
            "farkli videolari birlestirme");
        Expect(Tools.For(new[] { a }).Length == 5 && Tools.For(new[] { a, b }).Length == 5, "video menusu");
        var trim = new VideoWindow(new[] { a }, new VideoJob { Task = VideoTask.Trim }, false);
        Expect(trim.ReadJob() == null, "kesme penceresi");
        trim.Window.Close();
        var merge = new VideoWindow(new[] { a, b }, new VideoJob { Task = VideoTask.Merge }, false);
        Expect(merge.ReadJob() == null, "birlestirme penceresi");
        merge.Window.Close();
    }

    // The smallest honest PDF: blank pages and a correct table of where its parts begin.
    static void MakePdf(string path, int pages)
    {
        var parts = new List<string> { "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Count " + pages + " /Kids [" + string.Join(" ", Enumerable.Range(3, pages).Select(i => i + " 0 R"))
                + "] >>" };
        parts.AddRange(Enumerable.Repeat("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 300] >>", pages));
        var text = new StringBuilder("%PDF-1.4\n");
        var at = new List<int>();
        for (int i = 0; i < parts.Count; i++)
        {
            at.Add(text.Length);
            text.Append((i + 1) + " 0 obj\n" + parts[i] + "\nendobj\n");
        }
        int table = text.Length;
        text.Append("xref\n0 " + (parts.Count + 1) + "\n0000000000 65535 f \n");
        foreach (var offset in at) text.Append(offset.ToString("0000000000") + " 00000 n \n");
        text.Append("trailer\n<< /Size " + (parts.Count + 1) + " /Root 1 0 R >>\nstartxref\n" + table + "\n%%EOF\n");
        File.WriteAllText(path, text.ToString(), Encoding.ASCII);
    }

    static void Pdfs(string dir)
    {
        Expect(Pdf.Range("1-3, 5", 5) == "1-3,5" && Pdf.Range("3-1", 3) == "3-1" && Pdf.Range("0", 3) == null
            && Pdf.Range("4", 3) == null && Pdf.Range("1;2", 3) == null && Pdf.Range("", 3) == null
            && Pdf.Range("1 --", 3) == null, "sayfa araligi");
        string one = Path.Combine(dir, "bir ğ.pdf"), two = Path.Combine(dir, "iki.pdf");
        MakePdf(one, 3);
        MakePdf(two, 1);
        var document = Pdf.Open(one);
        var page = Pdf.Render(document, 0, 100);
        Expect(document.PageCount == 3 && page.PixelWidth == 100 && page.PixelHeight == 150, "pdf goruntuleme");
        if (!Pdf.Available) return;
        var merged = Pdf.Merge(new[] { one, two }, "hepsi");
        Expect(Pdf.Pages(merged.File) == 4, "pdf birlestirme");
        var again = Pdf.Merge(new[] { one, two }, "hepsi");
        Expect(again.File != merged.File, "pdf ad cakismasi");
        Expect(Pdf.Pages(Pdf.Extract(one, "2-3", "parca").File) == 2, "pdf sayfa alma");
        var split = Pdf.SplitEach(one);
        Expect(Directory.GetFiles(Path.GetDirectoryName(split.File), "*.pdf").Length == 3, "pdf tek tek");
        string bad = Path.Combine(dir, "bozuk.pdf");
        File.WriteAllText(bad, "pdf degil");
        try
        {
            Pdf.Merge(new[] { bad, one }, "olmaz");
            Expect(false, "bozuk pdf");
        }
        catch (IOException) { Expect(!File.Exists(Path.Combine(dir, "olmaz.pdf")), "yarim pdf silinir"); }
        Expect(Tools.For(new[] { one, two }).Length == 6 && Tools.For(new[] { one }).Length == 7, "pdf menusu");
        new PdfWindow(new[] { one }).Window.Close();
        new PdfWindow(new[] { one, two }).Window.Close();
    }

    static void Tables()
    {
        var rows = new[] { new[] { "a;b", "=1" }, new[] { "de \"x\"", "-5" } };
        Expect(TableText.Csv(rows, ";") == "\"a;b\";'=1\r\n\"de \"\"x\"\"\";-5", "csv");
        Expect(TableWindow.Without(rows, 0).All(r => r.Length == 1) && TableWindow.Without(rows, 0)[1][0] == "-5", "sutun silme");
        var picture = System.Windows.Media.Imaging.BitmapSource.Create(1, 1, 96, 96,
            System.Windows.Media.PixelFormats.Bgra32, null, new byte[4], 4);
        var window = new TableWindow(rows, picture);
        Expect(TableText.Tsv(window.Rows()) == TableText.Tsv(rows), "tablo penceresi");
        window.Window.Close();
    }

    static void Previews(string dir)
    {
        Func<string, byte[], string> file = (name, data) =>
        {
            string path = Path.Combine(dir, name);
            File.WriteAllBytes(path, data);
            return path;
        };
        string text = file("not.txt", Encoding.UTF8.GetBytes("çğ ışık"));
        string old = file("eski.txt", Encoding.Default.GetBytes("é"));
        string unknown = file("ayar.xyz", Encoding.ASCII.GetBytes("a=1"));
        string binary = file("veri.bin", new byte[] { 1, 0, 2 });
        string longer = file("uzun.log", Encoding.UTF8.GetBytes(new string('ş', 150 * 1024)));
        Expect(Preview.Of(dir) == ItemKind.Folder && Preview.Of(text) == ItemKind.Text && Preview.Of(unknown) == ItemKind.Text
            && Preview.Of(binary) == ItemKind.Other && Preview.Of(file("a.pdf", new byte[1])) == ItemKind.Pdf, "tur secimi");
        string cut = Preview.ReadText(longer);
        Expect(Preview.ReadText(text) == "çğ ışık" && Preview.ReadText(old) == "é" && cut.StartsWith("şşş") && !cut.Contains("�")
            && cut.Length < 110 * 1024, "metin okuma");
        string docx = Path.Combine(dir, "b.docx");
        using (var archive = new ZipArchive(File.Create(docx), ZipArchiveMode.Create))
        using (var writer = new StreamWriter(archive.CreateEntry("word/document.xml").Open(), new UTF8Encoding(false)))
            writer.Write("<w:document xmlns:w=\"x\"><w:body><w:p><w:r><w:t>Merhaba</w:t></w:r><w:r><w:tab/><w:t>dünya</w:t></w:r>"
                + "</w:p><w:p/><w:p><w:r><w:t>son</w:t></w:r></w:p></w:body></w:document>");
        Expect(Preview.ReadDocument(docx) == "Merhaba\tdünya\n\nson\n", "belge okuma");
        Expect(Preview.ReadArchive(docx).StartsWith("word/document.xml"), "arsiv listesi");
        new Preview(new[] { text, binary }).Window.Close();
    }
}

// Saved recipes that resize, convert and shrink a picture in one step with a single save.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Win32;
using Path = System.IO.Path;

class Preset
{
    public string Name = "", Format = ""; // Format "": the picture keeps its own
    public int MaxSide, LimitKb; // 0: no limit
    public int Quality = 82;

    public string Line { get { return string.Join("|", new object[] { Name, MaxSide, Format, LimitKb, Quality }); } }

    public static Preset Parse(string line)
    {
        var parts = line.Split('|');
        var preset = new Preset();
        if (parts.Length != 5 || !int.TryParse(parts[1], out preset.MaxSide) || !int.TryParse(parts[3], out preset.LimitKb)
            || !int.TryParse(parts[4], out preset.Quality)) return null;
        preset.Name = parts[0];
        preset.Format = parts[2];
        return preset.Problem() == null && preset.NameProblem() == null ? preset : null;
    }

    // Why the numbers cannot be used, or null.
    public string Problem()
    {
        if (MaxSide != 0 && (MaxSide < 16 || MaxSide > 20000)) return "En uzun kenar 16 ile 20000 arasında olmalı (0: değiştirme).";
        if (LimitKb != 0 && LimitKb < 5) return "Boyut sınırı en az 5 KB olmalı (0: sınırsız).";
        if (Quality < 10 || Quality > 100) return "Kalite 10 ile 100 arasında olmalı.";
        return Format == "" || Format == ".jpg" || Format == ".png" || Format == ".webp" ? null : "Bilinmeyen biçim.";
    }

    public string NameProblem()
    {
        if (string.IsNullOrWhiteSpace(Name)) return "Ayara bir ad ver.";
        return Name.Contains("|") || Name.Length > 20 ? "Ayar adı en fazla 20 karakter olabilir, | içeremez." : null;
    }

    // The part of the file name that tells which recipe made it.
    public string Suffix
    {
        get
        {
            var bad = Path.GetInvalidFileNameChars();
            string clean = new string(Name.Trim().ToLower(new System.Globalization.CultureInfo("tr-TR"))
                .Where(c => !bad.Contains(c) && c != '.').Select(c => c == ' ' ? '-' : c).ToArray());
            return "_" + (clean == "" ? "hazir" : clean);
        }
    }
}

static class Presets
{
    public const int Most = 6; // as many as the outer ring of the menu holds
    const string Value = "Presets";

    public static List<Preset> All()
    {
        var lines = Registry.GetValue(@"HKEY_CURRENT_USER\" + Prefs.Store, Value, null) as string[];
        if (lines != null) return lines.Select(Preset.Parse).Where(p => p != null).Take(Most).ToList();
        return new List<Preset>
        {
            new Preset { Name = "Web", MaxSide = 1920, Format = ".webp", LimitKb = 500 },
            new Preset { Name = "E-posta", MaxSide = 1600, Format = ".jpg", LimitKb = 300 },
            new Preset { Name = "Küçük", MaxSide = 640, Format = ".jpg" }
        };
    }

    static void Store(IEnumerable<Preset> presets)
    {
        Registry.SetValue(@"HKEY_CURRENT_USER\" + Prefs.Store, Value, presets.Select(p => p.Line).ToArray(), RegistryValueKind.MultiString);
    }

    // Replaces the preset of the same name, or adds a new one.
    public static void Put(Preset preset)
    {
        string problem = preset.NameProblem() ?? preset.Problem();
        if (problem != null) throw new ArgumentException(problem);
        var all = All();
        int at = all.FindIndex(p => string.Equals(p.Name, preset.Name, StringComparison.CurrentCultureIgnoreCase));
        if (at >= 0) all[at] = preset;
        else if (all.Count >= Most) throw new ArgumentException("En fazla " + Most + " hazır ayar olabilir; önce birini sil.");
        else all.Add(preset);
        Store(all);
    }

    public static void Remove(string name)
    {
        Store(All().Where(p => !string.Equals(p.Name, name, StringComparison.CurrentCultureIgnoreCase)));
    }

    public static string Extension(Preset preset, string path, Picture picture)
    {
        string ext = preset.Format == "" ? Images.Writable(path) : preset.Format;
        if (ext == ".webp" && Images.Ffmpeg == null) ext = ".jpg";
        // A clear background is never traded for a format that cannot hold it.
        return picture.Transparent && (ext == ".jpg" || ext == ".jpeg" || ext == ".bmp") ? ".png" : ext;
    }

    public static Candidate Make(Preset preset, Picture picture, string ext)
    {
        var pixels = picture.Pixels;
        int longest = Math.Max(pixels.PixelWidth, pixels.PixelHeight);
        if (preset.MaxSide > 0 && longest > preset.MaxSide)
        {
            double percent = preset.MaxSide * 100.0 / longest;
            pixels = Images.Resize(pixels, Images.Percent(pixels.PixelWidth, percent), Images.Percent(pixels.PixelHeight, percent));
        }
        var found = new Candidate { Data = Images.Encode(pixels, picture.Colors, ext, preset.Quality), Quality = preset.Quality,
            Width = pixels.PixelWidth, Height = pixels.PixelHeight };
        // The chosen quality unless that is too big; then whatever fits.
        if (preset.LimitKb > 0 && found.Data.Length > preset.LimitKb * 1024L)
            found = Images.UnderSize(pixels, picture.Colors, ext, preset.LimitKb * 1024L);
        return found;
    }

    // Why a picture is left alone: it already is what the recipe would make of it.
    public static string Skip(Preset preset, string path, Picture picture, string ext, Candidate found)
    {
        bool same = Images.Kind(Path.GetExtension(path)) == Images.Kind(ext)
            && (preset.MaxSide == 0 || Math.Max(picture.Pixels.PixelWidth, picture.Pixels.PixelHeight) <= preset.MaxSide);
        if (!same) return null;
        long size = new FileInfo(path).Length;
        return found.Data.Length >= size || (preset.LimitKb > 0 && size <= preset.LimitKb * 1024L)
            ? "Dosya zaten bu ayara uygun." : null;
    }

    // The one-click option of the menu.
    public static Outcome Apply(Preset preset, string path)
    {
        var picture = Images.Load(path);
        string ext = Extension(preset, path, picture);
        var found = Make(preset, picture, ext);
        string skip = Skip(preset, path, picture, ext, found);
        if (skip != null) return new Outcome { Text = skip };
        return Images.SaveEncoded(path, found.Data, ext, Naming.Default(path, preset.Suffix), false);
    }
}

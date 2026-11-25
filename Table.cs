// A table on the screen as rows and columns, ready to paste into a spreadsheet. Only the places of the
// recognised words are used, so tables with and without ruling lines are treated alike.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

// A recognised word and where it stands in the picture.
struct Word
{
    public string Text;
    public double X, Y, Width, Height;
    public double Middle { get { return Y + Height / 2; } }
}

// shortcut: columns are told apart by clear space. Cells closer than about a letter's height are joined,
// a cell wrapped onto a second line becomes a second row, and a cell spanning columns joins them; reading
// the ruling lines would be needed for those.
static class TableText
{
    class Cell
    {
        public string Text;
        public double Left, Right;
    }

    // The engine misreads a character at one size and gets it right at another, so the picture is read at
    // three sizes (median word height in pixels, the preferred one first) and the readings vote.
    static readonly double[] Heights = { 24, 20, 32 };

    public static string[][] Read(Picture picture)
    {
        var first = Ocr.Words(picture, 2);
        if (first.Length == 0) return new string[0][];
        double height = first.Select(w => w.Height).OrderBy(h => h).ElementAt(first.Length / 2) / 2; // in the picture
        return Agree(Heights.Select(wanted => Rows(Ocr.Words(picture, Math.Max(0.5, Math.Min(4, wanted / height)))))
            .ToList());
    }

    // Among the readings with the most common shape, every cell takes its most common text; a text beats a gap,
    // and the earlier reading wins a tie.
    public static string[][] Agree(List<string[][]> readings)
    {
        var alike = readings.Where(r => r.Length > 0)
            .GroupBy(r => r.Length + "x" + r[0].Length).OrderByDescending(g => g.Count()).FirstOrDefault();
        if (alike == null) return new string[0][];
        var shape = alike.First();
        return shape.Select((row, y) => row.Select((cell, x) => alike.Select(r => r[y][x]).Where(t => t != "")
            .GroupBy(t => t).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault() ?? "").ToArray())
            .ToArray();
    }

    // Rows from top to bottom, every one with the same number of columns.
    public static string[][] Rows(IEnumerable<Word> words)
    {
        var all = words.OrderBy(w => w.Middle).ToList();
        if (all.Count == 0) return new string[0][];
        double letter = all.Select(w => w.Height).OrderBy(h => h).ElementAt(all.Count / 2);

        // A row: words whose middles are close.
        var lines = new List<List<Word>>();
        foreach (var word in all)
        {
            if (lines.Count == 0 || word.Middle - lines[lines.Count - 1].Average(w => w.Middle) > letter * 0.6)
                lines.Add(new List<Word>());
            lines[lines.Count - 1].Add(word);
        }

        // A cell: words of a row with no more than an ordinary space between them.
        var rows = lines.Select(line =>
        {
            var cells = new List<Cell>();
            foreach (var word in line.OrderBy(w => w.X))
            {
                var last = cells.LastOrDefault();
                if (last != null && word.X - last.Right < letter * 0.9)
                {
                    // Digits the engine has split, as in "1 5,00" or "4, 50", are one number.
                    bool number = Regex.IsMatch(last.Text, @"^[+\-]?\d+$") && Regex.IsMatch(word.Text, @"^\d+[.,]\d+$")
                        || Regex.IsMatch(last.Text, @"^[+\-]?\d+[.,]$") && Regex.IsMatch(word.Text, @"^\d+$");
                    last.Text += (number ? "" : " ") + word.Text;
                    last.Right = Math.Max(last.Right, word.X + word.Width);
                }
                else cells.Add(new Cell { Text = word.Text, Left = word.X, Right = word.X + word.Width });
            }
            return cells;
        }).ToList();

        // A column: a stretch covered by cells. A row with a single cell, such as a title, must not join columns.
        var telling = rows.Where(r => r.Count > 1).SelectMany(r => r).ToList();
        if (telling.Count == 0) telling = rows.SelectMany(r => r).ToList();
        var columns = new List<Cell>();
        foreach (var cell in telling.OrderBy(c => c.Left))
        {
            var last = columns.LastOrDefault();
            if (last == null || cell.Left > last.Right) columns.Add(new Cell { Left = cell.Left, Right = cell.Right });
            else last.Right = Math.Max(last.Right, cell.Right);
        }

        return rows.Select(cells =>
        {
            var row = Enumerable.Repeat("", columns.Count).ToArray();
            foreach (var cell in cells)
            {
                // The column it overlaps most; failing that, the nearest one (a negative overlap is a distance).
                int at = Enumerable.Range(0, columns.Count)
                    .OrderByDescending(i => Math.Min(cell.Right, columns[i].Right) - Math.Max(cell.Left, columns[i].Left))
                    .First();
                row[at] = row[at] == "" ? cell.Text : row[at] + " " + cell.Text;
            }
            return row;
        }).ToArray();
    }

    // Columns apart by tabs: what spreadsheets take from the clipboard.
    public static string Tsv(string[][] rows)
    {
        return string.Join("\r\n", rows.Select(row => string.Join("\t", row.Select(Safe))));
    }

    // Text read from the screen must not run as a formula when it is pasted; plain signed numbers stay as they are.
    static string Safe(string cell)
    {
        bool formula = cell.Length > 0 && "=@+-".IndexOf(cell[0]) >= 0
            && !Regex.IsMatch(cell, @"^[+\-][\d\s.,%₺$€£]*(TL)?$");
        return formula ? "'" + cell : cell;
    }
}

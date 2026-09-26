// Prints a PDF as text with page markers, the way the product's parser would (docs/02 names PdfPig).
//   PdfPigCheck <file.pdf>            PdfPig's own content-order extractor
//   PdfPigCheck <file.pdf> --logical  our visual-to-logical pass: lines built from glyph positions,
//                                     Arabic lines read right to left, Latin and digit runs kept left to right,
//                                     each glyph's text kept whole so a lam-alef ligature is not split
using System.Text;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

Console.OutputEncoding = Encoding.UTF8;
var logical = args.Contains("--logical");
using var pdf = PdfDocument.Open(args[0]);
foreach (var page in pdf.GetPages())
{
    Console.WriteLine($"<!-- page {page.Number} -->");
    Console.WriteLine(logical ? LogicalText(page) : ContentOrderTextExtractor.GetText(page));
    Console.WriteLine();
}

static string LogicalText(Page page)
{
    var sb = new StringBuilder();
    var glyphs = page.Letters.Where(l => !string.IsNullOrWhiteSpace(l.Value)).ToList();
    // Lines: glyphs whose baselines are within a third of the font size.
    var lines = new List<List<Letter>>();
    foreach (var g in glyphs.OrderByDescending(g => g.StartBaseLine.Y))
    {
        var line = lines.LastOrDefault();
        if (line != null && Math.Abs(line[0].StartBaseLine.Y - g.StartBaseLine.Y) < g.PointSize / 3) line.Add(g);
        else lines.Add([g]);
    }
    foreach (var line in lines)
    {
        var arabic = line.Count(g => IsArabic(g.Value)) * 2 > line.Count(g => !IsDigitOrLatin(g.Value));
        var ordered = arabic ? line.OrderByDescending(g => g.StartBaseLine.X).ToList()
                             : line.OrderBy(g => g.StartBaseLine.X).ToList();
        // Tokens are glyph values; a gap wider than a quarter of the font size is a space.
        var tokens = new List<string>();
        for (var i = 0; i < ordered.Count; i++)
        {
            if (i > 0)
            {
                var (a, b) = arabic ? (ordered[i], ordered[i - 1]) : (ordered[i - 1], ordered[i]);
                if (b.GlyphRectangle.Left - a.GlyphRectangle.Right > a.PointSize / 4) tokens.Add(" ");
            }
            tokens.Add(ordered[i].Value);
        }
        if (arabic) ReverseLatinRuns(tokens);
        sb.AppendLine(string.Concat(tokens));
    }
    return sb.ToString();
}

// In a right-to-left line, runs of Latin letters and digits (with spaces and punctuation inside them) read left to right.
static void ReverseLatinRuns(List<string> tokens)
{
    var i = 0;
    while (i < tokens.Count)
    {
        if (!IsDigitOrLatin(tokens[i])) { i++; continue; }
        var j = i;
        while (j + 1 < tokens.Count && (IsDigitOrLatin(tokens[j + 1]) || (IsNeutral(tokens[j + 1]) && j + 2 < tokens.Count && IsDigitOrLatin(tokens[j + 2])))) j++;
        tokens.Reverse(i, j - i + 1);
        i = j + 1;
    }
}

static bool IsArabic(string s) => Regex.IsMatch(s, @"[؀-ۿﭐ-﷿ﹰ-﻿]");
static bool IsDigitOrLatin(string s) => Regex.IsMatch(s, @"^[A-Za-z0-9]+$");
static bool IsNeutral(string s) => Regex.IsMatch(s, @"^[\s./\-:]+$");

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Interchanges
{
    /// <summary>
    /// The interchanges' tabs on the station booklet's tab column (sprite MapSchematicNavigator):
    /// a copy of the column with a tab painted after the stock ones for each interchange
    /// (TabPixels), as a sprite like the stock one, so every page shows it in the same place.
    /// </summary>
    internal static class TabColumn
    {
        public static Sprite Paint(Sprite stock, IList<string> stations, IList<(string Code, Color32 Colour)> tabs, List<string> notes)
        {
            if (stock == null)
                return null;
            var source = stock.texture;
            if (stock.rect.x != 0 || stock.rect.y != 0 || (int)stock.rect.width != source.width || (int)stock.rect.height != source.height)
            {
                notes.Add($"tab column: sprite {stock.name} is not its whole texture; no tabs added");
                return null;
            }
            var tex = MapPicture.Readable(source, source.name + " (interchanges)", source.mipmapCount > 1);
            var pixels = tex.GetPixels32();
            var rgba = new byte[pixels.Length * 4];
            for (var i = 0; i < pixels.Length; i++)
            {
                rgba[4 * i] = pixels[i].r;
                rgba[4 * i + 1] = pixels[i].g;
                rgba[4 * i + 2] = pixels[i].b;
                rgba[4 * i + 3] = pixels[i].a;
            }
            var painter = new TabPixels(rgba, tex.width, tex.height);
            if (!painter.Measure(stations, notes))
            {
                UnityEngine.Object.Destroy(tex);
                return null;
            }
            for (var k = 0; k < tabs.Count; k++)
                painter.Paint(stations.Count + k, tabs[k].Code, new[] { tabs[k].Colour.r, tabs[k].Colour.g, tabs[k].Colour.b }, notes);
            for (var i = 0; i < pixels.Length; i++)
                pixels[i] = new Color32(rgba[4 * i], rgba[4 * i + 1], rgba[4 * i + 2], rgba[4 * i + 3]);
            tex.SetPixels32(pixels);
            tex.Apply(true, true);
            var pivot = new Vector2(stock.pivot.x / stock.rect.width, stock.pivot.y / stock.rect.height);
            var sprite = Sprite.Create(tex, stock.rect, pivot, stock.pixelsPerUnit, 0, SpriteMeshType.FullRect, stock.border);
            sprite.name = stock.name + " (interchanges)";
            return sprite;
        }
    }

    /// <summary>
    /// Tabs painted onto the tab column's pixels (RGBA, rows from the bottom, as Unity's). The
    /// stock column is a strip of equal tabs, one per station in the booklet's order, on a grey
    /// ground with room after the last; each tab is its station's colour from edge to edge,
    /// with its code in light letters in one or two rows ("CP"; "IM" over "E").
    ///
    /// A new tab takes the next place in the strip and the interchange's colour. Its code is
    /// set in the column's own letters: each stock tab's label is known (StockLabels), so its
    /// letters are found by their rows and gaps, lifted off its colour as coverage (how far each
    /// pixel goes from the tab's colour toward the letters'), and laid on the new colour, in the
    /// rows and spacing the stock labels use. Codes of three letters take two rows, the first
    /// two over the third, as the stock ones do.
    /// </summary>
    internal sealed class TabPixels
    {
        /// <summary>The stock tabs' labels, row by row (the column's picture shows them so).</summary>
        public static readonly Dictionary<string, string[]> StockLabels = new Dictionary<string, string[]>
        {
            ["CME"] = new[] { "CM", "E" }, ["CMS"] = new[] { "CM", "S" }, ["CP"] = new[] { "CP" }, ["CS"] = new[] { "C", "S" },
            ["CW"] = new[] { "C", "W" }, ["FF"] = new[] { "FF" }, ["FM"] = new[] { "FM" }, ["FRC"] = new[] { "FR", "C" },
            ["FRS"] = new[] { "FR", "S" }, ["GF"] = new[] { "GF" }, ["HB"] = new[] { "HB" }, ["IME"] = new[] { "IM", "E" },
            ["IMW"] = new[] { "IM", "W" }, ["MB"] = new[] { "MB" }, ["MF"] = new[] { "MF" }, ["OR"] = new[] { "OR" },
            ["OWC"] = new[] { "OW", "C" }, ["OWN"] = new[] { "OW", "N" }, ["SM"] = new[] { "SM" }, ["SW"] = new[] { "SW" },
        };

        private static readonly float[] Ink = { 222f, 219f, 222f };   // the labels' light grey
        private const int GroundRow = 4;     // a row below the labels: plain tab colours on the grey ground
        private const int GroundTolerance = 12;
        private const int LetterGap = 4;     // between letters in a row, as the stock labels have it

        private sealed class Glyph
        {
            public int Width, Height;        // the coverage box: the letter and a pixel round it
            public float[] Cover;
        }

        private readonly byte[] px;
        private readonly int width, height;
        private readonly Dictionary<char, Glyph> glyphs = new Dictionary<char, Glyph>();
        private readonly List<int> rowBottoms = new List<int>();   // each label row's lowest pixel row, top row first
        private int left, tabWidth, stockTabs;
        private byte[] ground;

        public TabPixels(byte[] rgba, int width, int height)
        {
            px = rgba;
            this.width = width;
            this.height = height;
        }

        public int Left => left;
        public int TabWidth => tabWidth;
        public IEnumerable<char> Letters => glyphs.Keys;

        private int At(int x, int y) => 4 * (y * width + x);

        private int Distance(int x, int y, byte[] c)
        {
            var i = At(x, y);
            return Math.Max(Math.Abs(px[i] - c[0]), Math.Max(Math.Abs(px[i + 1] - c[1]), Math.Abs(px[i + 2] - c[2])));
        }

        private byte[] ColourAt(int x, int y)
        {
            var i = At(x, y);
            return new[] { px[i], px[i + 1], px[i + 2] };
        }

        /// <summary>Finds the strip of stock tabs and learns their letters. False if the column is not as expected.</summary>
        public bool Measure(IList<string> stations, List<string> notes)
        {
            stockTabs = stations.Count;
            ground = ColourAt(0, GroundRow);
            int first = -1, last = -1;
            for (var x = 0; x < width; x++)
                if (Distance(x, GroundRow, ground) > GroundTolerance)
                {
                    if (first < 0)
                        first = x;
                    last = x;
                }
            var span = last - first + 1;
            if (first < 0 || stockTabs == 0 || span % stockTabs > 2 && stockTabs - span % stockTabs > 2)
            {
                notes.Add($"tab column: {span} px of tabs for {stockTabs} stations; no tabs added");
                return false;
            }
            left = first;
            tabWidth = (int)Math.Round(span / (double)stockTabs);
            for (var i = 0; i < stockTabs; i++)
                if (StockLabels.TryGetValue(stations[i], out var label))
                    Learn(i, label);
            notes.Add($"tab column: {stockTabs} tabs of {tabWidth} px from x {left}; letters {new string(glyphs.Keys.OrderBy(c => c).ToArray())}; " +
                      $"label rows from y {string.Join(", ", rowBottoms)}");
            return true;
        }

        /// <summary>How far a pixel goes from the tab's colour toward the letters' (0 to 1).</summary>
        private float Cover(int x, int y, byte[] tab)
        {
            var i = At(x, y);
            float dot = 0f, len = 0f;
            for (var c = 0; c < 3; c++)
            {
                var d = Ink[c] - tab[c];
                dot += (px[i + c] - tab[c]) * d;
                len += d * d;
            }
            return len < 1f ? 0f : Math.Max(0f, Math.Min(1f, dot / len));
        }

        private static List<(int Lo, int Hi)> Runs(IEnumerable<int> indices, int maxGap)
        {
            var runs = new List<(int Lo, int Hi)>();
            foreach (var i in indices.OrderBy(v => v))
            {
                if (runs.Count > 0 && i - runs[runs.Count - 1].Hi <= maxGap)
                    runs[runs.Count - 1] = (runs[runs.Count - 1].Lo, i);
                else
                    runs.Add((i, i));
            }
            return runs;
        }

        private void Learn(int tab, string[] label)
        {
            int x0 = left + tab * tabWidth, x1 = x0 + tabWidth - 1;
            var colour = ColourAt(x0 + tabWidth / 2, GroundRow);
            var inked = Enumerable.Range(0, height).Where(y => Enumerable.Range(x0, tabWidth).Any(x => Cover(x, y, colour) > 0.5f));
            // Rows of letters, the top one first (a texture's rows count up from the bottom).
            var bands = Runs(inked, 2).OrderByDescending(b => b.Lo).ToList();
            if (bands.Count != label.Length)
                return;
            for (var r = 0; r < bands.Count; r++)
            {
                var (lo, hi) = bands[r];
                var columns = Enumerable.Range(x0, tabWidth).Where(x => Enumerable.Range(lo, hi - lo + 1).Any(y => Cover(x, y, colour) > 0.5f));
                var letters = Runs(columns, 1);
                if (letters.Count != label[r].Length)
                    return;
                if (label.Length == 2 && rowBottoms.Count == 0)
                    rowBottoms.AddRange(bands.Select(b => b.Lo));
                for (var k = 0; k < letters.Count; k++)
                {
                    var ch = label[r][k];
                    if (glyphs.ContainsKey(ch))
                        continue;
                    int gx0 = Math.Max(x0, letters[k].Lo - 1), gx1 = Math.Min(x1, letters[k].Hi + 1);
                    int gy0 = Math.Max(0, lo - 1), gy1 = Math.Min(height - 1, hi + 1);
                    var g = new Glyph { Width = gx1 - gx0 + 1, Height = gy1 - gy0 + 1 };
                    g.Cover = new float[g.Width * g.Height];
                    for (var y = 0; y < g.Height; y++)
                        for (var x = 0; x < g.Width; x++)
                            g.Cover[y * g.Width + x] = Cover(gx0 + x, gy0 + y, colour);
                    glyphs[ch] = g;
                }
            }
        }

        /// <summary>Paints tab number `index` (after the stock ones) in a colour, with a code in the column's letters.</summary>
        public void Paint(int index, string code, byte[] colour, List<string> notes)
        {
            int x0 = left + index * tabWidth, x1 = x0 + tabWidth - 1;
            if (index < stockTabs || x1 > width - 1)
            {
                notes.Add($"tab column: no room for tab {index} ({code})");
                return;
            }
            if (Enumerable.Range(x0, tabWidth).Any(x => Distance(x, GroundRow, ground) > GroundTolerance))
            {
                notes.Add($"tab column: tab {index} ({code}) would cover something; not painted");
                return;
            }
            for (var y = 0; y < height; y++)
                for (var x = x0; x <= x1; x++)
                {
                    var i = At(x, y);
                    px[i] = colour[0];
                    px[i + 1] = colour[1];
                    px[i + 2] = colour[2];
                    px[i + 3] = 255;
                }
            var rows = code.Length > 2 ? new[] { code.Substring(0, 2), code.Substring(2) } : new[] { code };
            var missing = new string(code.Where(c => !glyphs.ContainsKey(c)).Distinct().ToArray());
            if (missing.Length > 0 || rows.Length > rowBottoms.Count)
            {
                notes.Add($"tab column: tab {index} ({code}) painted without its code (letters missing: '{missing}')");
                return;
            }
            for (var r = 0; r < rows.Length; r++)
            {
                var letters = rows[r].Select(c => glyphs[c]).ToList();
                // Each glyph's box has a pixel's margin on either side of its letter.
                var inner = letters.Sum(g => g.Width - 2) + LetterGap * (letters.Count - 1);
                var x = x0 + (tabWidth - inner) / 2 - 1;
                var yBottom = rowBottoms[r] - 1;
                foreach (var g in letters)
                {
                    for (var gy = 0; gy < g.Height; gy++)
                        for (var gx = 0; gx < g.Width; gx++)
                        {
                            int tx = x + gx, ty = yBottom + gy;
                            if (tx < x0 || tx > x1 || ty < 0 || ty >= height)
                                continue;
                            var a = g.Cover[gy * g.Width + gx];
                            var i = At(tx, ty);
                            for (var c = 0; c < 3; c++)
                                px[i + c] = (byte)Math.Round(px[i + c] + (Ink[c] - px[i + c]) * a);
                        }
                    x += g.Width - 2 + LetterGap;
                }
            }
            notes.Add($"tab column: tab {index} painted for {code}");
        }
    }
}

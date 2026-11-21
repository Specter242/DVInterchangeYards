using System.Collections.Generic;
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
            if (source == null)
                return null;
            if (stock.rect.x != 0 || stock.rect.y != 0 || (int)stock.rect.width != source.width || (int)stock.rect.height != source.height)
            {
                notes.Add($"tab column: sprite {stock.name} is not its whole texture; no tabs added");
                return null;
            }
            var tex = MapPicture.Readable(source, source.name + " (interchanges)", source.mipmapCount > 1);
            Sprite sprite = null;
            try
            {
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
                    return null;
                for (var k = 0; k < tabs.Count; k++)
                    painter.Paint(stations.Count + k, tabs[k].Code, new[] { tabs[k].Colour.r, tabs[k].Colour.g, tabs[k].Colour.b }, notes);
                for (var i = 0; i < pixels.Length; i++)
                    pixels[i] = new Color32(rgba[4 * i], rgba[4 * i + 1], rgba[4 * i + 2], rgba[4 * i + 3]);
                tex.SetPixels32(pixels);
                tex.Apply(true, true);
                var pivot = new Vector2(stock.pivot.x / stock.rect.width, stock.pivot.y / stock.rect.height);
                sprite = Sprite.Create(tex, stock.rect, pivot, stock.pixelsPerUnit, 0, SpriteMeshType.FullRect, stock.border);
                sprite.name = stock.name + " (interchanges)";
                var result = sprite;
                sprite = null;
                tex = null;
                return result;
            }
            finally
            {
                if (sprite != null)
                    UnityEngine.Object.Destroy(sprite);
                if (tex != null)
                    UnityEngine.Object.Destroy(tex);
            }
        }
    }
}

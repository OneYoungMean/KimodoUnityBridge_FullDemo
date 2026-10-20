using System;
using System.Linq;
using UnityEngine;

namespace KimodoUnityBridge.Command
{
    internal static partial class command_context
    {
        // Rasterize Unity's font at 4x size; coverage sampling keeps small exported labels smooth.
        private sealed class SceneCaptureLabels : IDisposable
        {
            private const int Samples = 4;
            private readonly Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            private Texture2D atlas;
            private Color32[] pixels;

            public void Draw(Texture2D canvas, RectInt rect, string text, Color color)
            {
                if (rect.width < 8 || rect.height < 4 || string.IsNullOrEmpty(text)) return;
                int size = Mathf.Max(Samples, Mathf.RoundToInt(rect.height * .52f) * Samples);
                CharacterInfo[] glyphs;
                int advance;
                while (true)
                {
                    font.RequestCharactersInTexture(text, size, FontStyle.Normal);
                    glyphs = text.Select(character =>
                    {
                        font.GetCharacterInfo(character, out CharacterInfo glyph, size, FontStyle.Normal);
                        return glyph;
                    }).ToArray();
                    advance = glyphs.Sum(glyph => glyph.advance);
                    if (advance <= rect.width * .92f * Samples || size <= Samples) break;
                    size = Mathf.Max(Samples, Mathf.FloorToInt(size * rect.width * .9f * Samples / advance));
                }
                ReadAtlas();
                float pen = rect.x + (rect.width - advance / (float)Samples) * .5f;
                float baseline = rect.y + rect.height * .5f - (glyphs.Min(glyph => glyph.minY) + glyphs.Max(glyph => glyph.maxY)) / (2f * Samples);
                foreach (CharacterInfo glyph in glyphs)
                {
                    float left = pen + glyph.minX / (float)Samples, right = pen + glyph.maxX / (float)Samples;
                    float bottom = baseline + glyph.minY / (float)Samples, top = baseline + glyph.maxY / (float)Samples;
                    for (int y = Mathf.Max(rect.y, Mathf.FloorToInt(bottom)); y < Mathf.Min(rect.yMax, Mathf.CeilToInt(top)); y++)
                    for (int x = Mathf.Max(rect.x, Mathf.FloorToInt(left)); x < Mathf.Min(rect.xMax, Mathf.CeilToInt(right)); x++)
                    {
                        float coverage = 0;
                        for (int sy = 0; sy < Samples; sy++)
                        for (int sx = 0; sx < Samples; sx++)
                        {
                            float u = (x + (sx + .5f) / Samples - left) / (right - left);
                            float v = (y + (sy + .5f) / Samples - bottom) / (top - bottom);
                            if (u < 0 || u > 1 || v < 0 || v > 1) continue;
                            Vector2 uv = Vector2.Lerp(Vector2.Lerp(glyph.uvBottomLeft, glyph.uvBottomRight, u),
                                Vector2.Lerp(glyph.uvTopLeft, glyph.uvTopRight, u), v);
                            coverage += Alpha(uv);
                        }
                        if (coverage > 0)
                            canvas.SetPixel(x, y, Color.Lerp(canvas.GetPixel(x, y), color, coverage * color.a / (Samples * Samples)));
                    }
                    pen += glyph.advance / (float)Samples;
                }
            }

            private float Alpha(Vector2 uv)
            {
                float x = Mathf.Clamp(uv.x * atlas.width - .5f, 0, atlas.width - 1);
                float y = Mathf.Clamp(uv.y * atlas.height - .5f, 0, atlas.height - 1);
                int x0 = (int)x, y0 = (int)y;
                int x1 = Mathf.Min(x0 + 1, atlas.width - 1), y1 = Mathf.Min(y0 + 1, atlas.height - 1);
                return Mathf.Lerp(Mathf.Lerp(pixels[y0 * atlas.width + x0].a, pixels[y0 * atlas.width + x1].a, x - x0),
                    Mathf.Lerp(pixels[y1 * atlas.width + x0].a, pixels[y1 * atlas.width + x1].a, x - x0), y - y0) / 255f;
            }

            private void ReadAtlas()
            {
                Texture source = font.material.mainTexture;
                if (atlas != null && (atlas.width != source.width || atlas.height != source.height))
                {
                    UnityEngine.Object.DestroyImmediate(atlas);
                    atlas = null;
                }
                if (atlas == null) atlas = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false, true);
                RenderTexture previous = RenderTexture.active;
                RenderTexture target = RenderTexture.GetTemporary(source.width, source.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
                try
                {
                    Graphics.Blit(source, target);
                    RenderTexture.active = target;
                    atlas.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0, false);
                    atlas.Apply(false, false);
                    pixels = atlas.GetPixels32();
                }
                finally
                {
                    RenderTexture.active = previous;
                    RenderTexture.ReleaseTemporary(target);
                }
            }

            public void Dispose()
            {
                if (atlas != null) UnityEngine.Object.DestroyImmediate(atlas);
            }
        }
    }
}

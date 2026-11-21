// Desenha o menu com GL numa RenderTexture com MSAA. Assim as bordas saem suaves e o texto que
// passa do retângulo é recortado sem depender do GUIClip do Inspector.
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace DoskerTools.AuthoringTools
{
    internal sealed class MenuPainter : IGlyphMetrics, IDisposable
    {
        private const int ReferenceSize = 64;
        private const int MaxGlyphSize = 256;
        private const float SegmentDegrees = 3f;

        private readonly Dictionary<(char, bool), float> advances = new Dictionary<(char, bool), float>();
        private readonly Dictionary<(int, FontStyle), HashSet<char>> requests = new Dictionary<(int, FontStyle), HashSet<char>>();

        private RenderTexture target;
        private RenderTexture previous;
        private Material colored;
        private Material textured;
        private Font font;
        private bool linear;

        public Font Font
        {
            get
            {
                if (font == null)
                {
                    // A Arial tem as mesmas métricas da LiberationSans, a fonte padrão do TMP.
                    font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                    if (font == null)
                    {
                        font = Resources.GetBuiltinResource<Font>("Arial.ttf");
                    }
                }
                return font;
            }
        }

        public bool Ready => Font != null;

        public RenderTexture Begin(int width, int height, Color background)
        {
            width = Mathf.Clamp(width, 8, 4096);
            height = Mathf.Clamp(height, 8, 4096);
            if (target == null || target.width != width || target.height != height)
            {
                ReleaseTarget();
                target = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB)
                {
                    antiAliasing = 8,
                    hideFlags = HideFlags.HideAndDontSave,
                    name = "Dosker Expressions Menu Preview",
                };
                target.Create();
            }

            EnsureMaterials();
            linear = QualitySettings.activeColorSpace == ColorSpace.Linear;
            previous = RenderTexture.active;
            RenderTexture.active = target;
            GL.PushMatrix();
            GL.LoadPixelMatrix(0, width, height, 0);
            GL.Clear(true, true, ToGpu(background));
            return target;
        }

        public void End()
        {
            GL.PopMatrix();
            RenderTexture.active = previous;
            previous = null;
        }

        public RenderTexture Target => target;

        // Ângulos em graus, no sentido horário a partir do topo, com y para baixo.
        public static Vector2 Direction(float degrees)
        {
            var radians = degrees * Mathf.Deg2Rad;
            return new Vector2(Mathf.Sin(radians), -Mathf.Cos(radians));
        }

        public void Sector(Vector2 center, float innerRadius, float outerRadius, float from, float to, Color inner, Color outer)
        {
            if (to <= from || outerRadius <= innerRadius)
            {
                return;
            }
            var segments = Mathf.Max(1, Mathf.CeilToInt((to - from) / SegmentDegrees));
            colored.SetPass(0);
            GL.Begin(GL.QUADS);
            for (var i = 0; i < segments; i++)
            {
                var a = Direction(Mathf.Lerp(from, to, (float)i / segments));
                var b = Direction(Mathf.Lerp(from, to, (float)(i + 1) / segments));
                GL.Color(ToGpu(inner));
                GL.Vertex(center + a * innerRadius);
                GL.Color(ToGpu(outer));
                GL.Vertex(center + a * outerRadius);
                GL.Vertex(center + b * outerRadius);
                GL.Color(ToGpu(inner));
                GL.Vertex(center + b * innerRadius);
            }
            GL.End();
        }

        public void Disc(Vector2 center, float radius, Color color)
        {
            Sector(center, 0f, radius, 0f, 360f, color, color);
        }

        public void Ring(Vector2 center, float radius, float width, Color color)
        {
            Sector(center, radius - width / 2f, radius + width / 2f, 0f, 360f, color, color);
        }

        public void Arc(Vector2 center, float radius, float width, float from, float to, Color color)
        {
            Sector(center, radius - width / 2f, radius + width / 2f, from, to, color, color);
        }

        public void Line(Vector2 a, Vector2 b, float width, Color color)
        {
            var along = b - a;
            if (along.sqrMagnitude < 1e-6f)
            {
                return;
            }
            var side = new Vector2(-along.y, along.x).normalized * (width / 2f);
            colored.SetPass(0);
            GL.Begin(GL.QUADS);
            GL.Color(ToGpu(color));
            GL.Vertex(a - side);
            GL.Vertex(b - side);
            GL.Vertex(b + side);
            GL.Vertex(a + side);
            GL.End();
        }

        // Polígono convexo, desenhado em leque.
        public void Polygon(IReadOnlyList<Vector2> points, Color color)
        {
            if (points.Count < 3)
            {
                return;
            }
            colored.SetPass(0);
            GL.Begin(GL.TRIANGLES);
            GL.Color(ToGpu(color));
            for (var i = 1; i < points.Count - 1; i++)
            {
                GL.Vertex(points[0]);
                GL.Vertex(points[i]);
                GL.Vertex(points[i + 1]);
            }
            GL.End();
        }

        public void Quad(Rect rect, Color color)
        {
            Polygon(new[] { new Vector2(rect.xMin, rect.yMin), new Vector2(rect.xMax, rect.yMin), new Vector2(rect.xMax, rect.yMax), new Vector2(rect.xMin, rect.yMax) }, color);
        }

        public void RoundedRect(Rect rect, float radius, Color color)
        {
            radius = Mathf.Min(radius, rect.width / 2f, rect.height / 2f);
            var points = new List<Vector2>();
            AddCorner(points, new Vector2(rect.xMax - radius, rect.yMin + radius), radius, 0f);
            AddCorner(points, new Vector2(rect.xMax - radius, rect.yMax - radius), radius, 90f);
            AddCorner(points, new Vector2(rect.xMin + radius, rect.yMax - radius), radius, 180f);
            AddCorner(points, new Vector2(rect.xMin + radius, rect.yMin + radius), radius, 270f);
            Polygon(points, color);
        }

        private static void AddCorner(List<Vector2> points, Vector2 center, float radius, float from)
        {
            for (var i = 0; i <= 6; i++)
            {
                points.Add(center + Direction(from + 15f * i) * radius);
            }
        }

        public void Texture(Texture texture, Rect rect, Color tint)
        {
            if (texture == null)
            {
                return;
            }
            textured.mainTexture = texture;
            textured.SetPass(0);
            GL.Begin(GL.QUADS);
            GL.Color(ToGpu(tint));
            GL.TexCoord2(0f, 1f);
            GL.Vertex3(rect.xMin, rect.yMin, 0f);
            GL.TexCoord2(1f, 1f);
            GL.Vertex3(rect.xMax, rect.yMin, 0f);
            GL.TexCoord2(1f, 0f);
            GL.Vertex3(rect.xMax, rect.yMax, 0f);
            GL.TexCoord2(0f, 0f);
            GL.Vertex3(rect.xMin, rect.yMax, 0f);
            GL.End();
        }

        // Desenha um layout de rich text. origin é o canto superior esquerdo da caixa do texto, e scale
        // converte as unidades do TMP em pixels.
        public void Text(RichLayout layout, Vector2 origin, float scale, float alpha = 1f)
        {
            foreach (var mark in layout.Marks)
            {
                Quad(Transform(mark.Rect, origin, scale), Fade(mark.Color, alpha));
            }

            requests.Clear();
            foreach (var glyph in layout.Glyphs)
            {
                var key = (PixelSize(glyph.Source.Size * scale), glyph.Source.Bold ? FontStyle.Bold : FontStyle.Normal);
                if (!requests.TryGetValue(key, out var set))
                {
                    requests[key] = set = new HashSet<char>();
                }
                set.Add(glyph.Source.Code);
            }
            // Pedir todos os caracteres antes de ler qualquer um, porque o atlas pode ser refeito.
            foreach (var request in requests)
            {
                Font.RequestCharactersInTexture(new string(new List<char>(request.Value).ToArray()), request.Key.Item1, request.Key.Item2);
            }

            var material = Font.material;
            material.SetPass(0);
            GL.Begin(GL.QUADS);
            foreach (var glyph in layout.Glyphs)
            {
                var c = glyph.Source;
                var style = c.Bold ? FontStyle.Bold : FontStyle.Normal;
                var pixels = PixelSize(c.Size * scale);
                if (!Font.GetCharacterInfo(c.Code, out var info, pixels, style))
                {
                    continue;
                }

                var k = c.Size * scale / pixels;
                var baseline = new Vector2(origin.x + glyph.X * scale, origin.y + glyph.BaselineY * scale);
                var left = info.minX * k;
                var right = info.maxX * k;
                if (c.Bold)
                {
                    // O TMP alarga o avanço do negrito sintético; o glifo fica no meio do espaço extra.
                    var shift = (glyph.Advance * scale - info.advance * k) / 2f;
                    left += shift;
                    right += shift;
                }
                var top = -info.maxY * k;
                var bottom = -info.minY * k;

                var corners = new[]
                {
                    new Vector2(left, top),
                    new Vector2(right, top),
                    new Vector2(right, bottom),
                    new Vector2(left, bottom),
                };
                var pivot = new Vector2(glyph.Advance * scale / 2f, -c.Size * scale * 0.35f);
                for (var i = 0; i < corners.Length; i++)
                {
                    var p = corners[i];
                    if (c.Italic)
                    {
                        p.x += TmpFace.ItalicShear * -p.y;
                    }
                    if (!Mathf.Approximately(c.ScaleX, 1f) || !Mathf.Approximately(c.Rotation, 0f))
                    {
                        var local = p - pivot;
                        local.x *= c.ScaleX;
                        var radians = -c.Rotation * Mathf.Deg2Rad;
                        local = new Vector2(local.x * Mathf.Cos(radians) - local.y * Mathf.Sin(radians), local.x * Mathf.Sin(radians) + local.y * Mathf.Cos(radians));
                        p = pivot + local;
                    }
                    corners[i] = baseline + p;
                }

                GL.Color(ToGpu(Fade(c.Color, alpha)));
                GL.TexCoord(info.uvTopLeft);
                GL.Vertex(corners[0]);
                GL.TexCoord(info.uvTopRight);
                GL.Vertex(corners[1]);
                GL.TexCoord(info.uvBottomRight);
                GL.Vertex(corners[2]);
                GL.TexCoord(info.uvBottomLeft);
                GL.Vertex(corners[3]);
            }
            GL.End();

            foreach (var line in layout.Lines)
            {
                Quad(Transform(line.Rect, origin, scale), Fade(line.Color, alpha));
            }
            foreach (var sprite in layout.Sprites)
            {
                var rect = Transform(sprite.Rect, origin, scale);
                RoundedRect(rect, rect.width * 0.25f, Fade(new Color32(255, 204, 77, 255), alpha));
            }
        }

        public bool TryAdvance(char code, bool bold, out float advance)
        {
            if (code == ' ')
            {
                code = ' ';
            }
            // O negrito do TMP é sintético: parte do avanço do glifo normal, que o layout multiplica.
            if (advances.TryGetValue((code, false), out advance))
            {
                return !float.IsNaN(advance);
            }

            Font.RequestCharactersInTexture(code.ToString(), ReferenceSize, FontStyle.Normal);
            advance = Font.GetCharacterInfo(code, out var info, ReferenceSize, FontStyle.Normal) ? info.advance / (float)ReferenceSize : float.NaN;
            advances[(code, false)] = advance;
            return !float.IsNaN(advance);
        }

        private static int PixelSize(float size)
        {
            return Mathf.Clamp(Mathf.RoundToInt(size), 1, MaxGlyphSize);
        }

        private static Rect Transform(Rect rect, Vector2 origin, float scale)
        {
            return new Rect(origin.x + rect.x * scale, origin.y + rect.y * scale, rect.width * scale, rect.height * scale);
        }

        private static Color Fade(Color32 color, float alpha)
        {
            Color result = color;
            result.a *= alpha;
            return result;
        }

        // Cores de vértice não passam pela conversão automática do Unity em Linear.
        private Color ToGpu(Color color)
        {
            if (!linear)
            {
                return color;
            }
            var converted = color.linear;
            converted.a = color.a;
            return converted;
        }

        private void EnsureMaterials()
        {
            if (colored == null)
            {
                colored = new Material(Shader.Find("Hidden/Internal-Colored")) { hideFlags = HideFlags.HideAndDontSave };
                colored.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
                colored.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
                colored.SetInt("_Cull", (int)CullMode.Off);
                colored.SetInt("_ZWrite", 0);
                colored.SetInt("_ZTest", (int)CompareFunction.Always);
            }
            if (textured == null)
            {
                textured = new Material(Shader.Find("Sprites/Default")) { hideFlags = HideFlags.HideAndDontSave };
            }
        }

        private void ReleaseTarget()
        {
            if (target == null)
            {
                return;
            }
            if (RenderTexture.active == target)
            {
                RenderTexture.active = null;
            }
            target.Release();
            UnityEngine.Object.DestroyImmediate(target);
            target = null;
        }

        public void Dispose()
        {
            ReleaseTarget();
            if (colored != null)
            {
                UnityEngine.Object.DestroyImmediate(colored);
            }
            if (textured != null)
            {
                UnityEngine.Object.DestroyImmediate(textured);
            }
            colored = null;
            textured = null;
        }
    }
}

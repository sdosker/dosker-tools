// Reproduz o rich text do TextMeshPro, que o VRChat usa nos nomes do menu de expressões.
// As regras seguem o TMP_Text.ValidateHtmlTag: nomes de tag só em minúsculas ou só em maiúsculas,
// nomes de cor só em minúsculas, e uma tag inválida aparece como texto.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace DoskerTools.AuthoringTools
{
    // Métricas da fonte padrão do TMP (LiberationSans SDF), em em. A Arial tem as mesmas medidas.
    internal static class TmpFace
    {
        public const float Ascender = 0.905f;
        public const float Descender = -0.212f;
        public const float LineHeight = 1.149f;
        public const float ScriptSize = 0.5f;
        public const float SuperscriptOffset = 0.905f;
        public const float SubscriptOffset = -0.212f;
        public const float UnderlineOffset = -0.147f;
        public const float LineThickness = 0.073f;
        public const float StrikethroughOffset = 0.26f;
        public const float BoldAdvance = 1.07f;
        public const float ItalicShear = 0.35f;
        public const float SmallCapsScale = 0.8f;
        public const float TabStops = 10f;
        public const float SpriteAdvance = 1.1f;
    }

    internal interface IGlyphMetrics
    {
        // Avanço em em. False quando a fonte não tem o caractere.
        bool TryAdvance(char code, bool bold, out float advance);
    }

    internal enum RichKind
    {
        Glyph,
        LineBreak,
        Space,
        Sprite,
    }

    internal enum RichAlign
    {
        Left,
        Center,
        Right,
        Justified,
        Flush,
    }

    internal struct RichChar
    {
        public RichKind Kind;
        public char Code;
        public float Size;
        public float Baseline;
        public Color32 Color;
        public bool Bold;
        public bool Italic;
        public bool Underline;
        public bool Strike;
        public bool Mark;
        public Color32 UnderlineColor;
        public Color32 StrikeColor;
        public Color32 MarkColor;
        public float CSpace;
        public float MonoSpace;
        public bool NoBreak;
        public RichAlign Align;
        public float ScaleX;
        public float Rotation;
        public float LineHeight;
        public float SpaceAdvance;
    }

    internal struct LaidGlyph
    {
        public RichChar Source;
        public float X;
        public float Advance;
        public float BaselineY;
    }

    internal struct LaidRect
    {
        public Rect Rect;
        public Color32 Color;
    }

    internal sealed class RichLayout
    {
        public readonly List<LaidGlyph> Glyphs = new List<LaidGlyph>();
        public readonly List<LaidRect> Marks = new List<LaidRect>();
        public readonly List<LaidRect> Lines = new List<LaidRect>();
        public readonly List<LaidRect> Sprites = new List<LaidRect>();
        public Rect Bounds;
        public int LineCount;
    }

    internal static class MenuRichText
    {
        private static readonly Color32 DefaultMarkColor = new Color32(255, 255, 0, 64);

        private static readonly Dictionary<string, Color32> NamedColors = new Dictionary<string, Color32>
        {
            { "red", new Color32(255, 0, 0, 255) },
            { "lightblue", new Color32(173, 216, 230, 255) },
            { "blue", new Color32(0, 0, 255, 255) },
            { "grey", new Color32(128, 128, 128, 255) },
            { "black", new Color32(0, 0, 0, 255) },
            { "green", new Color32(0, 255, 0, 255) },
            { "white", new Color32(255, 255, 255, 255) },
            { "orange", new Color32(255, 128, 0, 255) },
            { "purple", new Color32(160, 32, 240, 255) },
            { "yellow", new Color32(255, 235, 4, 255) },
        };

        // Tags que o TMP aceita sem precisar de asset. Elas somem do texto mesmo quando o preview não
        // reproduz o efeito (posição, margens, links).
        private static readonly HashSet<string> SilentTags = new HashSet<string>
        {
            "pos", "/pos", "indent", "/indent", "line-indent", "/line-indent", "margin", "/margin",
            "margin-left", "margin-right", "width", "/width", "link", "/link", "/a", "action", "/action",
            "page", "/font", "/material", "/gradient", "/style", "table", "/table", "tr", "/tr", "th", "/th",
            "td", "/td",
        };

        private enum Unit
        {
            None,
            Pixels,
            FontUnits,
            Percentage,
        }

        // Converte o texto em caracteres com estilo, como o TMP faz antes do layout.
        public static List<RichChar> Parse(string text, float fontSize, Color32 color)
        {
            var parser = new Parser(fontSize, color);
            parser.Run(Unescape(text ?? string.Empty));
            return parser.Output;
        }

        // As sequências que o TMP interpreta com parseCtrlCharacters ligado, o padrão.
        internal static string Unescape(string text)
        {
            if (text.IndexOf('\\') < 0)
            {
                return text;
            }

            var builder = new StringBuilder(text.Length);
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (c == '\\' && i + 1 < text.Length)
                {
                    switch (text[i + 1])
                    {
                        case 'n':
                            builder.Append('\n');
                            i++;
                            continue;
                        case 'r':
                            builder.Append('\r');
                            i++;
                            continue;
                        case 't':
                            builder.Append('\t');
                            i++;
                            continue;
                        case 'v':
                            builder.Append('\v');
                            i++;
                            continue;
                        case 'u':
                            if (TryHex(text, i + 2, 4, out var utf16))
                            {
                                builder.Append((char)utf16);
                                i += 5;
                                continue;
                            }
                            break;
                        case 'U':
                            if (TryHex(text, i + 2, 8, out var utf32) && utf32 <= 0x10FFFF)
                            {
                                builder.Append(char.ConvertFromUtf32(utf32));
                                i += 9;
                                continue;
                            }
                            break;
                    }
                }
                builder.Append(c);
            }
            return builder.ToString();
        }

        private static bool TryHex(string text, int start, int count, out int value)
        {
            value = 0;
            if (start + count > text.Length)
            {
                return false;
            }
            return int.TryParse(text.Substring(start, count), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        }

        private sealed class Parser
        {
            public readonly List<RichChar> Output = new List<RichChar>();

            private readonly float baseSize;
            private readonly Color32 initialColor;
            private readonly List<Color32> colors = new List<Color32>();
            private readonly List<float> sizes = new List<float>();
            private readonly List<Color32> underlineColors = new List<Color32>();
            private readonly List<Color32> strikeColors = new List<Color32>();
            private readonly List<Color32> markColors = new List<Color32>();
            private readonly List<float> baselines = new List<float>();
            private readonly List<RichAlign> aligns = new List<RichAlign>();
            private readonly List<bool> weights = new List<bool>();

            private Color32 color;
            private float size;
            private float scriptScale = 1f;
            private float baseline;
            private int bold;
            private int italic;
            private int underline;
            private int strike;
            private int mark;
            private int lower;
            private int upper;
            private int smallCaps;
            private bool weightBold;
            private float cSpace;
            private float monoSpace;
            private bool noBreak;
            private bool noParse;
            private RichAlign align = RichAlign.Center;
            private float scaleX = 1f;
            private float rotation;
            private float lineHeight = float.NaN;

            public Parser(float fontSize, Color32 baseColor)
            {
                baseSize = fontSize;
                size = fontSize;
                color = baseColor;
                initialColor = baseColor;
            }

            public void Run(string text)
            {
                for (var i = 0; i < text.Length; i++)
                {
                    var c = text[i];
                    if (c == '<' && TryTag(text, i, out var end))
                    {
                        i = end;
                        continue;
                    }

                    if (c == '\n' || c == '\v')
                    {
                        AddBreak();
                        continue;
                    }
                    if (c == '\r')
                    {
                        continue;
                    }
                    AddGlyph(c);
                }
            }

            private bool TryTag(string text, int start, out int end)
            {
                end = start;
                var close = -1;
                for (var i = start + 1; i < text.Length && i - start <= 128; i++)
                {
                    if (text[i] == '<')
                    {
                        return false;
                    }
                    if (text[i] == '>')
                    {
                        close = i;
                        break;
                    }
                }
                if (close < 0)
                {
                    return false;
                }

                var content = text.Substring(start + 1, close - start - 1);
                if (noParse)
                {
                    if (content != "/noparse" && content != "/NOPARSE")
                    {
                        return false;
                    }
                    noParse = false;
                    end = close;
                    return true;
                }

                if (!Apply(content))
                {
                    return false;
                }
                end = close;
                return true;
            }

            private bool Apply(string content)
            {
                // Com espaço logo depois do <, o TMP fica sem nome de tag e mostra o texto.
                if (content.Length == 0 || content[0] == ' ')
                {
                    return false;
                }

                // <#FFF>, <#FFF7>, <#FF00FF> e <#FF00FF00>
                if (content[0] == '#')
                {
                    if (!TryHexColor(content, out var hex))
                    {
                        return false;
                    }
                    color = hex;
                    colors.Add(color);
                    return true;
                }

                SplitTag(content, out var name, out var value, out var attributes);
                if (name == null)
                {
                    return false;
                }

                var key = name.ToLowerInvariant();
                switch (key)
                {
                    case "br":
                        if (value != null)
                        {
                            return false;
                        }
                        AddBreak();
                        return true;
                    case "nbsp":
                        AddGlyph(' ');
                        return true;
                    case "zwsp":
                        AddGlyph('​');
                        return true;
                    case "noparse":
                        noParse = true;
                        return true;
                    case "b":
                        bold++;
                        return true;
                    case "/b":
                        bold = Math.Max(0, bold - 1);
                        return true;
                    case "i":
                        if (attributes.TryGetValue("angle", out var angle) && (!TryNumber(angle, out var angleValue, out _) || angleValue < -180f || angleValue > 180f))
                        {
                            return false;
                        }
                        italic++;
                        return true;
                    case "/i":
                        italic = Math.Max(0, italic - 1);
                        return true;
                    case "s":
                        strike++;
                        strikeColors.Add(AttributeColor(attributes, color));
                        return true;
                    case "/s":
                        strike = Math.Max(0, strike - 1);
                        Pop(strikeColors);
                        return true;
                    case "u":
                        underline++;
                        underlineColors.Add(AttributeColor(attributes, color));
                        return true;
                    case "/u":
                        underline = Math.Max(0, underline - 1);
                        Pop(underlineColors);
                        return true;
                    case "mark":
                        var highlight = DefaultMarkColor;
                        if (value != null && value.StartsWith("#", StringComparison.Ordinal) && TryHexColor(value, out var markValue))
                        {
                            highlight = markValue;
                        }
                        if (attributes.TryGetValue("color", out var markAttribute) && TryHexColor(markAttribute, out var markColor))
                        {
                            highlight = markColor;
                        }
                        highlight.a = Math.Min(color.a, highlight.a);
                        mark++;
                        markColors.Add(highlight);
                        return true;
                    case "/mark":
                        mark = Math.Max(0, mark - 1);
                        Pop(markColors);
                        return true;
                    case "sub":
                    case "sup":
                        scriptScale *= TmpFace.ScriptSize;
                        baselines.Add(baseline);
                        baseline += (key == "sub" ? TmpFace.SubscriptOffset : TmpFace.SuperscriptOffset) * size * scriptScale;
                        return true;
                    case "/sub":
                    case "/sup":
                        if (scriptScale < 1f)
                        {
                            baseline = Pop(baselines, 0f);
                            scriptScale /= TmpFace.ScriptSize;
                        }
                        return true;
                    case "font-weight":
                        if (!TryNumber(value, out var weight, out _))
                        {
                            return false;
                        }
                        weights.Add(weightBold);
                        weightBold = weight >= 700f;
                        return true;
                    case "/font-weight":
                        weightBold = Pop(weights, false);
                        return true;
                    case "voffset":
                        if (!TryNumber(value, out var offset, out var offsetUnit) || offsetUnit == Unit.Percentage)
                        {
                            return false;
                        }
                        baseline = offsetUnit == Unit.FontUnits ? offset * size : offset;
                        return true;
                    case "/voffset":
                        baseline = 0f;
                        return true;
                    case "nobr":
                        noBreak = true;
                        return true;
                    case "/nobr":
                        noBreak = false;
                        return true;
                    case "size":
                        if (!TryNumber(value, out var number, out var sizeUnit))
                        {
                            return false;
                        }
                        switch (sizeUnit)
                        {
                            case Unit.FontUnits:
                                size = baseSize * number;
                                break;
                            case Unit.Percentage:
                                size = baseSize * number / 100f;
                                break;
                            default:
                                size = value[0] == '+' || value[0] == '-' ? baseSize + number : number;
                                break;
                        }
                        sizes.Add(size);
                        return true;
                    case "/size":
                        Pop(sizes);
                        size = sizes.Count > 0 ? sizes[sizes.Count - 1] : baseSize;
                        return true;
                    case "space":
                        if (!TryNumber(value, out var space, out var spaceUnit) || spaceUnit == Unit.Percentage)
                        {
                            return false;
                        }
                        var item = Current(RichKind.Space, ' ');
                        item.SpaceAdvance = spaceUnit == Unit.FontUnits ? space * size : space;
                        Output.Add(item);
                        return true;
                    case "alpha":
                        if (value == null || value.Length != 3 || value[0] != '#')
                        {
                            return false;
                        }
                        color.a = (byte)(HexDigit(value[1]) * 16 + HexDigit(value[2]));
                        return true;
                    case "color":
                        if (value == null)
                        {
                            return false;
                        }
                        if (value[0] == '#')
                        {
                            if (!TryHexColor(value, out var colorValue))
                            {
                                return false;
                            }
                            color = colorValue;
                        }
                        else if (!(name == "color" || name == "COLOR") || !NamedColors.TryGetValue(value, out color))
                        {
                            return false;
                        }
                        colors.Add(color);
                        return true;
                    case "/color":
                        Pop(colors);
                        color = colors.Count > 0 ? colors[colors.Count - 1] : initialColor;
                        return true;
                    case "lowercase":
                        lower++;
                        return true;
                    case "/lowercase":
                        lower = Math.Max(0, lower - 1);
                        return true;
                    case "uppercase":
                    case "allcaps":
                        upper++;
                        return true;
                    case "/uppercase":
                    case "/allcaps":
                        upper = Math.Max(0, upper - 1);
                        return true;
                    case "smallcaps":
                        smallCaps++;
                        return true;
                    case "/smallcaps":
                        smallCaps = Math.Max(0, smallCaps - 1);
                        return true;
                    case "cspace":
                        if (!TryNumber(value, out var spacing, out var spacingUnit) || spacingUnit == Unit.Percentage)
                        {
                            return false;
                        }
                        cSpace = spacingUnit == Unit.FontUnits ? spacing * size : spacing;
                        return true;
                    case "/cspace":
                        cSpace = 0f;
                        return true;
                    case "mspace":
                        if (!TryNumber(value, out var mono, out var monoUnit) || monoUnit == Unit.Percentage)
                        {
                            return false;
                        }
                        monoSpace = monoUnit == Unit.FontUnits ? mono * size : mono;
                        return true;
                    case "/mspace":
                        monoSpace = 0f;
                        return true;
                    case "line-height":
                        if (!TryNumber(value, out var height, out var heightUnit))
                        {
                            return false;
                        }
                        lineHeight = heightUnit == Unit.FontUnits ? height * size
                            : heightUnit == Unit.Percentage ? TmpFace.LineHeight * height / 100f * size
                            : height;
                        return true;
                    case "/line-height":
                        lineHeight = float.NaN;
                        return true;
                    case "align":
                        RichAlign parsed;
                        switch (value)
                        {
                            case "left":
                                parsed = RichAlign.Left;
                                break;
                            case "right":
                                parsed = RichAlign.Right;
                                break;
                            case "center":
                                parsed = RichAlign.Center;
                                break;
                            case "justified":
                                parsed = RichAlign.Justified;
                                break;
                            case "flush":
                                parsed = RichAlign.Flush;
                                break;
                            default:
                                return false;
                        }
                        aligns.Add(align);
                        align = parsed;
                        return true;
                    case "/align":
                        align = Pop(aligns, RichAlign.Center);
                        return true;
                    case "scale":
                        if (!TryNumber(value, out var scale, out _))
                        {
                            return false;
                        }
                        scaleX = scale;
                        return true;
                    case "/scale":
                        scaleX = 1f;
                        return true;
                    case "rotate":
                        if (!TryNumber(value, out var degrees, out _))
                        {
                            return false;
                        }
                        rotation = degrees;
                        return true;
                    case "/rotate":
                        rotation = 0f;
                        return true;
                    case "sprite":
                        // O TMP do VRChat traz o sprite asset padrão; aqui o emoji vira um marcador.
                        Output.Add(Current(RichKind.Sprite, ' '));
                        return true;
                    case "font":
                        return value == "default" || value == "Default";
                    default:
                        return SilentTags.Contains(key) && (name == key || name == key.ToUpperInvariant());
                }
            }

            private void AddBreak()
            {
                Output.Add(Current(RichKind.LineBreak, '\n'));
            }

            private void AddGlyph(char c)
            {
                var item = Current(RichKind.Glyph, c);
                if (upper > 0)
                {
                    item.Code = char.ToUpperInvariant(c);
                }
                else if (lower > 0)
                {
                    item.Code = char.ToLowerInvariant(c);
                }
                else if (smallCaps > 0 && char.IsLower(c))
                {
                    item.Code = char.ToUpperInvariant(c);
                    item.Size *= TmpFace.SmallCapsScale;
                }
                Output.Add(item);
            }

            private RichChar Current(RichKind kind, char c)
            {
                return new RichChar
                {
                    Kind = kind,
                    Code = c,
                    Size = size * scriptScale,
                    Baseline = baseline,
                    Color = color,
                    Bold = bold > 0 || weightBold,
                    Italic = italic > 0,
                    Underline = underline > 0,
                    Strike = strike > 0,
                    Mark = mark > 0,
                    UnderlineColor = Top(underlineColors, color),
                    StrikeColor = Top(strikeColors, color),
                    MarkColor = Top(markColors, DefaultMarkColor),
                    CSpace = cSpace,
                    MonoSpace = monoSpace,
                    NoBreak = noBreak,
                    Align = align,
                    ScaleX = scaleX,
                    Rotation = rotation,
                    LineHeight = lineHeight,
                };
            }

            // O nome da tag é case-sensitive como no TMP: só "color" ou "COLOR".
            private static void SplitTag(string content, out string name, out string value, out Dictionary<string, string> attributes)
            {
                attributes = new Dictionary<string, string>();
                value = null;
                name = null;

                var parts = SplitSpaces(content);
                if (parts.Count == 0)
                {
                    return;
                }

                var head = parts[0];
                var equals = head.IndexOf('=');
                var rawName = equals < 0 ? head : head.Substring(0, equals);
                if (rawName.Length == 0 || (rawName != rawName.ToLowerInvariant() && rawName != rawName.ToUpperInvariant()))
                {
                    return;
                }
                name = rawName;
                if (equals >= 0)
                {
                    value = Unquote(head.Substring(equals + 1));
                    if (value.Length == 0)
                    {
                        name = null;
                        return;
                    }
                }

                for (var i = 1; i < parts.Count; i++)
                {
                    var split = parts[i].IndexOf('=');
                    if (split <= 0)
                    {
                        continue;
                    }
                    attributes[parts[i].Substring(0, split).ToLowerInvariant()] = Unquote(parts[i].Substring(split + 1));
                }
            }

            private static List<string> SplitSpaces(string content)
            {
                var parts = new List<string>();
                var builder = new StringBuilder();
                var quoted = false;
                foreach (var c in content)
                {
                    if (c == '"')
                    {
                        quoted = !quoted;
                    }
                    if (c == ' ' && !quoted)
                    {
                        if (builder.Length > 0)
                        {
                            parts.Add(builder.ToString());
                            builder.Clear();
                        }
                        continue;
                    }
                    builder.Append(c);
                }
                if (builder.Length > 0)
                {
                    parts.Add(builder.ToString());
                }
                return parts;
            }

            private static string Unquote(string value)
            {
                return value.Length >= 2 && value[0] == '"' && value[value.Length - 1] == '"' ? value.Substring(1, value.Length - 2) : value;
            }

            private static Color32 AttributeColor(Dictionary<string, string> attributes, Color32 fallback)
            {
                if (attributes.TryGetValue("color", out var text) && TryHexColor(text, out var parsed))
                {
                    parsed.a = Math.Min(fallback.a, parsed.a);
                    return parsed;
                }
                return fallback;
            }

            // O TMP lê os números até p, e ou %, que definem a unidade.
            private static bool TryNumber(string text, out float number, out Unit unit)
            {
                number = 0f;
                unit = Unit.None;
                if (string.IsNullOrEmpty(text))
                {
                    return false;
                }

                var length = 0;
                while (length < text.Length && (char.IsDigit(text[length]) || text[length] == '.' || ((text[length] == '+' || text[length] == '-') && length == 0)))
                {
                    length++;
                }
                if (length == 0 || !float.TryParse(text.Substring(0, length), NumberStyles.Float, CultureInfo.InvariantCulture, out number))
                {
                    return false;
                }

                if (length == text.Length)
                {
                    unit = Unit.Pixels;
                    return true;
                }
                switch (text[length])
                {
                    case 'p':
                        unit = Unit.Pixels;
                        return true;
                    case 'e':
                        unit = Unit.FontUnits;
                        return true;
                    case '%':
                        unit = Unit.Percentage;
                        return true;
                    default:
                        return false;
                }
            }

            private static T Pop<T>(List<T> stack, T fallback = default)
            {
                if (stack.Count == 0)
                {
                    return fallback;
                }
                var item = stack[stack.Count - 1];
                stack.RemoveAt(stack.Count - 1);
                return item;
            }

            private static T Top<T>(List<T> stack, T fallback)
            {
                return stack.Count > 0 ? stack[stack.Count - 1] : fallback;
            }
        }

        // Como o HexCharsToColor do TMP, com 3, 4, 6 ou 8 dígitos depois do #.
        internal static bool TryHexColor(string text, out Color32 result)
        {
            result = default;
            if (string.IsNullOrEmpty(text) || text[0] != '#')
            {
                return false;
            }

            switch (text.Length)
            {
                case 4:
                    result = new Color32(Short(text[1]), Short(text[2]), Short(text[3]), 255);
                    return true;
                case 5:
                    result = new Color32(Short(text[1]), Short(text[2]), Short(text[3]), Short(text[4]));
                    return true;
                case 7:
                    result = new Color32(Pair(text, 1), Pair(text, 3), Pair(text, 5), 255);
                    return true;
                case 9:
                    result = new Color32(Pair(text, 1), Pair(text, 3), Pair(text, 5), Pair(text, 7));
                    return true;
                default:
                    return false;
            }
        }

        private static byte Short(char c)
        {
            return (byte)(HexDigit(c) * 16 + HexDigit(c));
        }

        private static byte Pair(string text, int index)
        {
            return (byte)(HexDigit(text[index]) * 16 + HexDigit(text[index + 1]));
        }

        // O TMP devolve 15 para um caractere que não é hexadecimal.
        private static int HexDigit(char c)
        {
            if (c >= '0' && c <= '9')
            {
                return c - '0';
            }
            if (c >= 'a' && c <= 'f')
            {
                return c - 'a' + 10;
            }
            if (c >= 'A' && c <= 'F')
            {
                return c - 'A' + 10;
            }
            return 15;
        }

        // Quebra de linha como o TMP: em espaços, depois de hífen e entre caracteres CJK. Uma palavra maior
        // que a largura é quebrada no meio. O overflow é livre, como no modo Overflow.
        public static RichLayout Layout(List<RichChar> chars, float width, IGlyphMetrics metrics)
        {
            var layout = new RichLayout();
            var lines = BreakLines(chars, width, metrics);

            var baselineY = 0f;
            var previousDescender = 0f;
            var minX = float.MaxValue;
            var maxX = float.MinValue;
            var bottom = 0f;

            for (var lineIndex = 0; lineIndex < lines.Count; lineIndex++)
            {
                var (start, end) = lines[lineIndex];
                var ascender = float.MinValue;
                var descender = float.MaxValue;
                var maxSize = 0f;
                for (var i = start; i < end; i++)
                {
                    var c = chars[i];
                    ascender = Mathf.Max(ascender, c.Size * TmpFace.Ascender + c.Baseline);
                    descender = Mathf.Min(descender, c.Size * TmpFace.Descender + c.Baseline);
                    maxSize = Mathf.Max(maxSize, c.Size);
                }
                if (start == end)
                {
                    var size = start < chars.Count ? chars[start].Size : start > 0 ? chars[start - 1].Size : 0f;
                    ascender = size * TmpFace.Ascender;
                    descender = size * TmpFace.Descender;
                    maxSize = size;
                }

                if (lineIndex == 0)
                {
                    baselineY = ascender;
                }
                else
                {
                    var forced = start < chars.Count ? chars[Math.Max(start - 1, 0)].LineHeight : float.NaN;
                    baselineY += !float.IsNaN(forced)
                        ? forced
                        : -previousDescender + ascender + (TmpFace.LineHeight - (TmpFace.Ascender - TmpFace.Descender)) * maxSize;
                }
                previousDescender = descender;

                var positions = new float[end - start];
                var advances = new float[end - start];
                var x = 0f;
                for (var i = start; i < end; i++)
                {
                    var advance = Advance(chars[i], x, metrics);
                    positions[i - start] = x;
                    advances[i - start] = advance;
                    x += advance;
                }

                var visibleEnd = end;
                while (visibleEnd > start && IsWhiteSpace(chars[visibleEnd - 1]))
                {
                    visibleEnd--;
                }
                var lineWidth = visibleEnd > start ? positions[visibleEnd - 1 - start] + advances[visibleEnd - 1 - start] - chars[visibleEnd - 1].CSpace : 0f;

                var alignment = start < end ? chars[start].Align : RichAlign.Center;
                var lastLine = lineIndex == lines.Count - 1 || (end < chars.Count && chars[end].Kind == RichKind.LineBreak);
                var offset = 0f;
                var stretch = 0f;
                switch (alignment)
                {
                    case RichAlign.Center:
                        offset = (width - lineWidth) / 2f;
                        break;
                    case RichAlign.Right:
                        offset = width - lineWidth;
                        break;
                    case RichAlign.Justified:
                    case RichAlign.Flush:
                        if (alignment == RichAlign.Flush || !lastLine)
                        {
                            var gaps = 0;
                            for (var i = start; i < visibleEnd - 1; i++)
                            {
                                if (IsWhiteSpace(chars[i]))
                                {
                                    gaps++;
                                }
                            }
                            stretch = gaps > 0 && lineWidth < width ? (width - lineWidth) / gaps : 0f;
                        }
                        break;
                }

                var extra = 0f;
                for (var i = start; i < visibleEnd; i++)
                {
                    var c = chars[i];
                    var glyphX = offset + positions[i - start] + extra;
                    var advance = advances[i - start];
                    var y = baselineY - c.Baseline;
                    if (stretch > 0f && IsWhiteSpace(c))
                    {
                        extra += stretch;
                    }

                    if (c.Mark)
                    {
                        layout.Marks.Add(new LaidRect
                        {
                            Rect = Rect.MinMaxRect(glyphX, y - c.Size * TmpFace.Ascender, glyphX + advance, y - c.Size * TmpFace.Descender),
                            Color = c.MarkColor,
                        });
                    }
                    var thickness = c.Size * TmpFace.LineThickness;
                    if (c.Underline)
                    {
                        var lineY = y - c.Size * TmpFace.UnderlineOffset;
                        layout.Lines.Add(new LaidRect { Rect = new Rect(glyphX, lineY - thickness / 2f, advance, thickness), Color = c.UnderlineColor });
                    }
                    if (c.Strike)
                    {
                        var lineY = y - c.Size * TmpFace.StrikethroughOffset;
                        layout.Lines.Add(new LaidRect { Rect = new Rect(glyphX, lineY - thickness / 2f, advance, thickness), Color = c.StrikeColor });
                    }

                    if (c.Kind == RichKind.Sprite)
                    {
                        var side = c.Size * TmpFace.Ascender;
                        layout.Sprites.Add(new LaidRect { Rect = new Rect(glyphX + (advance - side) / 2f, y - side, side, side), Color = c.Color });
                    }
                    else if (c.Kind == RichKind.Glyph && !IsWhiteSpace(c))
                    {
                        layout.Glyphs.Add(new LaidGlyph { Source = c, X = glyphX, Advance = advance - c.CSpace, BaselineY = y });
                    }

                    minX = Mathf.Min(minX, glyphX);
                    maxX = Mathf.Max(maxX, glyphX + advance);
                }
                bottom = baselineY - descender;
            }

            layout.LineCount = lines.Count;
            layout.Bounds = minX <= maxX ? Rect.MinMaxRect(minX, 0f, maxX, bottom) : new Rect(width / 2f, 0f, 0f, bottom);
            return layout;
        }

        private static List<(int, int)> BreakLines(List<RichChar> chars, float width, IGlyphMetrics metrics)
        {
            var lines = new List<(int, int)>();
            var start = 0;
            var x = 0f;
            var lastBreak = -1;
            var i = 0;
            while (i < chars.Count)
            {
                var c = chars[i];
                if (c.Kind == RichKind.LineBreak)
                {
                    lines.Add((start, i));
                    start = i + 1;
                    x = 0f;
                    lastBreak = -1;
                    i++;
                    continue;
                }

                var advance = Advance(c, x, metrics);
                var white = IsWhiteSpace(c);
                var cjk = c.Kind == RichKind.Glyph && IsCjk(c.Code) && !c.NoBreak;
                if (cjk && i > start)
                {
                    lastBreak = i - 1;
                }
                if (!white && i > start && x + advance - c.CSpace > width + 0.001f)
                {
                    if (lastBreak >= start && lastBreak < i)
                    {
                        lines.Add((start, lastBreak + 1));
                        start = lastBreak + 1;
                    }
                    else
                    {
                        lines.Add((start, i));
                        start = i;
                    }
                    // Recalcula a linha nova desde o início, porque tabs dependem da posição.
                    i = start;
                    x = 0f;
                    lastBreak = -1;
                    continue;
                }

                if (!c.NoBreak && (white || cjk || c.Code == '-' || c.Code == '​'))
                {
                    lastBreak = i;
                }
                x += advance;
                i++;
            }
            lines.Add((start, chars.Count));
            return lines;
        }

        private static float Advance(RichChar c, float x, IGlyphMetrics metrics)
        {
            switch (c.Kind)
            {
                case RichKind.Space:
                    return c.SpaceAdvance;
                case RichKind.Sprite:
                    return c.Size * TmpFace.SpriteAdvance + c.CSpace;
                case RichKind.LineBreak:
                    return 0f;
            }

            if (c.Code == '​')
            {
                return 0f;
            }
            if (c.Code == '\t')
            {
                metrics.TryAdvance(' ', false, out var space);
                var stop = space * c.Size * TmpFace.TabStops;
                return stop > 0f ? Mathf.Ceil(x / stop) * stop - x : 0f;
            }
            if (c.MonoSpace > 0f)
            {
                return c.MonoSpace + c.CSpace;
            }
            if (!metrics.TryAdvance(c.Code, c.Bold, out var em))
            {
                em = 0.5f;
            }
            return em * c.Size * (c.Bold ? TmpFace.BoldAdvance : 1f) + c.CSpace;
        }

        private static bool IsWhiteSpace(RichChar c)
        {
            return c.Kind == RichKind.Glyph && (c.Code == ' ' || c.Code == '\t' || (c.Code >= ' ' && c.Code <= ' ') || c.Code == '　');
        }

        private static bool IsCjk(char c)
        {
            return (c >= '぀' && c <= 'ヿ') || (c >= '㐀' && c <= '䶿') || (c >= '一' && c <= '鿿')
                || (c >= '가' && c <= '힯') || (c >= '豈' && c <= '﫿') || (c >= '＀' && c <= '￯');
        }
    }
}

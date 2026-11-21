// Barra de formatação embaixo dos campos de nome do editor do SDK: aplica as tags do TMP na parte selecionada
// do texto, ou no texto todo, para ninguém precisar decorar os códigos.
using System;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using static DoskerTools.EditorUi.DoskerLocale;

namespace DoskerTools.AuthoringTools
{
    internal static class MenuTextFormat
    {
        // Envolve o trecho [start, end) com as tags. Sem seleção, vale para o texto todo.
        public static string Wrap(string text, int start, int end, string open, string close, out int newStart, out int newEnd)
        {
            text ??= string.Empty;
            start = Mathf.Clamp(start, 0, text.Length);
            end = Mathf.Clamp(end, 0, text.Length);
            if (end < start)
            {
                (start, end) = (end, start);
            }
            if (start == end)
            {
                start = 0;
                end = text.Length;
            }
            newStart = start;
            newEnd = end + open.Length + close.Length;
            return text.Substring(0, start) + open + text.Substring(start, end - start) + close + text.Substring(end);
        }

        public static string Color(Color color)
        {
            return "<color=#" + ColorUtility.ToHtmlStringRGB(color) + ">";
        }

        // Tira as tags que o TMP reconhece e mantém <br> e o texto que só parece tag.
        public static string Strip(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return text ?? string.Empty;
            }
            var builder = new StringBuilder(text.Length);
            for (var i = 0; i < text.Length; i++)
            {
                if (text[i] == '<')
                {
                    var close = text.IndexOf('>', i + 1);
                    var next = text.IndexOf('<', i + 1);
                    if (close > i && (next < 0 || next > close) && close - i <= 129)
                    {
                        var tag = text.Substring(i, close - i + 1);
                        var keep = tag == "<br>" || tag == "<BR>";
                        var valid = MenuRichText.Parse(tag, 24f, new Color32(255, 255, 255, 255)).All(c => c.Kind != RichKind.Glyph);
                        if (valid && !keep)
                        {
                            i = close;
                            continue;
                        }
                    }
                }
                builder.Append(text[i]);
            }
            return builder.ToString();
        }
    }

    // A barra, ligada a um TextField do SDK. Guarda a última seleção, porque ela some quando o campo perde o foco.
    internal static class MenuNameFormatBar
    {
        internal const string BarName = "dosker-name-format-bar";
        private static readonly Regex NamePath = new Regex(@"^controls\.Array\.data\[\d+\]\.(name|labels\.Array\.data\[\d+\]\.name)$");
        private static readonly ConditionalWeakTable<TextField, SelectionRange> Bars = new ConditionalWeakTable<TextField, SelectionRange>();
        private static readonly string[] Sizes = { "50%", "75%", "125%", "150%", "200%" };

        private sealed class SelectionRange
        {
            public int Start;
            public int End;
        }

        public static void Decorate(VisualElement inspector)
        {
            inspector.Query<TextField>().ForEach(field =>
            {
                if (field == null || string.IsNullOrEmpty(field.bindingPath) || !NamePath.IsMatch(field.bindingPath) || Bars.TryGetValue(field, out _))
                {
                    return;
                }
                var row = field.parent;
                var host = row?.parent;
                if (host == null)
                {
                    return;
                }
                var selection = new SelectionRange();
                Bars.Add(field, selection);
                Track(field, selection);
                host.Insert(host.IndexOf(row) + 1, Build(field, selection));
            });
        }

        private static void Track(TextField field, SelectionRange selection)
        {
            void Remember()
            {
                selection.Start = Mathf.Min(field.cursorIndex, field.selectIndex);
                selection.End = Mathf.Max(field.cursorIndex, field.selectIndex);
            }
            field.RegisterCallback<KeyUpEvent>(_ => Remember(), TrickleDown.TrickleDown);
            field.RegisterCallback<PointerUpEvent>(_ => Remember(), TrickleDown.TrickleDown);
            field.RegisterCallback<PointerMoveEvent>(evt =>
            {
                if (evt.pressedButtons != 0)
                {
                    Remember();
                }
            }, TrickleDown.TrickleDown);
            field.RegisterCallback<FocusOutEvent>(_ => Remember(), TrickleDown.TrickleDown);
            // Outro controle no mesmo campo: a seleção antiga não vale mais.
            field.RegisterValueChangedCallback(evt =>
            {
                if (evt.previousValue == null || evt.newValue == null || Math.Abs(evt.newValue.Length - evt.previousValue.Length) > 1)
                {
                    selection.Start = selection.End = 0;
                }
            });
        }

        private static VisualElement Build(TextField field, SelectionRange selection)
        {
            var bar = new VisualElement { name = BarName };
            bar.style.flexDirection = FlexDirection.Row;
            bar.style.flexWrap = Wrap.Wrap;
            bar.style.alignItems = Align.Center;
            bar.style.marginTop = 2f;
            bar.style.marginBottom = 4f;

            void Apply(string open, string close)
            {
                var text = MenuTextFormat.Wrap(field.value, selection.Start, selection.End, open, close, out var start, out var end);
                field.value = text;
                selection.Start = start;
                selection.End = end;
                field.Focus();
                field.SelectRange(start, end);
            }

            bar.Add(Small("<b>B</b>", Tr("Negrito", "Bold"), () => Apply("<b>", "</b>")));
            bar.Add(Small("<i>I</i>", Tr("Itálico", "Italic"), () => Apply("<i>", "</i>")));
            bar.Add(Small("<u>U</u>", Tr("Sublinhado", "Underline"), () => Apply("<u>", "</u>")));
            bar.Add(Small("<s>S</s>", Tr("Riscado", "Strikethrough"), () => Apply("<s>", "</s>")));

            var color = new ColorField { value = new Color(1f, 0.35f, 0.35f), showAlpha = false, showEyeDropper = true, tooltip = Tr("Cor para aplicar", "Color to apply") };
            color.style.width = 56f;
            color.style.marginLeft = 6f;
            bar.Add(color);
            bar.Add(Small(Tr("Colorir", "Color"), Tr("Aplica a cor escolhida", "Applies the chosen color"), () => Apply(MenuTextFormat.Color(color.value), "</color>")));

            var size = new PopupField<string>(Sizes.ToList(), 3) { tooltip = Tr("Tamanho em relação ao texto normal", "Size relative to normal text") };
            size.style.width = 64f;
            size.style.marginLeft = 6f;
            bar.Add(size);
            bar.Add(Small(Tr("Tamanho", "Size"), Tr("Aplica o tamanho escolhido", "Applies the chosen size"), () => Apply("<size=" + size.value + ">", "</size>")));

            bar.Add(Small(Tr("Limpar", "Clear"), Tr("Remove toda a formatação", "Removes all formatting"), () =>
            {
                field.value = MenuTextFormat.Strip(field.value);
                selection.Start = selection.End = 0;
            }));

            var hint = new Label(Tr("Selecione um trecho ou aplique no nome todo.", "Select part of the text, or apply to the whole name."));
            hint.style.marginLeft = 6f;
            hint.style.opacity = 0.6f;
            hint.style.fontSize = 10f;
            bar.Add(hint);
            return bar;
        }

        // Botões sem foco, para o campo de texto não perder a seleção ao clicar.
        private static Button Small(string text, string tooltip, Action action)
        {
            var button = new Button(action) { text = text, tooltip = tooltip, focusable = false };
            button.style.minWidth = 24f;
            button.style.height = 20f;
            button.style.marginLeft = 1f;
            button.style.marginRight = 1f;
            button.style.paddingLeft = 6f;
            button.style.paddingRight = 6f;
            return button;
        }
    }
}

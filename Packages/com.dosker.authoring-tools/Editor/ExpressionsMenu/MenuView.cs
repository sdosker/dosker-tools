// Desenha o menu radial como no VRChat e trata o mouse. As medidas estão em unidades do menu: o
// diâmetro tem 600, a escala em que o texto base do TMP tem tamanho 24.
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.ScriptableObjects;
using ControlType = VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionsMenu.Control.ControlType;
using static DoskerTools.EditorUi.DoskerLocale;

namespace DoskerTools.AuthoringTools
{
    internal sealed class MenuView : IDisposable
    {
        internal const float Radius = 300f;
        internal const float InnerRadius = 100f;
        internal const float ContentRadius = 200f;
        internal const float IconSize = 100f;
        internal const float IconLift = 14f;
        internal const float LabelWidth = 200f;
        internal const float FontSize = 24f;
        private const float BorderWidth = 4f;
        private const float BadgeSize = 50f;
        private const float BadgeGlyph = 40f;
        private const float Margin = 0.9f;
        private const float RunningSpeed = 80f;
        private const float AxisReach = 0.9f;
        private const int LayoutCacheLimit = 256;

        private static readonly Vector2 BadgeOffset = new Vector2(45f, 11f);

        // Cores do Action Menu do VRChat.
        internal static readonly Color Backdrop = new Color(0.09f, 0.1f, 0.11f);

        private static readonly System.Reflection.MethodInfo DefaultBackground = typeof(EditorGUIUtility).GetMethod(
            "GetDefaultBackgroundColor", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);

        // A cor de fundo real de um elemento do UIToolkit: a do primeiro pai opaco. Assim o quadrado em volta do
        // menu some em qualquer tema, e as bordas suaves se misturam ao Inspector.
        internal static Color BackgroundOf(UnityEngine.UIElements.VisualElement element)
        {
            for (var current = element; current != null; current = current.parent)
            {
                var color = current.resolvedStyle.backgroundColor;
                if (color.a > 0.99f)
                {
                    return color;
                }
            }
            return EditorBackground;
        }

        // O fundo padrão das janelas do editor, quando não há elemento para consultar.
        internal static Color EditorBackground
        {
            get
            {
                if (DefaultBackground != null && DefaultBackground.Invoke(null, null) is Color color)
                {
                    color.a = 1f;
                    return color;
                }
                return EditorGUIUtility.isProSkin ? new Color32(56, 56, 56, 255) : new Color32(200, 200, 200, 255);
            }
        }
        internal static readonly Color SliceRim = new Color(0.14f, 0.18f, 0.2f);
        internal static readonly Color SliceCenter = new Color(0.06f, 0.27f, 0.29f);
        internal static readonly Color HoverRim = new Color(0.07f, 0.55f, 0.58f);
        internal static readonly Color HoverCenter = new Color(0.06f, 0.2f, 0.22f);
        internal static readonly Color Border = new Color(0.1f, 0.35f, 0.38f);
        internal static readonly Color Inner = new Color(0.21f, 0.24f, 0.27f);
        internal static readonly Color BadgeBackground = new Color(0.22f, 0.24f, 0.27f);
        internal static readonly Color Pill = new Color(0.11f, 0.11f, 0.11f, 0.49f);
        internal static readonly Color32 LabelColor = new Color32(210, 210, 210, 255);
        private static readonly Color Glyph = new Color(0.95f, 0.95f, 0.95f);

        private readonly MenuPainter painter = new MenuPainter();
        private readonly Dictionary<string, RichLayout> layouts = new Dictionary<string, RichLayout>();

        private int pressed = -1;
        private int dragControl;

        public int Hover { get; private set; } = -1;

        public MenuPainter Painter => painter;

        private enum Badge
        {
            None,
            Toggle,
            SubMenu,
            TwoAxis,
            FourAxis,
            Radial,
        }

        // Desenha no Repaint e devolve true quando há animação na tela.
        public bool Draw(Rect rect, MenuSession session, double now, bool trackHover, Color? background = null)
        {
            if (Event.current.type != EventType.Repaint || rect.width < 8f || rect.height < 8f || session == null || !painter.Ready)
            {
                return false;
            }

            Hover = trackHover && session.Puppet == null ? SliceAt(rect, Event.current.mousePosition, session) : -1;
            var scale = EditorGUIUtility.pixelsPerPoint;
            var width = Mathf.RoundToInt(rect.width * scale);
            var height = Mathf.RoundToInt(rect.height * scale);
            Render(width, height, session, now, Hover, background ?? EditorBackground);
            GUI.DrawTexture(rect, painter.Target, ScaleMode.StretchToFill, false);

            var animated = session.HasPendingButton || HasRunning(session);
            if (session.Current != null && session.Current.controls != null && session.Current.controls.Count > MenuSession.MaxControls)
            {
                var label = new Rect(rect.x + 6f, rect.y + 4f, rect.width - 12f, 18f);
                GUI.Label(label, Tr("Só os 8 primeiros controles aparecem no VRChat.", "Only the first 8 controls show in VRChat."), EditorStyles.whiteMiniLabel);
            }
            return animated;
        }

        // Renderiza na RenderTexture do painter, que fica em painter.Target.
        public void Render(int width, int height, MenuSession session, double now, int hover, Color? background = null)
        {
            painter.Begin(width, height, background ?? Backdrop);
            var center = new Vector2(width / 2f, height / 2f);
            var unit = Mathf.Min(width, height) / 2f * Margin / Radius;
            if (session.Puppet != null)
            {
                DrawPuppet(center, unit, session);
            }
            else
            {
                DrawMenu(center, unit, session, now, hover);
            }
            painter.End();
        }

        public bool HandleInput(Rect rect, MenuSession session, double now)
        {
            var e = Event.current;
            if (session == null)
            {
                return false;
            }
            var id = GUIUtility.GetControlID(FocusType.Passive, rect);
            switch (e.GetTypeForControl(id))
            {
                case EventType.MouseDown when rect.Contains(e.mousePosition):
                    if (e.button == 1)
                    {
                        session.Back();
                        e.Use();
                        return true;
                    }
                    if (e.button != 0)
                    {
                        return false;
                    }
                    if (session.Puppet != null)
                    {
                        GUIUtility.hotControl = id;
                        dragControl = id;
                        DragPuppet(rect, session, e.mousePosition, true);
                    }
                    else
                    {
                        pressed = SliceAt(rect, e.mousePosition, session);
                        if (pressed >= 0)
                        {
                            GUIUtility.hotControl = id;
                        }
                    }
                    e.Use();
                    return true;
                case EventType.MouseDrag when GUIUtility.hotControl == id:
                    if (session.Puppet != null && dragControl == id)
                    {
                        DragPuppet(rect, session, e.mousePosition, false);
                    }
                    e.Use();
                    return true;
                case EventType.MouseUp when GUIUtility.hotControl == id:
                    GUIUtility.hotControl = 0;
                    if (dragControl == id)
                    {
                        dragControl = 0;
                        // O joystick volta ao centro quando é solto.
                        if (session.Puppet != null && session.Puppet.type != ControlType.RadialPuppet)
                        {
                            session.SetAxes(Vector2.zero);
                        }
                    }
                    else if (pressed >= 0 && pressed == SliceAt(rect, e.mousePosition, session))
                    {
                        Activate(session, pressed, now);
                    }
                    pressed = -1;
                    e.Use();
                    return true;
                case EventType.MouseMove:
                    return rect.Contains(e.mousePosition) || Hover >= 0;
            }
            return false;
        }

        public static void Activate(MenuSession session, int slice, double now)
        {
            if (slice == 0)
            {
                session.Back();
                return;
            }
            var controls = MenuSession.Controls(session.Current);
            if (slice - 1 < controls.Count)
            {
                session.Activate(controls[slice - 1], now);
            }
        }

        // Fatia sob o mouse: 0 é o Back no topo, as outras seguem no sentido horário.
        public static int SliceAt(Rect rect, Vector2 point, MenuSession session)
        {
            if (!rect.Contains(point) || session == null || session.Puppet != null)
            {
                return -1;
            }
            var radius = Mathf.Min(rect.width, rect.height) / 2f * Margin;
            var offset = point - rect.center;
            if (offset.magnitude < radius * InnerRadius / Radius)
            {
                return -1;
            }
            var count = MenuSession.Controls(session.Current).Count + 1;
            var step = 360f / count;
            var angle = Mathf.Repeat(Mathf.Atan2(offset.x, -offset.y) * Mathf.Rad2Deg + step / 2f, 360f);
            return Mathf.Clamp(Mathf.FloorToInt(angle / step), 0, count - 1);
        }

        private void DrawMenu(Vector2 center, float unit, MenuSession session, double now, int hover)
        {
            var controls = MenuSession.Controls(session.Current);
            var count = controls.Count + 1;
            var step = 360f / count;
            var outer = Radius * unit;
            var inner = InnerRadius * unit;
            var t = InnerRadius / Radius;

            for (var i = 0; i < count; i++)
            {
                var hot = i == hover || i == pressed;
                var rim = hot ? HoverRim : SliceRim;
                var middle = hot ? HoverCenter : SliceCenter;
                painter.Sector(center, inner, outer, i * step - step / 2f, i * step + step / 2f, Color.Lerp(middle, rim, t), rim);
            }
            if (count > 1)
            {
                for (var i = 0; i < count; i++)
                {
                    var direction = MenuPainter.Direction(i * step - step / 2f);
                    painter.Line(center + direction * inner, center + direction * outer, BorderWidth * unit, Border);
                }
            }
            painter.Ring(center, outer, BorderWidth * unit, Border);
            painter.Disc(center, inner, Inner);
            painter.Ring(center, inner, BorderWidth * unit, Border);

            DrawSlice(center, unit, 0f, "Back", null, Badge.None, false, null, now, true);
            for (var i = 0; i < controls.Count; i++)
            {
                var control = controls[i];
                float? radial = control.type == ControlType.RadialPuppet ? session.Get(MenuSession.SubParameter(control, 0)) : (float?)null;
                var active = (control.type == ControlType.Toggle || control.type == ControlType.Button) && session.IsActive(control);
                DrawSlice(center, unit, (i + 1) * step, control.name, control.icon, BadgeFor(control.type), active, radial, now, false);
            }
        }

        private void DrawSlice(Vector2 center, float unit, float angle, string name, Texture2D icon, Badge badge, bool running, float? radial, double now, bool back)
        {
            var point = center + MenuPainter.Direction(angle) * ContentRadius * unit;
            var iconRect = Centered(point + new Vector2(0f, -IconLift) * unit, IconSize * unit);

            if (back)
            {
                DrawBackIcon(iconRect);
            }
            else
            {
                painter.Texture(icon, iconRect, Color.white);
            }
            if (running)
            {
                DrawRunning(iconRect, (float)(now * RunningSpeed % 360.0));
            }
            if (radial.HasValue)
            {
                var pill = new Rect(iconRect.xMin, iconRect.yMin + 30f * unit, iconRect.width, 40f * unit);
                painter.RoundedRect(pill, pill.height / 2f, Pill);
                var percent = Layout(Mathf.RoundToInt(Mathf.Clamp01(radial.Value) * 100f) + "%", 28f, IconSize, new Color32(255, 255, 255, 255));
                painter.Text(percent, new Vector2(pill.xMin, pill.center.y - percent.Bounds.height / 2f * unit), unit);
            }
            if (badge != Badge.None)
            {
                var badgeCenter = point + BadgeOffset * unit;
                painter.Disc(badgeCenter, BadgeSize / 2f * unit, BadgeBackground);
                DrawBadge(badge, badgeCenter, BadgeGlyph * unit);
            }

            var label = Layout(name, FontSize, LabelWidth, LabelColor);
            painter.Text(label, new Vector2(point.x - LabelWidth / 2f * unit, iconRect.yMax), unit);
        }

        private void DrawPuppet(Vector2 center, float unit, MenuSession session)
        {
            var control = session.Puppet;
            var outer = Radius * unit;
            var inner = InnerRadius * unit;
            var t = InnerRadius / Radius;

            painter.Sector(center, 0f, outer, 0f, 360f, SliceCenter, SliceRim);
            painter.Ring(center, outer, BorderWidth * unit, Border);

            if (control.type == ControlType.RadialPuppet)
            {
                var value = Mathf.Clamp01(session.RadialValue);
                painter.Sector(center, inner, outer, 0f, value * 360f, Color.Lerp(HoverCenter, HoverRim, t), HoverRim);
                var direction = MenuPainter.Direction(value * 360f);
                painter.Line(center + direction * inner, center + direction * outer, BorderWidth * 1.5f * unit, Glyph);
                painter.Disc(center, inner, Inner);
                painter.Ring(center, inner, BorderWidth * unit, Border);
                painter.Texture(control.icon, Centered(center + new Vector2(0f, -22f) * unit, 70f * unit), Color.white);
                var percent = Layout(Mathf.RoundToInt(value * 100f) + "%", 32f, InnerRadius * 2f, new Color32(255, 255, 255, 255));
                painter.Text(percent, new Vector2(center.x - InnerRadius * unit, center.y + 18f * unit), unit);
                return;
            }

            painter.Line(center + Vector2.left * outer, center + Vector2.right * outer, BorderWidth * unit, Border);
            painter.Line(center + Vector2.up * outer, center + Vector2.down * outer, BorderWidth * unit, Border);
            for (var i = 0; i < 4; i++)
            {
                var label = control.labels != null && i < control.labels.Length ? control.labels[i] : default;
                var point = center + MenuPainter.Direction(i * 90f) * ContentRadius * unit;
                var iconRect = Centered(point + new Vector2(0f, -IconLift) * unit, IconSize * 0.8f * unit);
                painter.Texture(label.icon, iconRect, Color.white);
                var layout = Layout(label.name, FontSize, LabelWidth, LabelColor);
                painter.Text(layout, new Vector2(point.x - LabelWidth / 2f * unit, iconRect.yMax), unit);
            }

            var axes = session.Axes;
            var cursor = center + new Vector2(axes.x, -axes.y) * outer * AxisReach;
            painter.Disc(cursor, 30f * unit, HoverRim);
            painter.Ring(cursor, 30f * unit, BorderWidth * unit, Glyph);
        }

        private static void DragPuppet(Rect rect, MenuSession session, Vector2 mouse, bool start)
        {
            var radius = Mathf.Min(rect.width, rect.height) / 2f * Margin;
            var offset = mouse - rect.center;
            if (session.Puppet.type == ControlType.RadialPuppet)
            {
                var value = Mathf.Repeat(Mathf.Atan2(offset.x, -offset.y) * Mathf.Rad2Deg, 360f) / 360f;
                // Não deixa o valor pular de 100% para 0% ao passar pelo topo.
                var current = session.RadialValue;
                if (!start && Mathf.Abs(value - current) > 0.5f)
                {
                    value = current > 0.5f ? 1f : 0f;
                }
                session.SetRadial(value);
                return;
            }
            var axes = new Vector2(offset.x, -offset.y) / (radius * AxisReach);
            session.SetAxes(axes);
        }

        private void DrawBackIcon(Rect rect)
        {
            var size = rect.width;
            Vector2 P(float x, float y) => rect.center + new Vector2(x, y) * size;
            var stroke = 0.09f * size;
            painter.Polygon(new[] { P(-0.42f, -0.12f), P(-0.12f, -0.38f), P(-0.12f, 0.14f) }, Glyph);
            painter.Line(P(-0.14f, -0.12f), P(0.04f, -0.12f), stroke, Glyph);
            painter.Arc(P(0.04f, 0.14f), 0.26f * size, stroke, 0f, 180f, Glyph);
        }

        private void DrawRunning(Rect rect, float rotation)
        {
            var center = rect.center;
            var radius = rect.width * 0.46f;
            var stroke = rect.width * 0.06f;
            for (var i = 0; i < 2; i++)
            {
                var from = rotation + i * 180f + 20f;
                var to = from + 130f;
                painter.Arc(center, radius, stroke, from, to, Glyph);
                // A ponta segue a tangente no sentido horário.
                var outward = MenuPainter.Direction(to);
                var tangent = MenuPainter.Direction(to + 90f);
                var head = stroke * 1.7f;
                painter.Polygon(new[]
                {
                    center + outward * radius + tangent * head * 1.3f,
                    center + outward * (radius + head),
                    center + outward * (radius - head),
                }, Glyph);
            }
        }

        private void DrawBadge(Badge badge, Vector2 center, float size)
        {
            Vector2 P(float x, float y) => center + new Vector2(x, y) * size;
            var stroke = 0.11f * size;
            switch (badge)
            {
                case Badge.Toggle:
                    var a = P(-0.25f, -0.36f);
                    var b = P(0.38f, 0f);
                    var c = P(-0.25f, 0.36f);
                    painter.Line(a, b, stroke, Glyph);
                    painter.Line(b, c, stroke, Glyph);
                    painter.Line(c, a, stroke, Glyph);
                    painter.Disc(a, stroke / 2f, Glyph);
                    painter.Disc(b, stroke / 2f, Glyph);
                    painter.Disc(c, stroke / 2f, Glyph);
                    break;
                case Badge.SubMenu:
                    for (var i = -1; i <= 1; i++)
                    {
                        painter.Disc(P(-0.3f, i * 0.28f), stroke * 0.7f, Glyph);
                        painter.Line(P(-0.12f, i * 0.28f), P(0.38f, i * 0.28f), stroke, Glyph);
                    }
                    break;
                case Badge.Radial:
                    painter.Arc(center, 0.32f * size, stroke, 45f, 330f, Glyph);
                    var end = center + MenuPainter.Direction(45f) * 0.32f * size;
                    painter.Polygon(new[] { end + new Vector2(-0.2f, -0.12f) * size, end + new Vector2(0.14f, -0.12f) * size, end + new Vector2(0.06f, 0.2f) * size }, Glyph);
                    break;
                case Badge.TwoAxis:
                case Badge.FourAxis:
                    for (var i = 0; i < 4; i++)
                    {
                        var direction = MenuPainter.Direction(i * 90f);
                        var side = new Vector2(-direction.y, direction.x);
                        painter.Line(center + direction * 0.14f * size, center + direction * 0.3f * size, stroke, Glyph);
                        painter.Polygon(new[] { center + direction * 0.48f * size, center + (direction * 0.28f + side * 0.15f) * size, center + (direction * 0.28f - side * 0.15f) * size }, Glyph);
                    }
                    painter.Disc(center, (badge == Badge.FourAxis ? 0.12f : 0.09f) * size, Glyph);
                    break;
            }
        }

        private static Badge BadgeFor(ControlType type)
        {
            switch (type)
            {
                case ControlType.Toggle:
                    return Badge.Toggle;
                case ControlType.SubMenu:
                    return Badge.SubMenu;
                case ControlType.TwoAxisPuppet:
                    return Badge.TwoAxis;
                case ControlType.FourAxisPuppet:
                    return Badge.FourAxis;
                case ControlType.RadialPuppet:
                    return Badge.Radial;
                default:
                    return Badge.None;
            }
        }

        private static bool HasRunning(MenuSession session)
        {
            if (session.Puppet != null)
            {
                return false;
            }
            foreach (var control in MenuSession.Controls(session.Current))
            {
                if ((control.type == ControlType.Toggle || control.type == ControlType.Button) && session.IsActive(control))
                {
                    return true;
                }
            }
            return false;
        }

        internal RichLayout Layout(string text, float size, float width, Color32 color)
        {
            var key = string.Concat(text ?? string.Empty, "\u0001", size.ToString("R"), "\u0001", width.ToString("R"), "\u0001", color.r, ",", color.g, ",", color.b, ",", color.a);
            if (layouts.TryGetValue(key, out var layout))
            {
                return layout;
            }
            if (layouts.Count > LayoutCacheLimit)
            {
                layouts.Clear();
            }
            layout = MenuRichText.Layout(MenuRichText.Parse(text, size, color), width, painter);
            layouts[key] = layout;
            return layout;
        }

        private static Rect Centered(Vector2 center, float size)
        {
            return new Rect(center.x - size / 2f, center.y - size / 2f, size, size);
        }

        public void Dispose()
        {
            painter.Dispose();
        }
    }
}

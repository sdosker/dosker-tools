using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

// Elementos visuais comuns das janelas ShaderView: cartões recolhíveis, indicadores de
// estado e botões desenhados em IMGUI. Usa apenas ícones embutidos do Editor; um ícone
// ausente vira só texto.
public static class DoskerEditorUi
{
    private const float CardHeaderHeight = 24f;
    private const float EyeButtonWidth = 24f;
    private const float AccentWidth = 3f;
    private const float CornerRadius = 4f;

    private static readonly Dictionary<string, Texture> IconCache = new Dictionary<string, Texture>();
    private static readonly GUIContent Scratch = new GUIContent();
    private static GUIStyle cardStyle;
    private static GUIStyle panelStyle;
    private static GUIStyle barStyle;
    private static GUIStyle titleStyle;
    private static GUIStyle badgeStyle;
    private static GUIStyle pillStyle;
    private static GUIStyle buttonLabelStyle;
    private static GUIStyle chipLabelStyle;
    private static GUIStyle barLabelStyle;

    public static Color Cyan
    {
        get { return Themed(new Color(0.2f, 0.85f, 0.8f), new Color(0.05f, 0.5f, 0.5f)); }
    }

    public static Color Yellow
    {
        get { return Themed(new Color(1f, 0.8f, 0.25f), new Color(0.7f, 0.5f, 0.05f)); }
    }

    public static Color Blue
    {
        get { return Themed(new Color(0.35f, 0.65f, 1f), new Color(0.15f, 0.4f, 0.8f)); }
    }

    public static Color Purple
    {
        get { return Themed(new Color(0.72f, 0.5f, 1f), new Color(0.45f, 0.25f, 0.75f)); }
    }

    public static Color Green
    {
        get { return Themed(new Color(0.35f, 0.85f, 0.45f), new Color(0.1f, 0.55f, 0.2f)); }
    }

    public static Color Red
    {
        get { return Themed(new Color(0.95f, 0.4f, 0.35f), new Color(0.75f, 0.2f, 0.15f)); }
    }

    public static Color Neutral
    {
        get { return Themed(new Color(0.6f, 0.6f, 0.6f), new Color(0.4f, 0.4f, 0.4f)); }
    }

    public static Color Divider
    {
        get { return Themed(new Color(0.14f, 0.14f, 0.14f), new Color(0.6f, 0.6f, 0.6f)); }
    }

    private static Color CardBackground
    {
        get { return Themed(new Color(0.26f, 0.26f, 0.26f), new Color(0.84f, 0.84f, 0.84f)); }
    }

    private static Color TrackBackground
    {
        get { return Themed(new Color(0.16f, 0.16f, 0.16f), new Color(0.66f, 0.66f, 0.66f)); }
    }

    private static Color TextColor
    {
        get { return EditorStyles.label.normal.textColor; }
    }

    private static GUIStyle CardStyle
    {
        get { return cardStyle ?? (cardStyle = new GUIStyle { padding = new RectOffset(10, 6, 2, 6), margin = new RectOffset(4, 4, 3, 3) }); }
    }

    private static GUIStyle PanelStyle
    {
        get { return panelStyle ?? (panelStyle = new GUIStyle { padding = new RectOffset(9, 5, 4, 5), margin = new RectOffset(2, 2, 2, 2) }); }
    }

    private static GUIStyle BarStyle
    {
        get { return barStyle ?? (barStyle = new GUIStyle { padding = new RectOffset(6, 6, 6, 6) }); }
    }

    private static GUIStyle TitleStyle
    {
        get { return titleStyle ?? (titleStyle = new GUIStyle(EditorStyles.boldLabel) { alignment = TextAnchor.MiddleLeft }); }
    }

    private static GUIStyle BadgeStyle
    {
        get { return badgeStyle ?? (badgeStyle = new GUIStyle(EditorStyles.miniBoldLabel) { alignment = TextAnchor.MiddleCenter, padding = new RectOffset() }); }
    }

    private static GUIStyle PillStyle
    {
        get { return pillStyle ?? (pillStyle = new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleLeft, padding = new RectOffset() }); }
    }

    private static GUIStyle ButtonLabelStyle
    {
        get
        {
            if (buttonLabelStyle == null)
            {
                buttonLabelStyle = new GUIStyle(EditorStyles.boldLabel) { alignment = TextAnchor.MiddleCenter };
                buttonLabelStyle.normal.textColor = Color.white;
            }
            return buttonLabelStyle;
        }
    }

    private static GUIStyle ChipLabelStyle
    {
        get { return chipLabelStyle ?? (chipLabelStyle = new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleCenter, padding = new RectOffset() }); }
    }

    private static GUIStyle BarLabelStyle
    {
        get { return barLabelStyle ?? (barLabelStyle = new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleCenter, padding = new RectOffset() }); }
    }

    private static Color Themed(Color dark, Color light)
    {
        return EditorGUIUtility.isProSkin ? dark : light;
    }

    private static Color WithAlpha(Color color, float alpha)
    {
        color.a = alpha;
        return color;
    }

    private static void RoundedRect(Rect rect, Color color, float radius = CornerRadius)
    {
        if (Event.current.type != EventType.Repaint)
        {
            return;
        }
        if (!GUI.enabled)
        {
            color.a *= 0.5f;
        }
        GUI.DrawTexture(rect, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0f, color, 0f, radius);
    }

    private static void DrawStyledLabel(Rect rect, string text, Texture icon, GUIStyle style, Color color)
    {
        if (Event.current.type != EventType.Repaint)
        {
            return;
        }
        if (!GUI.enabled)
        {
            color.a *= 0.5f;
        }
        style.normal.textColor = color;
        Scratch.text = text;
        Scratch.image = icon;
        Scratch.tooltip = null;
        var previousIconSize = EditorGUIUtility.GetIconSize();
        EditorGUIUtility.SetIconSize(new Vector2(16f, 16f));
        style.Draw(rect, Scratch, false, false, false, false);
        EditorGUIUtility.SetIconSize(previousIconSize);
        Scratch.image = null;
    }

    private static bool InvisibleButton(Rect rect, string tooltip)
    {
        Scratch.text = null;
        Scratch.image = null;
        Scratch.tooltip = tooltip;
        return GUI.Button(rect, Scratch, GUIStyle.none);
    }

    public static Texture Icon(string name)
    {
        var themedName = EditorGUIUtility.isProSkin ? "d_" + name : name;
        if (IconCache.TryGetValue(themedName, out var cached))
        {
            return cached;
        }
        // FindTexture não acusa ausência, mas não acha os ícones "... Icon"; IconContent acha
        // esses e registra erro quando o nome não existe (ex.: não há d_LockIcon).
        Texture texture = EditorGUIUtility.FindTexture(themedName);
        if (texture == null)
        {
            texture = EditorGUIUtility.FindTexture(name);
        }
        if (texture == null)
        {
            texture = EditorGUIUtility.IconContent(themedName).image;
        }
        IconCache[themedName] = texture;
        return texture;
    }

    public static GUIContent Content(string text, string iconName, string tooltip = null)
    {
        return new GUIContent(text, Icon(iconName), tooltip);
    }

    // Faixa fixa de topo ou rodapé, fora da área de rolagem.
    public static EditorGUILayout.VerticalScope ActionBar()
    {
        return new EditorGUILayout.VerticalScope(BarStyle);
    }

    public static void HorizontalLine()
    {
        var rect = GUILayoutUtility.GetRect(1f, 1f, GUILayout.ExpandWidth(true));
        if (Event.current.type == EventType.Repaint)
        {
            EditorGUI.DrawRect(rect, Divider);
        }
    }

    public sealed class CardScope : IDisposable
    {
        public readonly bool Expanded;

        public CardScope(string key, string title, string iconName, Color accent, string badge = null, bool defaultExpanded = true)
        {
            var visible = true;
            Expanded = Begin(key, title, iconName, accent, badge, defaultExpanded, ref visible, false);
        }

        // Com olho: o cabeçalho também liga e desliga a visibilidade da categoria. Clicar no
        // olho marca GUI.changed, como qualquer controle comum.
        public CardScope(string key, string title, string iconName, Color accent, string badge, ref bool visible, bool defaultExpanded = true)
        {
            Expanded = Begin(key, title, iconName, accent, badge, defaultExpanded, ref visible, true);
        }

        public void Dispose()
        {
            EditorGUILayout.EndVertical();
        }

        private static bool Begin(string key, string title, string iconName, Color accent, string badge, bool defaultExpanded, ref bool visible, bool hasEye)
        {
            var sessionKey = "ShaderView.Card." + key;
            var expanded = SessionState.GetBool(sessionKey, defaultExpanded);

            var cardRect = EditorGUILayout.BeginVertical(CardStyle);
            RoundedRect(cardRect, CardBackground);
            var accentRect = new Rect(cardRect.x, cardRect.y, AccentWidth, cardRect.height);
            RoundedRect(accentRect, visible ? accent : WithAlpha(accent, 0.3f), 1.5f);

            var row = GUILayoutUtility.GetRect(1f, CardHeaderHeight, GUILayout.ExpandWidth(true));
            var headerRect = row;
            if (hasEye)
            {
                headerRect.width -= EyeButtonWidth;
                var eyeRect = new Rect(row.xMax - EyeButtonWidth, row.y, EyeButtonWidth, row.height);
                var eyeIcon = Icon(visible ? "scenevis_visible_hover" : "scenevis_hidden_hover");
                var eyeLabel = eyeIcon != null ? new GUIContent(eyeIcon, "Mostrar na Scene View") : new GUIContent(visible ? "●" : "○", "Mostrar na Scene View");
                if (GUI.Button(eyeRect, eyeLabel, EditorStyles.label))
                {
                    visible = !visible;
                }
            }

            var previousChanged = GUI.changed;
            if (InvisibleButton(headerRect, null))
            {
                expanded = !expanded;
                SessionState.SetBool(sessionKey, expanded);
            }
            GUI.changed = previousChanged;

            if (Event.current.type == EventType.Repaint)
            {
                EditorStyles.foldout.Draw(new Rect(headerRect.x, headerRect.y + 5f, 14f, 14f), GUIContent.none, false, false, expanded, false);
            }
            var titleRect = new Rect(headerRect.x + 16f, headerRect.y, headerRect.width - 16f, headerRect.height);
            DrawStyledLabel(titleRect, " " + title, Icon(iconName), TitleStyle, TextColor);
            if (!string.IsNullOrEmpty(badge))
            {
                DrawBadge(new Rect(headerRect.x, headerRect.y, headerRect.width - 2f, headerRect.height), badge, accent);
            }
            return expanded;
        }
    }

    public sealed class PanelScope : IDisposable
    {
        public PanelScope(Color accent)
        {
            var rect = EditorGUILayout.BeginVertical(PanelStyle, GUILayout.ExpandWidth(true));
            RoundedRect(rect, CardBackground);
            RoundedRect(new Rect(rect.x, rect.y, AccentWidth, rect.height), accent, 1.5f);
        }

        public void Dispose()
        {
            EditorGUILayout.EndVertical();
        }
    }

    // Desenha o badge alinhado à direita do retângulo.
    private static void DrawBadge(Rect area, string text, Color color)
    {
        var width = BadgeStyle.CalcSize(new GUIContent(text)).x + 12f;
        var rect = new Rect(area.xMax - width, area.center.y - 8f, width, 16f);
        RoundedRect(rect, WithAlpha(color, 0.22f), 8f);
        DrawStyledLabel(rect, text, null, BadgeStyle, TextColor);
    }

    public static void Badge(string text, Color color)
    {
        var width = BadgeStyle.CalcSize(new GUIContent(text)).x + 12f;
        var rect = GUILayoutUtility.GetRect(width, 18f, GUILayout.ExpandWidth(false));
        DrawBadge(rect, text, color);
    }

    public static void StatusPill(string text, Color color, float height = 20f)
    {
        var width = PillStyle.CalcSize(new GUIContent(text)).x + 28f;
        var rect = GUILayoutUtility.GetRect(width, height, GUILayout.ExpandWidth(false));
        RoundedRect(rect, WithAlpha(color, 0.18f), height * 0.5f);
        RoundedRect(new Rect(rect.x + 8f, rect.center.y - 4f, 8f, 8f), color, 4f);
        DrawStyledLabel(new Rect(rect.x + 21f, rect.y, rect.width - 24f, rect.height), text, null, PillStyle, TextColor);
    }

    public static void ValueBar(Rect rect, float value, string label, Color color)
    {
        RoundedRect(rect, TrackBackground, 3f);
        var fill = rect;
        fill.width = rect.width * Mathf.Clamp01(value);
        if (fill.width >= 1f)
        {
            RoundedRect(fill, WithAlpha(color, 0.65f), 3f);
        }
        DrawStyledLabel(rect, label, null, BarLabelStyle, TextColor);
    }

    public static bool PrimaryButton(string text, string iconName, Color color, float height = 30f, params GUILayoutOption[] options)
    {
        var rect = GUILayoutUtility.GetRect(new GUIContent(text), ButtonLabelStyle, Merge(options, GUILayout.Height(height)));
        var hovered = GUI.enabled && rect.Contains(Event.current.mousePosition);
        RoundedRect(rect, hovered ? Color.Lerp(color, Color.white, 0.12f) : Color.Lerp(color, Color.black, 0.2f));
        DrawStyledLabel(rect, " " + text, Icon(iconName), ButtonLabelStyle, Color.white);
        return InvisibleButton(rect, null);
    }

    public static bool IconButton(string iconName, string fallbackText, string tooltip, float size = 30f)
    {
        var icon = Icon(iconName);
        var content = icon != null ? new GUIContent(icon, tooltip) : new GUIContent(fallbackText, tooltip);
        return GUILayout.Button(content, GUILayout.Width(size), GUILayout.Height(size));
    }

    public static bool ChipToggle(bool value, string text, string iconName, Color accent, string tooltip)
    {
        var rect = GUILayoutUtility.GetRect(new GUIContent(text), ChipLabelStyle, GUILayout.Height(22f), GUILayout.MinWidth(84f), GUILayout.ExpandWidth(true));
        RoundedRect(rect, value ? WithAlpha(accent, 0.4f) : TrackBackground);
        DrawStyledLabel(rect, " " + text, Icon(iconName), ChipLabelStyle, TextColor);
        return InvisibleButton(rect, tooltip) ? !value : value;
    }

    private static GUILayoutOption[] Merge(GUILayoutOption[] options, GUILayoutOption extra)
    {
        var merged = new GUILayoutOption[options.Length + 1];
        options.CopyTo(merged, 0);
        merged[options.Length] = extra;
        return merged;
    }
}

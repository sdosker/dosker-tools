using System;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace DoskerTools.EditorUi
{
    public static partial class DoskerEditorUi
    {
        public enum BannerKind
        {
            Info,
            Warning,
            Error,
            Success
        }

        // Coluna de Table: Weight divide a largura entre as colunas.
        public struct TableColumn
        {
            public string Title;
            public float Weight;
            public bool Sortable;

            public TableColumn(string title, float weight, bool sortable = true)
            {
                Title = title;
                Weight = weight;
                Sortable = sortable;
            }
        }

        private const float SplitterWidth = 5f;
        private const float BannerActionWidth = 110f;

        public static int Tabs(int selected, params string[] labels)
        {
            return GUILayout.Toolbar(selected, labels, GUILayout.Height(24f));
        }

        // Faixa vertical arrastável. Devolve a nova largura do painel à esquerda.
        public static float Splitter(Rect rect, float size, float min, float max, ref bool dragging)
        {
            var handle = new Rect(rect.xMax - SplitterWidth * 0.5f, rect.y, SplitterWidth, rect.height);
            EditorGUIUtility.AddCursorRect(handle, MouseCursor.ResizeHorizontal);
            var current = Event.current;
            if (current.type == EventType.Repaint)
            {
                EditorGUI.DrawRect(new Rect(rect.xMax, rect.y, 1f, rect.height), Divider);
            }
            else if (current.type == EventType.MouseDown && handle.Contains(current.mousePosition))
            {
                dragging = true;
                current.Use();
            }
            else if (dragging && current.type == EventType.MouseDrag)
            {
                size = Mathf.Clamp(size + current.delta.x, min, max);
                HandleUtility.Repaint();
                current.Use();
            }
            else if (dragging && current.rawType == EventType.MouseUp)
            {
                dragging = false;
            }
            return size;
        }

        // Aviso com ação opcional. Devolve true no clique da ação.
        public static bool Banner(string text, BannerKind kind, string action = null)
        {
            var clicked = false;
            using (new PanelScope(BannerColor(kind)))
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label(text, EditorStyles.wordWrappedLabel);
                if (!string.IsNullOrEmpty(action))
                {
                    clicked = GUILayout.Button(action, GUILayout.MaxWidth(BannerActionWidth));
                }
            }
            return clicked;
        }

        private static Color BannerColor(BannerKind kind)
        {
            switch (kind)
            {
                case BannerKind.Warning: return Yellow;
                case BannerKind.Error: return Red;
                case BannerKind.Success: return Green;
                default: return Blue;
            }
        }

        // O que aparece no lugar de uma lista vazia: o que falta e o próximo passo.
        public static void EmptyState(string title, string nextStep)
        {
            GUILayout.Space(SpacingLarge);
            GUILayout.Label(title, TitleStyle);
            GUILayout.Label(nextStep, EditorStyles.wordWrappedMiniLabel);
            GUILayout.Space(SpacingLarge);
        }

        // Campo de busca com botão de limpar. O SearchField é criado aqui porque não sobrevive a um
        // reload de scripts, e a janela só precisa guardar a referência entre repaints.
        public static string Search(ref SearchField field, string text, params GUILayoutOption[] options)
        {
            if (field == null)
            {
                field = new SearchField();
            }
            return field.OnGUI(GUILayoutUtility.GetRect(1f, 20f, options), text);
        }

        // Só desenha as linhas visíveis, para listas longas não pesarem no IMGUI.
        public static void VirtualList(int count, float rowHeight, float height, ref Vector2 scroll, Action<Rect, int> drawRow)
        {
            var viewport = GUILayoutUtility.GetRect(1f, height, GUILayout.ExpandWidth(true));
            var content = new Rect(0f, 0f, viewport.width - 14f, count * rowHeight);
            scroll = GUI.BeginScrollView(viewport, scroll, content);
            var first = Mathf.Max(0, Mathf.FloorToInt(scroll.y / rowHeight));
            var last = Mathf.Min(count, first + Mathf.CeilToInt(viewport.height / rowHeight) + 1);
            for (var index = first; index < last; index++)
            {
                drawRow(new Rect(0f, index * rowHeight, content.width, rowHeight), index);
            }
            GUI.EndScrollView();
        }

        // Cabeçalho de tabela. Clicar em uma coluna ordena; clicar de novo inverte. Devolve true se mudou.
        public static bool TableHeader(TableColumn[] columns, ref int sortColumn, ref bool ascending)
        {
            var rect = GUILayoutUtility.GetRect(1f, 20f, GUILayout.ExpandWidth(true));
            var changed = false;
            var cells = TableCells(rect, columns);
            for (var index = 0; index < columns.Length; index++)
            {
                var title = columns[index].Title + (index == sortColumn ? (ascending ? " ▲" : " ▼") : "");
                if (GUI.Button(cells[index], title, EditorStyles.toolbarButton) && columns[index].Sortable)
                {
                    ascending = index != sortColumn || !ascending;
                    sortColumn = index;
                    changed = true;
                }
            }
            return changed;
        }

        public static Rect[] TableCells(Rect row, TableColumn[] columns)
        {
            var total = 0f;
            foreach (var column in columns)
            {
                total += column.Weight;
            }

            var cells = new Rect[columns.Length];
            var x = row.x;
            for (var index = 0; index < columns.Length; index++)
            {
                var width = row.width * columns[index].Weight / total;
                cells[index] = new Rect(x, row.y, width, row.height);
                x += width;
            }
            return cells;
        }

        // Campo numérico com um botão que volta ao valor padrão.
        public static float FloatWithReset(string label, float value, float defaultValue, params GUILayoutOption[] options)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                value = EditorGUILayout.FloatField(label, value, options);
                using (new EditorGUI.DisabledScope(Mathf.Approximately(value, defaultValue)))
                {
                    if (GUILayout.Button(Content("", "Refresh", DoskerLocale.Tr("Voltar ao padrão", "Reset to default")), GUILayout.Width(24f), GUILayout.Height(18f)))
                    {
                        value = defaultValue;
                        GUI.changed = true;
                        GUIUtility.keyboardControl = 0;
                    }
                }
            }
            return value;
        }

        // Aviso passageiro para confirmar uma ação sem tomar espaço na janela.
        public static void Notify(EditorWindow window, string text)
        {
            window.ShowNotification(new GUIContent(text), 1.5f);
        }

        // Rodapé com versão, documentação, relato de bug e idioma.
        public static void AboutFooter(Type packageType, string docsUrl)
        {
            HorizontalLine();
            using (new EditorGUILayout.HorizontalScope())
            {
                var version = PackageInfo.FindForAssembly(packageType.Assembly)?.version;
                if (version != null)
                {
                    GUILayout.Label("v" + version, EditorStyles.miniLabel, GUILayout.ExpandWidth(false));
                }
                if (GUILayout.Button(DoskerLocale.Tr("Documentação", "Documentation"), EditorStyles.linkLabel, GUILayout.ExpandWidth(false)))
                {
                    Application.OpenURL(docsUrl);
                }
                if (GUILayout.Button(DoskerLocale.Tr("Relatar bug", "Report a bug"), EditorStyles.linkLabel, GUILayout.ExpandWidth(false)))
                {
                    Application.OpenURL(DoskerLocale.IssuesUrl);
                }
                GUILayout.FlexibleSpace();
                LanguageButton(EditorStyles.miniPullDown);
            }
        }

        // Globo com a sigla do idioma ("EN", "PT"). Abre o menu com os idiomas escritos no próprio idioma,
        // para quem não lê o idioma atual achar o seu.
        public static void LanguageButton(GUIStyle style = null)
        {
            style = style ?? EditorStyles.toolbarDropDown;
            // Ícone embutido, não emoji: as fontes do Editor não têm o caractere do globo.
            var icon = Icon(DoskerLocale.GlobeIcon);
            var content = new GUIContent(icon != null ? " " + DoskerLocale.ShortCode : DoskerLocale.ShortCode, icon, "Language / Idioma");
            var rect = GUILayoutUtility.GetRect(content, style, GUILayout.Width(52f));
            if (GUI.Button(rect, content, style))
            {
                DoskerLocale.ShowMenu(rect);
            }
        }
    }
}

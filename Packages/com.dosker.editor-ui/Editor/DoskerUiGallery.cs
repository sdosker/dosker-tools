#if DOSKER_UI_GALLERY
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;

namespace DoskerTools.EditorUi
{
    // Só para desenvolvimento: defina DOSKER_UI_GALLERY para conferir os componentes nos dois temas.
    public sealed class DoskerUiGallery : EditorWindow
    {
        private static readonly DoskerEditorUi.TableColumn[] Columns =
        {
            new DoskerEditorUi.TableColumn("Name", 3f),
            new DoskerEditorUi.TableColumn("Value", 1f),
            new DoskerEditorUi.TableColumn("Fixed", 1f, false),
        };

        [System.NonSerialized]
        private SearchField searchField;
        private string search = "";
        private int tab;
        private bool chip = true;
        private bool eye = true;
        private bool dragging;
        private float sidebar = 180f;
        private float number = 1f;
        private int sortColumn;
        private bool ascending = true;
        private Vector2 listScroll;

        [MenuItem("Tools/Dosker Tools/UI Gallery")]
        private static void Open()
        {
            GetWindow<DoskerUiGallery>("UI Gallery");
        }

        private void OnEnable()
        {
            wantsMouseMove = true;
        }

        private void OnGUI()
        {
            tab = DoskerEditorUi.Tabs(tab, "State", "Lists");
            search = DoskerEditorUi.Search(ref searchField, search);

            using (new DoskerEditorUi.CardScope("gallery", "Card", "Settings", DoskerEditorUi.Cyan, "12", ref eye))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    DoskerEditorUi.StatusPill("Ok", DoskerEditorUi.Green);
                    DoskerEditorUi.StatusPill("Warning", DoskerEditorUi.Yellow);
                    DoskerEditorUi.StatusPill("Error", DoskerEditorUi.Red);
                    DoskerEditorUi.StatusPill("Neutral", DoskerEditorUi.Neutral);
                }
                chip = DoskerEditorUi.ChipToggle(chip, "Chip", "Settings", DoskerEditorUi.Purple, "Tab focus test");
                DoskerEditorUi.PrimaryButton("Primary", "PlayButton", DoskerEditorUi.Blue);
                number = DoskerEditorUi.FloatWithReset("Number", number, 1f);
            }

            DoskerEditorUi.Banner("Warning with an action.", DoskerEditorUi.BannerKind.Warning, "Fix");
            DoskerEditorUi.EmptyState("Nothing here", "Select an object to start.");
            DoskerEditorUi.TableHeader(Columns, ref sortColumn, ref ascending);
            DoskerEditorUi.VirtualList(500, 18f, 120f, ref listScroll, (rect, index) => GUI.Label(rect, "Row " + index));

            var splitter = GUILayoutUtility.GetRect(1f, 24f, GUILayout.ExpandWidth(true));
            splitter.width = sidebar;
            sidebar = DoskerEditorUi.Splitter(splitter, sidebar, 100f, 300f, ref dragging);

            DoskerEditorUi.AboutFooter(typeof(DoskerUiGallery), DoskerLocale.IssuesUrl);
        }
    }
}
#endif

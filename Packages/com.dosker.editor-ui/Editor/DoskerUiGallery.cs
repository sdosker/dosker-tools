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
            new DoskerEditorUi.TableColumn("Nome", 3f),
            new DoskerEditorUi.TableColumn("Valor", 1f),
            new DoskerEditorUi.TableColumn("Fixa", 1f, false),
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
            tab = DoskerEditorUi.Tabs(tab, "Estado", "Listas");
            search = DoskerEditorUi.Search(ref searchField, search);

            using (new DoskerEditorUi.CardScope("gallery", "Cartão", "Settings", DoskerEditorUi.Cyan, "12", ref eye))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    DoskerEditorUi.StatusPill("Ok", DoskerEditorUi.Green);
                    DoskerEditorUi.StatusPill("Aviso", DoskerEditorUi.Yellow);
                    DoskerEditorUi.StatusPill("Erro", DoskerEditorUi.Red);
                    DoskerEditorUi.StatusPill("Neutro", DoskerEditorUi.Neutral);
                }
                chip = DoskerEditorUi.ChipToggle(chip, "Chip", "Settings", DoskerEditorUi.Purple, "Teste de foco com Tab");
                DoskerEditorUi.PrimaryButton("Principal", "PlayButton", DoskerEditorUi.Blue);
                number = DoskerEditorUi.FloatWithReset("Número", number, 1f);
            }

            DoskerEditorUi.Banner("Aviso com ação.", DoskerEditorUi.BannerKind.Warning, "Corrigir");
            DoskerEditorUi.EmptyState("Nada por aqui", "Selecione um objeto para começar.");
            DoskerEditorUi.TableHeader(Columns, ref sortColumn, ref ascending);
            DoskerEditorUi.VirtualList(500, 18f, 120f, ref listScroll, (rect, index) => GUI.Label(rect, "Linha " + index));

            var splitter = GUILayoutUtility.GetRect(1f, 24f, GUILayout.ExpandWidth(true));
            splitter.width = sidebar;
            sidebar = DoskerEditorUi.Splitter(splitter, sidebar, 100f, 300f, ref dragging);

            DoskerEditorUi.AboutFooter(typeof(DoskerUiGallery), DoskerLocale.IssuesUrl);
        }
    }
}
#endif

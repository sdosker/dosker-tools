// Prévia do menu dentro do Inspector de um VRCExpressionsMenu, abaixo dos campos do SDK. O editor do SDK não é
// substituído: a prévia é um elemento a mais no fim do InspectorElement. Se uma versão do Unity mudar isso, ela
// volta para o painel de preview do Inspector.
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using VRC.SDK3.Avatars.ScriptableObjects;
using static DoskerTools.EditorUi.DoskerLocale;

namespace DoskerTools.AuthoringTools
{
    // Sessões por menu, compartilhadas entre o Inspector e o painel de preview.
    internal static class MenuPreviewSessions
    {
        private static readonly Dictionary<int, MenuSession> Sessions = new Dictionary<int, MenuSession>();
        private static MenuView view;

        public static MenuView View => view ??= new MenuView();

        public static int Count => Sessions.Count;

        public static MenuSession For(VRCExpressionsMenu menu)
        {
            if (menu == null)
            {
                return null;
            }
            var id = menu.GetInstanceID();
            if (!Sessions.TryGetValue(id, out var session) || session.Root != menu)
            {
                var avatar = MenuSession.FindAvatar(menu);
                session = new MenuSession(menu, avatar != null ? avatar.expressionParameters : null, avatar);
                Sessions[id] = session;
            }
            return session;
        }

        // A sessão nova procura de novo o avatar e volta aos valores padrão.
        public static void Reset(VRCExpressionsMenu menu)
        {
            if (menu != null)
            {
                Sessions.Remove(menu.GetInstanceID());
            }
        }

        public static void Buttons(VRCExpressionsMenu menu, MenuSession session, GUIStyle style)
        {
            using (new EditorGUI.DisabledScope(session.Depth <= 1 && session.Puppet == null))
            {
                if (GUILayout.Button(Tr("Voltar", "Back"), style))
                {
                    session.Back();
                }
            }
            if (GUILayout.Button(Tr("Reiniciar", "Reset"), style))
            {
                Reset(menu);
            }
            if (GUILayout.Button(Tr("Janela", "Window"), style))
            {
                ExpressionsMenuPreviewWindow.Open(menu);
            }
        }

        // Trata o mouse e desenha. Devolve true quando precisa de outro quadro.
        public static bool Draw(Rect rect, VRCExpressionsMenu menu, bool hovering, Color? background = null)
        {
            var session = For(menu);
            if (session == null)
            {
                return false;
            }
            var now = EditorApplication.timeSinceStartup;
            session.Tick(now);
            View.HandleInput(rect, session, now);
            if (Event.current.type != EventType.Repaint)
            {
                return false;
            }
            var animated = View.Draw(rect, session, now, hovering, background);
            return animated || session.HasPendingButton;
        }
    }

    // Acrescenta a prévia ao fim do InspectorElement de cada VRCExpressionsMenu aberto num Inspector.
    [InitializeOnLoad]
    internal static class MenuInspectorInjector
    {
        internal const string ElementName = "dosker-expressions-menu-preview";
        private const string FoldoutKey = "DoskerTools.ExpressionsMenuPreview.Expanded";
        private const float MaxSize = 460f;
        private const double PollInterval = 0.25;

        private static readonly PropertyInfo EditorProperty = typeof(InspectorElement).GetProperty("editor", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static double nextPoll;

        static MenuInspectorInjector()
        {
            EditorApplication.update += Poll;
            Selection.selectionChanged += () => nextPoll = 0;
            ObjectChangeEvents.changesPublished += OnChanges;
        }

        // Mudanças feitas fora do Inspector, como um submenu ou ícone editado em outro lugar.
        private static void OnChanges(ref ObjectChangeEventStream stream)
        {
            foreach (var window in Resources.FindObjectsOfTypeAll<EditorWindow>())
            {
                if (window != null && window.GetType().Name == "InspectorWindow")
                {
                    window.rootVisualElement.Query<IMGUIContainer>(ElementName).ForEach(container => container.MarkDirtyRepaint());
                }
            }
            nextPoll = 0;
        }

        // False quando o Unity mudou a API interna; aí o painel de preview assume.
        public static bool Available => EditorProperty != null;

        private static void Poll()
        {
            var now = EditorApplication.timeSinceStartup;
            if (!Available || now < nextPoll)
            {
                return;
            }
            nextPoll = now + PollInterval;
            foreach (var window in Resources.FindObjectsOfTypeAll<EditorWindow>())
            {
                if (window != null && window.GetType().Name == "InspectorWindow")
                {
                    window.rootVisualElement.Query<InspectorElement>().ForEach(Inject);
                }
            }
        }

        private static void Inject(InspectorElement inspector)
        {
            if (!(EditorProperty.GetValue(inspector) is Editor editor) || !(editor.target is VRCExpressionsMenu menu))
            {
                return;
            }
            // O SDK cria e troca os campos de nome conforme o controle selecionado.
            MenuNameFormatBar.Decorate(inspector);
            var existing = inspector.Q<IMGUIContainer>(ElementName);
            if (existing != null)
            {
                if (existing.userData as Object == menu)
                {
                    return;
                }
                existing.RemoveFromHierarchy();
            }

            var container = new IMGUIContainer { name = ElementName, userData = menu };
            container.style.marginTop = 10f;
            container.onGUIHandler = () => OnGUI(container, menu);
            // O IMGUIContainer só redesenha em eventos; o hover precisa do movimento do mouse.
            container.RegisterCallback<MouseMoveEvent>(_ => container.MarkDirtyRepaint());
            container.RegisterCallback<MouseLeaveEvent>(_ => container.MarkDirtyRepaint());
            // Tempo real: qualquer mudança no asset (digitação, Undo, outra janela) redesenha a prévia.
            container.TrackSerializedObjectValue(new SerializedObject(menu), _ => container.MarkDirtyRepaint());
            inspector.RegisterCallback<ChangeEvent<string>>(_ => container.MarkDirtyRepaint());
            inspector.Add(container);
        }

        private static void OnGUI(IMGUIContainer container, VRCExpressionsMenu menu)
        {
            if (menu == null)
            {
                return;
            }
            var session = MenuPreviewSessions.For(menu);
            var expanded = EditorPrefs.GetBool(FoldoutKey, true);
            using (new EditorGUILayout.HorizontalScope())
            {
                var open = EditorGUILayout.Foldout(expanded, Tr("Prévia no VRChat", "VRChat preview"), true, EditorStyles.foldoutHeader);
                if (open != expanded)
                {
                    EditorPrefs.SetBool(FoldoutKey, expanded = open);
                }
                if (expanded)
                {
                    GUILayout.FlexibleSpace();
                    MenuPreviewSessions.Buttons(menu, session, EditorStyles.miniButton);
                }
            }
            if (!expanded)
            {
                return;
            }

            var path = string.Join(" › ", session.Path.Select(MenuSession.DisplayName));
            if (session.Puppet != null)
            {
                path += " › " + session.Puppet.name;
            }
            EditorGUILayout.LabelField(path, EditorStyles.miniLabel);

            var width = Mathf.Max(container.contentRect.width, 100f);
            var size = Mathf.Min(width, MaxSize);
            var area = GUILayoutUtility.GetRect(width, size, GUILayout.ExpandWidth(true));
            var rect = new Rect(area.x + (area.width - size) / 2f, area.y, size, size);
            var hovering = rect.Contains(Event.current.mousePosition);
            if (MenuPreviewSessions.Draw(rect, menu, hovering, MenuView.BackgroundOf(container)))
            {
                container.schedule.Execute(container.MarkDirtyRepaint).StartingIn(33);
            }
            EditorGUILayout.LabelField(Tr(
                "Clique para usar os controles; o botão direito volta. Os parâmetros vêm do avatar da cena que usa este menu.",
                "Click to use the controls; right click goes back. Parameters come from the scene avatar that uses this menu."), EditorStyles.centeredGreyMiniLabel);
        }
    }

    // Reserva para quando o Inspector não permite a prévia embutida.
    [CustomPreview(typeof(VRCExpressionsMenu))]
    internal sealed class ExpressionsMenuPreview : ObjectPreview
    {
        public override bool HasPreviewGUI()
        {
            return target is VRCExpressionsMenu && !MenuInspectorInjector.Available;
        }

        public override GUIContent GetPreviewTitle()
        {
            return new GUIContent(Tr("Prévia no VRChat", "VRChat preview"));
        }

        public override void OnPreviewSettings()
        {
            var menu = target as VRCExpressionsMenu;
            var session = MenuPreviewSessions.For(menu);
            if (session != null)
            {
                MenuPreviewSessions.Buttons(menu, session, EditorStyles.toolbarButton);
            }
        }

        public override void OnInteractivePreviewGUI(Rect rect, GUIStyle background)
        {
            MenuPreviewSessions.Draw(rect, target as VRCExpressionsMenu, rect.Contains(Event.current.mousePosition));
        }
    }
}

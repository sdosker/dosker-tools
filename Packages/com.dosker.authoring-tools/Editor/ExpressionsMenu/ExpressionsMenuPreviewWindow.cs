// Janela que mostra o menu de expressões da seleção, com os parâmetros editáveis ao lado.
using System;
using System.Linq;
using DoskerTools.EditorUi;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using static DoskerTools.EditorUi.DoskerLocale;
using ValueType = VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionParameters.ValueType;

namespace DoskerTools.AuthoringTools
{
    internal sealed class ExpressionsMenuPreviewWindow : EditorWindow
    {
        private const string MenuPath = "Tools/Dosker Tools/Expressions Menu Preview";
        private const float ParametersWidth = 260f;

        [SerializeField] private VRCAvatarDescriptor avatar;
        [SerializeField] private VRCExpressionsMenu menu;
        [SerializeField] private bool followSelection = true;
        [SerializeField] private bool showParameters = true;

        private MenuSession session;
        private MenuView view;
        private Vector2 scroll;
        private double lastFrame;

        [MenuItem(MenuPath, false, 30)]
        private static void OpenFromMenu()
        {
            Open(Selection.activeObject as VRCExpressionsMenu);
        }

        public static void Open(VRCExpressionsMenu target)
        {
            var window = GetWindow<ExpressionsMenuPreviewWindow>();
            window.titleContent = new GUIContent("Expressions Menu", EditorGUIUtility.IconContent("d_UnityEditor.InspectorWindow").image);
            window.minSize = new Vector2(320f, 320f);
            if (target != null)
            {
                window.SetTarget(null, target);
            }
            else
            {
                window.FollowSelection();
            }
            window.Show();
        }

        private void OnEnable()
        {
            wantsMouseMove = true;
            Selection.selectionChanged += OnSelectionChanged;
            EditorApplication.update += OnUpdate;
            DoskerLocale.Changed += Repaint;
            // Tempo real: o menu muda enquanto o nome é digitado no Inspector.
            ObjectChangeEvents.changesPublished += OnChanges;
            Undo.undoRedoPerformed += Repaint;
        }

        private void OnDisable()
        {
            Selection.selectionChanged -= OnSelectionChanged;
            EditorApplication.update -= OnUpdate;
            DoskerLocale.Changed -= Repaint;
            ObjectChangeEvents.changesPublished -= OnChanges;
            Undo.undoRedoPerformed -= Repaint;
            view?.Dispose();
            view = null;
        }

        private void OnChanges(ref ObjectChangeEventStream stream)
        {
            Repaint();
        }

        private void OnSelectionChanged()
        {
            if (followSelection)
            {
                FollowSelection();
                Repaint();
            }
        }

        private void FollowSelection()
        {
            if (Selection.activeObject is VRCExpressionsMenu selectedMenu)
            {
                SetTarget(null, selectedMenu);
                return;
            }
            var selectedAvatar = Selection.activeGameObject != null ? Selection.activeGameObject.GetComponentInParent<VRCAvatarDescriptor>(true) : null;
            if (selectedAvatar != null && selectedAvatar.expressionsMenu != null)
            {
                SetTarget(selectedAvatar, selectedAvatar.expressionsMenu);
            }
        }

        private void SetTarget(VRCAvatarDescriptor newAvatar, VRCExpressionsMenu newMenu)
        {
            if (newAvatar == null && newMenu != null)
            {
                newAvatar = MenuSession.FindAvatar(newMenu);
            }
            if (session != null && menu == newMenu && avatar == newAvatar)
            {
                return;
            }
            avatar = newAvatar;
            menu = newMenu;
            session = null;
        }

        private void OnUpdate()
        {
            if (session == null)
            {
                return;
            }
            var now = EditorApplication.timeSinceStartup;
            var busy = session.Tick(now);
            if ((busy || animating) && now - lastFrame > 1.0 / 30.0)
            {
                lastFrame = now;
                Repaint();
            }
        }

        private bool animating;

        private void OnGUI()
        {
            view ??= new MenuView();
            if (session == null && menu != null)
            {
                session = new MenuSession(menu, avatar != null ? avatar.expressionParameters : null, avatar);
            }

            DrawToolbar();
            if (session == null)
            {
                DoskerEditorUi.EmptyState(
                    Tr("Nenhum menu selecionado", "No menu selected"),
                    Tr("Selecione um VRCExpressionsMenu ou um avatar com menu de expressões.", "Select a VRCExpressionsMenu or an avatar with an expressions menu."));
                return;
            }

            var area = new Rect(0f, EditorGUIUtility.singleLineHeight + 4f, position.width, position.height - EditorGUIUtility.singleLineHeight - 4f);
            var menuRect = area;
            if (showParameters && area.width > ParametersWidth + 240f)
            {
                menuRect.width -= ParametersWidth;
                DrawParameters(new Rect(menuRect.xMax, area.y, ParametersWidth, area.height));
            }

            var now = EditorApplication.timeSinceStartup;
            if (view.HandleInput(menuRect, session, now))
            {
                Repaint();
            }
            if (Event.current.type == EventType.Repaint)
            {
                animating = view.Draw(menuRect, session, now, true, MenuView.BackgroundOf(rootVisualElement));
            }
        }

        private void DrawToolbar()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                using (new EditorGUI.DisabledScope(session == null || (session.Depth <= 1 && session.Puppet == null)))
                {
                    if (GUILayout.Button(Tr("Voltar", "Back"), EditorStyles.toolbarButton, GUILayout.Width(60f)))
                    {
                        session.Back();
                    }
                    if (GUILayout.Button(Tr("Início", "Root"), EditorStyles.toolbarButton, GUILayout.Width(50f)))
                    {
                        session.BackToRoot();
                    }
                }

                var path = session != null ? string.Join(" › ", session.Path.Select(MenuSession.DisplayName)) : string.Empty;
                if (session != null && session.Puppet != null)
                {
                    path += " › " + session.Puppet.name;
                }
                GUILayout.Label(path, EditorStyles.toolbarButton, GUILayout.ExpandWidth(true));

                using (new EditorGUI.DisabledScope(session == null))
                {
                    if (GUILayout.Button(Tr("Reiniciar", "Reset"), EditorStyles.toolbarButton, GUILayout.Width(70f)))
                    {
                        session.Reset();
                    }
                }
                followSelection = GUILayout.Toggle(followSelection, Tr("Seguir seleção", "Follow selection"), EditorStyles.toolbarButton, GUILayout.Width(110f));
                showParameters = GUILayout.Toggle(showParameters, Tr("Parâmetros", "Parameters"), EditorStyles.toolbarButton, GUILayout.Width(80f));
                DoskerEditorUi.LanguageButton(EditorStyles.toolbarButton);
            }
        }

        private void DrawParameters(Rect rect)
        {
            EditorGUI.DrawRect(new Rect(rect.x, rect.y, 1f, rect.height), DoskerEditorUi.Divider);
            GUILayout.BeginArea(new Rect(rect.x + 6f, rect.y + 4f, rect.width - 10f, rect.height - 8f));
            EditorGUI.BeginChangeCheck();
            var newMenu = (VRCExpressionsMenu)EditorGUILayout.ObjectField(menu, typeof(VRCExpressionsMenu), false);
            var newAvatar = (VRCAvatarDescriptor)EditorGUILayout.ObjectField(avatar, typeof(VRCAvatarDescriptor), true);
            if (EditorGUI.EndChangeCheck())
            {
                followSelection = false;
                if (newAvatar != avatar && newAvatar != null && newAvatar.expressionsMenu != null)
                {
                    newMenu = newAvatar.expressionsMenu;
                }
                avatar = newAvatar;
                menu = newMenu;
                session = null;
                GUILayout.EndArea();
                return;
            }

            if (session.Parameters == null)
            {
                EditorGUILayout.HelpBox(Tr(
                    "Sem VRCExpressionParameters: nenhum avatar na cena usa este menu. Os valores começam em zero.",
                    "No VRCExpressionParameters: no avatar in the scene uses this menu. Values start at zero."), MessageType.Info);
            }

            scroll = EditorGUILayout.BeginScrollView(scroll);
            var names = session.Values.Keys.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var name in names)
            {
                var value = session.Get(name);
                session.TryGetType(name, out var type);
                var known = session.IsKnown(name);
                EditorGUI.BeginChangeCheck();
                float edited;
                var label = new GUIContent(name, known ? type.ToString() : Tr("Fora do VRCExpressionParameters", "Not in VRCExpressionParameters"));
                if (known && type == ValueType.Bool)
                {
                    edited = EditorGUILayout.Toggle(label, value > 0.5f) ? 1f : 0f;
                }
                else if (known && type == ValueType.Int)
                {
                    edited = EditorGUILayout.IntSlider(label, Mathf.RoundToInt(value), 0, 255);
                }
                else
                {
                    edited = EditorGUILayout.Slider(label, value, -1f, 1f);
                }
                if (EditorGUI.EndChangeCheck())
                {
                    session.Set(name, edited);
                }
            }
            EditorGUILayout.EndScrollView();
            GUILayout.EndArea();
        }
    }
}

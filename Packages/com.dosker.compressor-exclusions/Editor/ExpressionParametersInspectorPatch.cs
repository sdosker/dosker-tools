using System;
using System.Linq;
using HarmonyLib;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using static DoskerTools.EditorUi.DoskerLocale;
using ParamType = VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionParameters.ValueType;

namespace CompressorExclusions {
    /**
     * Adds a "Compressão" column to the VRC Expression Parameters inspector.
     * If the SDK changes this inspector, nothing is injected and it keeps working as usual.
     */
    [InitializeOnLoad]
    internal static class ExpressionParametersInspectorPatch {
        private const string EditorTypeName = "VRC.SDK3.Editor.VRCExpressionParametersEditor";
        private const string ButtonName = "CompressorChoiceButton";
        private const int RefreshIntervalMs = 200;

        static ExpressionParametersInspectorPatch() {
            try {
                var editorType = AppDomain.CurrentDomain.GetAssemblies()
                    .Select(assembly => assembly.GetType(EditorTypeName))
                    .FirstOrDefault(type => type != null);
                var method = editorType?.GetMethod("CreateInspectorGUI");
                if (method == null) {
                    Debug.LogWarning("[Compressor Exclusions] Parameters inspector not found, skipping the column.");
                    return;
                }
                new Harmony("compressor-exclusions.inspector").Patch(
                    method,
                    postfix: new HarmonyMethod(AccessTools.Method(typeof(ExpressionParametersInspectorPatch), nameof(Postfix)))
                );
            } catch (Exception e) {
                Debug.LogWarning($"[Compressor Exclusions] Could not patch the parameters inspector: {e}");
            }
        }

        private static void Postfix(VisualElement __result) {
            try {
                Inject(__result);
            } catch (Exception e) {
                Debug.LogWarning($"[Compressor Exclusions] Could not add the column to the inspector: {e}");
            }
        }

        private static void Inject(VisualElement root) {
            var list = root.Q<ListView>("ParametersListView");
            var header = root.Q("ListHeader");
            var toolbar = root.Q("ActionsToolbar");
            if (list == null || header == null || toolbar == null || list.bindItem == null) return;

            CompressorUi.ApplyStyles(root);

            var headerLabel = new Label(Tr("Compressão", "Compression"));
            headerLabel.AddToClassList("ce-inspector-header");
            headerLabel.tooltip = Tr("Clique no botão de cada parâmetro para alternar entre Auto, Proteger e Comprimir.", "Click each parameter's button to cycle between Auto, Protect and Compress.");
            header.Add(headerLabel);

            toolbar.Add(new ToolbarButton(CompressorExclusionsWindow.Open) {
                text = Tr("Abrir configuração", "Open settings"),
                tooltip = Tr("Abre a configuração de compressão do VRCFury", "Opens the VRCFury compression settings")
            });

            var originalBind = list.bindItem;
            list.bindItem = (element, index) => {
                originalBind(element, index);
                AttachButton(element)?.Invoke();
            };
        }

        /**
         * Rows are recycled, so the button reads the row's fields rather than remembering a parameter.
         * Polling also covers renames, type changes, sync changes and changes made in the window.
         */
        private static Action AttachButton(VisualElement element) {
            var row = element.Q("ParameterField");
            var nameField = element.Q<TextField>("NameField");
            var typeField = element.Q<EnumField>("TypeField");
            var syncedField = element.Q<Toggle>("SyncedField");
            if (row == null || nameField == null || typeField == null || syncedField == null) return null;

            if (row.Q<Button>(ButtonName)?.userData is Action existing) return existing;

            var button = new Button { name = ButtonName };
            button.AddToClassList("ce-inspector-button");
            button.clicked += () => {
                var settings = CompressorExclusionSettings.Instance;
                var next = NextChoice(settings.GetChoice(nameField.value));
                settings.SetChoice(new[] { nameField.value }, next);
            };
            row.Add(button);

            var lastState = "";
            Action refresh = () => {
                var hasType = typeField.value is ParamType;
                var paramName = nameField.value;
                var enabled = syncedField.value && hasType && !string.IsNullOrEmpty(paramName);

                var settings = CompressorExclusionSettings.Instance;
                string text;
                string tooltip;
                string style;
                if (enabled) {
                    var known = CompressorSnapshotStore.Find(paramName);
                    var control = known?.control ?? CompressorPolicy.NoControl;
                    var verdict = CompressorPolicy.Evaluate(settings, paramName, (ParamType)typeField.value, control, known?.menuPath ?? "");
                    text = CompressorUi.ChoiceLabel(verdict.choice);
                    tooltip = verdict.details + (known == null ? "" : LastBuildText(known))
                              + "\n" + Tr("Clique para alternar: Auto, Proteger, Comprimir.", "Click to cycle: Auto, Protect, Compress.");
                    style = verdict.isProtected ? "ce-chip--protected" : "ce-chip--open";
                } else {
                    text = "—";
                    tooltip = Tr("Parâmetros não sincronizados não passam pelo compressor.", "Unsynced parameters don't go through the compressor.");
                    style = "ce-chip--off";
                }

                var state = $"{text}|{tooltip}|{style}|{enabled}";
                if (state == lastState) return;
                lastState = state;
                button.text = text;
                button.tooltip = tooltip;
                button.SetEnabled(enabled);
                button.RemoveFromClassList("ce-chip--protected");
                button.RemoveFromClassList("ce-chip--open");
                button.RemoveFromClassList("ce-chip--off");
                button.AddToClassList(style);
            };
            button.userData = refresh;
            button.schedule.Execute(refresh).Every(RefreshIntervalMs);
            return refresh;
        }

        private static string LastBuildText(SnapshotParam param) {
            var bits = param.cost > 0 ? param.cost : CompressorPolicy.CostOf((ParamType)param.valueType);
            return Tr($"\nÚltimo build: {bits} bits, {(param.compressed ? "comprimido" : "sincronizando direto")}.",
                      $"\nLast build: {bits} bits, {(param.compressed ? "compressed" : "syncing directly")}.");
        }

        private static ParamChoice NextChoice(ParamChoice choice) {
            switch (choice) {
                case ParamChoice.Auto: return ParamChoice.Protect;
                case ParamChoice.Protect: return ParamChoice.Compress;
                default: return ParamChoice.Auto;
            }
        }
    }
}

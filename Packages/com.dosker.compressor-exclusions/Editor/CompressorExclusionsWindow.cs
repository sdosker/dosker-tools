using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using ControlType = VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionsMenu.Control.ControlType;
using ParamType = VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionParameters.ValueType;

namespace CompressorExclusions {
    internal class CompressorExclusionsWindow : EditorWindow {
        private enum Filter {
            All,
            Compressed,
            Protected,
            Unavailable
        }

        private enum RowStatus {
            Compressed,
            Protected,
            Direct,
            Unavailable
        }

        private class Row {
            public SnapshotParam param;
            public string displayName;
            public string typeLabel;
            public string controlLabel;
            public int cost;
            public bool canCompress;
            public RowStatus status;
            public string reason;
            public ParamChoice choice;
        }

        private class RowView {
            public Row row;
            public Label name;
            public Label type;
            public Label control;
            public Label bits;
            public Label status;
            public Dictionary<ParamChoice, Button> choices;
        }

        private class PatternView {
            public VisualElement root;
            public TextField field;
            public Label count;
        }

        private static readonly (ControlType type, string label)[] ControlRules = {
            (ControlType.Toggle, "Toggle"),
            (ControlType.RadialPuppet, "Radial"),
            (ControlType.TwoAxisPuppet, "Puppet 2 eixos"),
            (ControlType.FourAxisPuppet, "Puppet 4 eixos"),
        };

        private static readonly ControlType[] CompressibleControls = ControlRules.Select(rule => rule.type).ToArray();

        private static readonly ParamChoice[] Choices = { ParamChoice.Auto, ParamChoice.Protect, ParamChoice.Compress };

        private static readonly (Filter filter, string label)[] Filters = {
            (Filter.All, "Todos"),
            (Filter.Compressed, "Comprimidos"),
            (Filter.Protected, "Protegidos"),
            (Filter.Unavailable, "Sem compressão"),
        };

        private Toggle activeToggle;
        private Label statusPill;
        private DropdownField avatarDropdown;
        private VisualElement barFill;
        private Label barText;
        private Label barHint;
        private Label statCompressed;
        private Label statProtected;
        private Label statDelay;
        private Label statBuild;
        private VisualElement banners;
        private Toggle protectBool;
        private Toggle protectInt;
        private Toggle protectFloat;
        private Dictionary<ControlType, Toggle> controlToggles;
        private VisualElement patternList;
        private readonly List<PatternView> patternViews = new List<PatternView>();
        private ToolbarSearchField searchField;
        private Dictionary<Filter, Button> filterButtons;
        private VisualElement bulkBar;
        private Label bulkLabel;
        private ListView table;
        private Label emptyLabel;

        private Snapshot snapshot;
        private string selectedAvatar;
        private Filter filter = Filter.All;
        private List<Row> allRows = new List<Row>();
        private readonly List<Row> visibleRows = new List<Row>();

        private static CompressorExclusionSettings Settings => CompressorExclusionSettings.Instance;

        [MenuItem("Tools/Dosker Tools/Compressor Exclusions")]
        public static void Open() {
            var window = GetWindow<CompressorExclusionsWindow>();
            window.titleContent = new GUIContent("Compressor Exclusions");
            window.minSize = new Vector2(640, 540);
        }

        private void OnEnable() {
            CompressorExclusionSettings.Changed += Refresh;
            CompressorSnapshotStore.Updated += OnSnapshotUpdated;
        }

        private void OnDisable() {
            CompressorExclusionSettings.Changed -= Refresh;
            CompressorSnapshotStore.Updated -= OnSnapshotUpdated;
        }

        private void OnSnapshotUpdated() {
            // This arrives in the middle of a build, so wait for it to end
            EditorApplication.delayCall += () => {
                if (this != null) Refresh();
            };
        }

        public void CreateGUI() {
            var root = new VisualElement();
            root.AddToClassList("ce-root");
            root.style.flexGrow = 1;
            CompressorUi.ApplyStyles(rootVisualElement);
            rootVisualElement.Add(root);

            root.Add(BuildHeader());
            root.Add(BuildSummary());
            root.Add(BuildRules());
            root.Add(BuildTableToolbar());
            root.Add(BuildBulkBar());
            root.Add(BuildTableHeader());
            root.Add(BuildTable());

            Refresh();
        }

        private VisualElement BuildHeader() {
            var header = new VisualElement();
            header.AddToClassList("ce-header");

            var title = new Label("Compressão de parâmetros");
            title.AddToClassList("ce-title");
            header.Add(title);

            var version = new Label("v" + CompressorUi.Version);
            version.AddToClassList("ce-version");
            header.Add(version);

            statusPill = new Label();
            statusPill.AddToClassList("ce-pill");
            header.Add(statusPill);

            var spacer = new VisualElement();
            spacer.AddToClassList("ce-spacer");
            header.Add(spacer);

            avatarDropdown = new DropdownField { tooltip = "Avatar usado para mostrar os parâmetros. As regras valem para o projeto todo." };
            avatarDropdown.style.minWidth = 140;
            avatarDropdown.RegisterValueChangedCallback(evt => {
                selectedAvatar = evt.newValue;
                Refresh();
            });
            header.Add(avatarDropdown);

            activeToggle = new Toggle("Ativo") {
                tooltip = "Desligado, o VRCFury comprime como faria sem esta ferramenta."
            };
            activeToggle.labelElement.style.minWidth = 36;
            activeToggle.style.marginLeft = 10;
            activeToggle.RegisterValueChangedCallback(evt =>
                Settings.Mutate("Toggle Compressor Exclusions", s => s.active = evt.newValue));
            header.Add(activeToggle);
            return header;
        }

        private VisualElement BuildSummary() {
            var card = new VisualElement();
            card.AddToClassList("ce-card");

            var labels = new VisualElement();
            labels.AddToClassList("ce-bar-label");
            barText = new Label();
            barText.AddToClassList("ce-bar-text");
            labels.Add(barText);
            barHint = new Label();
            barHint.AddToClassList("ce-muted");
            barHint.style.marginLeft = 8;
            labels.Add(barHint);
            card.Add(labels);

            var track = new VisualElement();
            track.AddToClassList("ce-bar-track");
            barFill = new VisualElement();
            barFill.AddToClassList("ce-bar-fill");
            track.Add(barFill);
            card.Add(track);

            var stats = new VisualElement();
            stats.AddToClassList("ce-stats");
            statCompressed = AddStat(stats, "Comprimidos", "Parâmetros que sincronizam em lotes, com atraso");
            statProtected = AddStat(stats, "Protegidos", "Parâmetros que seriam elegíveis, mas sincronizam direto");
            statDelay = AddStat(stats, "Atraso estimado", "Tempo até quem vê o avatar receber uma mudança comprimida");
            statBuild = AddStat(stats, "Último build", "Resultado real do solver do VRCFury no último build");
            card.Add(stats);

            banners = new VisualElement();
            card.Add(banners);
            return card;
        }

        private static Label AddStat(VisualElement parent, string title, string tooltip) {
            var stat = new VisualElement { tooltip = tooltip };
            stat.AddToClassList("ce-stat");
            var value = new Label("—");
            value.AddToClassList("ce-stat-value");
            stat.Add(value);
            var label = new Label(title);
            label.AddToClassList("ce-muted");
            stat.Add(label);
            parent.Add(stat);
            return value;
        }

        private VisualElement BuildRules() {
            var foldout = new Foldout { text = "Regras gerais", value = true };
            foldout.AddToClassList("ce-rules");

            var types = RuleRow("Proteger todos os");
            protectBool = RuleToggle(types, "Bool", (s, v) => s.protectBool = v);
            protectInt = RuleToggle(types, "Int", (s, v) => s.protectInt = v);
            protectFloat = RuleToggle(types, "Float", (s, v) => s.protectFloat = v);
            foldout.Add(types);

            var controls = RuleRow("Proteger controles");
            controlToggles = new Dictionary<ControlType, Toggle>();
            foreach (var (type, label) in ControlRules) {
                controlToggles[type] = RuleToggle(controls, label, (s, v) => {
                    s.protectedControls.Remove(type);
                    if (v) s.protectedControls.Add(type);
                });
            }
            foldout.Add(controls);

            var patterns = RuleRow("Padrões de nome");
            var hint = new Label("Use * como curinga, ex.: Fur*Off ou Roupas/*");
            hint.AddToClassList("ce-muted");
            patterns.Add(hint);
            var spacer = new VisualElement();
            spacer.AddToClassList("ce-spacer");
            patterns.Add(spacer);
            patterns.Add(new Button(() => Settings.Mutate("Add Name Pattern", s => s.namePatterns.Add(""))) {
                text = "Adicionar padrão"
            });
            foldout.Add(patterns);

            patternList = new VisualElement();
            foldout.Add(patternList);
            return foldout;
        }

        private static VisualElement RuleRow(string title) {
            var row = new VisualElement();
            row.AddToClassList("ce-rule-row");
            var label = new Label(title);
            label.AddToClassList("ce-rule-label");
            row.Add(label);
            return row;
        }

        private static Toggle RuleToggle(VisualElement row, string label, Action<CompressorExclusionSettings, bool> apply) {
            var toggle = new Toggle { text = label };
            toggle.RegisterValueChangedCallback(evt =>
                Settings.Mutate("Change Compression Rule", s => apply(s, evt.newValue)));
            row.Add(toggle);
            return toggle;
        }

        private PatternView BuildPatternRow(int index) {
            var view = new PatternView { root = new VisualElement(), field = new TextField(), count = new Label() };
            view.root.AddToClassList("ce-pattern");

            void Commit() {
                var value = view.field.value;
                if (index >= Settings.namePatterns.Count || Settings.namePatterns[index] == value) return;
                Settings.Mutate("Change Name Pattern", s => s.namePatterns[index] = value);
            }

            view.field.RegisterCallback<FocusOutEvent>(_ => Commit());
            view.field.RegisterCallback<KeyDownEvent>(evt => {
                if (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter) Commit();
            }, TrickleDown.TrickleDown);
            view.root.Add(view.field);

            view.count.AddToClassList("ce-pattern-count");
            view.root.Add(view.count);

            view.root.Add(new Button(() => Settings.Mutate("Remove Name Pattern", s => s.namePatterns.RemoveAt(index))) {
                text = "Remover"
            });
            return view;
        }

        private VisualElement BuildTableToolbar() {
            var toolbar = new VisualElement();
            toolbar.AddToClassList("ce-table-toolbar");

            searchField = new ToolbarSearchField();
            searchField.AddToClassList("ce-search");
            searchField.RegisterValueChangedCallback(_ => ApplyFilter());
            toolbar.Add(searchField);

            var group = new VisualElement();
            group.AddToClassList("ce-seg-group");
            filterButtons = new Dictionary<Filter, Button>();
            foreach (var (value, label) in Filters) {
                var button = new Button(() => {
                    filter = value;
                    table.ClearSelection();
                    ApplyFilter();
                }) { text = label };
                button.AddToClassList("ce-seg");
                filterButtons[value] = button;
                group.Add(button);
            }
            toolbar.Add(group);
            return toolbar;
        }

        private VisualElement BuildBulkBar() {
            bulkBar = new VisualElement();
            bulkBar.AddToClassList("ce-bulk");
            bulkLabel = new Label();
            bulkBar.Add(bulkLabel);
            foreach (var choice in Choices) {
                bulkBar.Add(new Button(() => ApplyToSelection(choice)) { text = CompressorUi.ChoiceLabel(choice) });
            }
            return bulkBar;
        }

        private void ApplyToSelection(ParamChoice choice) {
            var names = table.selectedItems.Cast<Row>()
                .Where(row => row.canCompress)
                .Select(row => row.param.name)
                .ToList();
            if (names.Count > 0) Settings.SetChoice(names, choice);
        }

        private static VisualElement BuildTableHeader() {
            var header = new VisualElement();
            header.AddToClassList("ce-table-header");
            foreach (var (title, style) in new[] {
                ("Parâmetro", "ce-col-name"),
                ("Tipo", "ce-col-type"),
                ("Controle", "ce-col-control"),
                ("Bits", "ce-col-bits"),
                ("Situação", "ce-col-status"),
                ("Escolha", "ce-col-choice"),
            }) {
                var label = new Label(title);
                label.AddToClassList(style);
                header.Add(label);
            }
            return header;
        }

        private VisualElement BuildTable() {
            var container = new VisualElement();
            container.AddToClassList("ce-table");

            emptyLabel = new Label();
            emptyLabel.AddToClassList("ce-empty");
            container.Add(emptyLabel);

            table = new ListView {
                itemsSource = visibleRows,
                fixedItemHeight = 26,
                selectionType = SelectionType.Multiple,
                showAlternatingRowBackgrounds = AlternatingRowBackground.All,
                makeItem = MakeRow,
                bindItem = BindRow
            };
            table.style.flexGrow = 1;
            table.selectionChanged += _ => UpdateBulkBar();
            container.Add(table);
            return container;
        }

        private VisualElement MakeRow() {
            var element = new VisualElement();
            element.AddToClassList("ce-row");

            Label AddLabel(string style) {
                var label = new Label();
                label.AddToClassList(style);
                element.Add(label);
                return label;
            }

            var view = new RowView {
                name = AddLabel("ce-col-name"),
                type = AddLabel("ce-col-type"),
                control = AddLabel("ce-col-control"),
                bits = AddLabel("ce-col-bits"),
                status = AddLabel("ce-col-status"),
                choices = new Dictionary<ParamChoice, Button>()
            };

            var group = new VisualElement();
            group.AddToClassList("ce-seg-group");
            group.AddToClassList("ce-col-choice");
            foreach (var choice in Choices) {
                var button = new Button(() => {
                    if (view.row != null) Settings.SetChoice(new[] { view.row.param.name }, choice);
                }) { text = CompressorUi.ChoiceLabel(choice) };
                button.AddToClassList("ce-seg");
                view.choices[choice] = button;
                group.Add(button);
            }
            element.Add(group);
            element.userData = view;
            return element;
        }

        private void BindRow(VisualElement element, int index) {
            var view = (RowView)element.userData;
            var row = visibleRows[index];
            view.row = row;
            view.name.text = row.displayName;
            view.name.tooltip = row.param.name;
            view.type.text = row.typeLabel;
            view.control.text = row.controlLabel;
            view.bits.text = row.cost.ToString();
            view.status.text = row.reason;
            view.status.tooltip = row.reason;

            view.status.EnableInClassList("ce-status--compressed", row.status == RowStatus.Compressed);
            view.status.EnableInClassList("ce-status--protected", row.status == RowStatus.Protected);
            view.status.EnableInClassList("ce-status--direct", row.status == RowStatus.Direct);
            view.status.EnableInClassList("ce-status--unavailable", row.status == RowStatus.Unavailable);

            foreach (var pair in view.choices) {
                pair.Value.EnableInClassList("ce-seg--on", pair.Key == row.choice);
                pair.Value.SetEnabled(row.canCompress);
            }
        }

        private void Refresh() {
            // Events can arrive before CreateGUI, or after an assembly reload
            if (table == null) return;

            RefreshAvatarList();
            snapshot = selectedAvatar == null ? null : CompressorSnapshotStore.Load(selectedAvatar);

            RefreshRuleControls();
            var estimate = BuildRows();
            RefreshPatterns();
            RefreshStatus();
            RefreshSummary(estimate);
            ApplyFilter();
        }

        private void RefreshAvatarList() {
            var avatars = CompressorSnapshotStore.AvatarNames();
            if (selectedAvatar == null || !avatars.Contains(selectedAvatar)) {
                selectedAvatar = avatars.FirstOrDefault();
            }
            avatarDropdown.choices = avatars;
            avatarDropdown.SetValueWithoutNotify(selectedAvatar ?? "Sem avatar");
            avatarDropdown.SetEnabled(avatars.Count > 1);
        }

        private void RefreshRuleControls() {
            var settings = Settings;
            activeToggle.SetValueWithoutNotify(settings.active);
            protectBool.SetValueWithoutNotify(settings.protectBool);
            protectInt.SetValueWithoutNotify(settings.protectInt);
            protectFloat.SetValueWithoutNotify(settings.protectFloat);
            foreach (var pair in controlToggles) {
                pair.Value.SetValueWithoutNotify(settings.protectedControls.Contains(pair.Key));
            }
        }

        private EstimateResult BuildRows() {
            var settings = Settings;
            var parameters = snapshot?.parameters ?? new List<SnapshotParam>();
            var candidates = new List<EstimatorParam>();
            var verdicts = new Dictionary<SnapshotParam, Verdict>();
            foreach (var param in parameters) {
                var type = (ParamType)param.valueType;
                var verdict = CompressorPolicy.Evaluate(settings, param.name, type, param.control);
                verdicts[param] = verdict;
                if (!verdict.isProtected) {
                    candidates.Add(new EstimatorParam {
                        name = param.name,
                        isBool = type == ParamType.Bool,
                        control = param.control,
                        addDriven = param.addDriven
                    });
                }
            }

            var estimate = snapshot != null && snapshot.needsCompression
                ? CompressorEstimator.Solve(candidates, snapshot.originalCost, snapshot.maxCost)
                : new EstimateResult();

            allRows = parameters.Select(param => MakeRowModel(param, verdicts[param], estimate))
                .OrderBy(row => row.displayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return estimate;
        }

        private static Row MakeRowModel(SnapshotParam param, Verdict verdict, EstimateResult estimate) {
            var type = (ParamType)param.valueType;
            var canCompress = CompressibleControls.Contains((ControlType)param.control);
            var row = new Row {
                param = param,
                displayName = CompressorPolicy.Normalize(param.name),
                typeLabel = type.ToString(),
                controlLabel = CompressorPolicy.ControlLabel(param.control),
                cost = CompressorPolicy.CostOf(type),
                canCompress = canCompress,
                choice = verdict.choice
            };

            if (!canCompress) {
                row.status = RowStatus.Unavailable;
                row.reason = param.control == CompressorPolicy.NoControl
                    ? "Fora do menu: o VRCFury não comprime"
                    : "O VRCFury não comprime este controle";
            } else if (verdict.isProtected) {
                row.status = RowStatus.Protected;
                row.reason = verdict.reason;
            } else if (estimate.compressed.Contains(param.name)) {
                row.status = RowStatus.Compressed;
                row.reason = verdict.choice == ParamChoice.Compress ? verdict.reason : "Comprimido";
            } else {
                row.status = RowStatus.Direct;
                row.reason = "Sincroniza direto: não precisou comprimir";
            }
            return row;
        }

        private void RefreshPatterns() {
            var patterns = Settings.namePatterns;
            if (patternViews.Count != patterns.Count) {
                patternViews.Clear();
                patternList.Clear();
                for (var i = 0; i < patterns.Count; i++) {
                    var view = BuildPatternRow(i);
                    patternViews.Add(view);
                    patternList.Add(view.root);
                }
            }

            for (var i = 0; i < patterns.Count; i++) {
                var view = patternViews[i];
                var pattern = patterns[i];
                // Don't replace what the user is typing
                if (view.field.panel?.focusController?.focusedElement != view.field) {
                    view.field.SetValueWithoutNotify(pattern);
                }

                var matches = allRows.Count(row => CompressorPolicy.MatchesPattern(pattern, row.param.name));
                var isEmpty = !string.IsNullOrWhiteSpace(pattern) && snapshot != null && matches == 0;
                view.count.text = string.IsNullOrWhiteSpace(pattern) ? "" : isEmpty ? "Nenhum resultado" : $"{matches} parâmetros";
                view.count.EnableInClassList("ce-pattern-count--empty", isEmpty);
            }
        }

        private void RefreshStatus() {
            var solverError = CompressorExclusionPatch.SolverError;
            var reportingError = CompressorExclusionPatch.ReportingError;
            var style = solverError != null ? "ce-pill--error" : reportingError != null ? "ce-pill--warn" : "ce-pill--ok";
            statusPill.text = solverError != null ? "Patch com erro" : reportingError != null ? "Patch ativo, com avisos" : "Patch ativo";
            statusPill.tooltip = solverError ?? reportingError ?? $"VRCFury {CompressorExclusionPatch.VrcfuryVersion}";
            statusPill.RemoveFromClassList("ce-pill--ok");
            statusPill.RemoveFromClassList("ce-pill--warn");
            statusPill.RemoveFromClassList("ce-pill--error");
            statusPill.AddToClassList(style);
        }

        private void RefreshSummary(EstimateResult estimate) {
            banners.Clear();

            var error = CompressorExclusionPatch.SolverError;
            if (error != null) AddBanner("error", error);
            else if (CompressorExclusionPatch.ReportingError != null) AddBanner("warn", CompressorExclusionPatch.ReportingError);

            if (snapshot == null) {
                ShowBar(0, CompressorExclusionPatch.MaxCost(), "");
                barText.text = "Sem dados de build";
                ClearStats();
                AddBanner("info", "Faça um Build & Test do avatar para carregar os parâmetros. As regras acima já podem ser editadas.");
                return;
            }

            if (!snapshot.needsCompression) {
                ShowBar(0, snapshot.maxCost, "");
                barText.text = "Este avatar cabe no limite";
                ClearStats();
                AddBanner("info", "No último build o avatar coube sem compressão, então nada foi comprimido.");
                return;
            }

            var hint = estimate.fits
                ? $"estimativa, antes: {snapshot.originalCost} bits sem compressão"
                : $"melhor resultado possível com a escolha atual ({snapshot.originalCost} bits sem compressão)";
            ShowBar(estimate.finalCost, snapshot.maxCost, hint);
            barFill.EnableInClassList("ce-bar-fill--over", !estimate.fits);

            statCompressed.text = allRows.Count(row => row.status == RowStatus.Compressed).ToString();
            statProtected.text = allRows.Count(row => row.status == RowStatus.Protected).ToString();
            statDelay.text = estimate.fits && estimate.batches > 0
                ? $"{estimate.MinDelay:0.00}–{estimate.MaxDelay:0.00}s"
                : "—";
            statBuild.text = snapshot.hasResult
                ? $"{snapshot.resultCompressed} em {snapshot.resultBatches} lotes"
                : "Sem resultado";
            statBuild.parent.tooltip = $"{snapshot.builtAt}, VRCFury {snapshot.vrcfuryVersion}"
                                       + (snapshot.hasResult ? $", {snapshot.resultCost}/{snapshot.maxCost} bits" : "");

            if (!estimate.fits) {
                AddBanner("error", $"A escolha atual não cabe: precisa de {estimate.finalCost}/{snapshot.maxCost} bits e o VRCFury vai recusar o upload."
                                   + " Libere parâmetros para compressão, de preferência números, que economizam 8 bits cada.");
            }

            var settingsChanged = snapshot.settingsFingerprint != Settings.Fingerprint();
            if (settingsChanged) {
                AddBanner("info", "A configuração mudou desde o último build. Ao enviar, envie PC e depois Quest, para os dois sincronizarem entre si.");
            } else if (snapshot.hasResult
                       && (estimate.compressed.Count != snapshot.resultCompressed || estimate.batches != snapshot.resultBatches)) {
                AddBanner("warn", "A estimativa difere do último build real. Vale o resultado real: o VRCFury pode ter mudado o solver.");
            }
        }

        private void ShowBar(int cost, int maxCost, string hint) {
            barText.text = $"{cost} / {maxCost} bits";
            barHint.text = hint;
            barFill.style.width = Length.Percent(maxCost <= 0 ? 0 : Mathf.Clamp01((float)cost / maxCost) * 100);
            barFill.RemoveFromClassList("ce-bar-fill--over");
        }

        private void ClearStats() {
            statCompressed.text = "—";
            statProtected.text = "—";
            statDelay.text = "—";
            statBuild.text = "—";
        }

        private void AddBanner(string kind, string text) {
            var banner = new Label(text);
            banner.AddToClassList("ce-banner");
            banner.AddToClassList("ce-banner--" + kind);
            banners.Add(banner);
        }

        private void ApplyFilter() {
            var search = searchField.value ?? "";
            visibleRows.Clear();
            visibleRows.AddRange(allRows.Where(row => MatchesFilter(row) && MatchesSearch(row, search)));
            table.RefreshItems();

            foreach (var pair in filterButtons) {
                pair.Value.EnableInClassList("ce-seg--on", pair.Key == filter);
            }

            var hasRows = visibleRows.Count > 0;
            table.style.display = hasRows ? DisplayStyle.Flex : DisplayStyle.None;
            emptyLabel.style.display = hasRows ? DisplayStyle.None : DisplayStyle.Flex;
            emptyLabel.text = allRows.Count == 0
                ? "Os parâmetros aparecem aqui depois do primeiro Build & Test."
                : "Nenhum parâmetro nesta busca ou filtro.";
            UpdateBulkBar();
        }

        private bool MatchesFilter(Row row) {
            switch (filter) {
                case Filter.Compressed: return row.status == RowStatus.Compressed;
                case Filter.Protected: return row.status == RowStatus.Protected;
                case Filter.Unavailable: return row.status == RowStatus.Unavailable || row.status == RowStatus.Direct;
                default: return true;
            }
        }

        private static bool MatchesSearch(Row row, string search) {
            return row.displayName.IndexOf(search.Trim(), StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void UpdateBulkBar() {
            var count = table.selectedIndices.Count();
            bulkBar.style.display = count > 1 ? DisplayStyle.Flex : DisplayStyle.None;
            bulkLabel.text = $"{count} selecionados:";
        }
    }
}

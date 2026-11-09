using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using static DoskerTools.EditorUi.DoskerLocale;
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
            public string details;
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

        // Lists with text are properties so they follow the language picked in DoskerLocale
        private static (ControlType type, string label)[] ControlRules => new[] {
            (ControlType.Toggle, "Toggle"),
            (ControlType.RadialPuppet, "Radial"),
            (ControlType.TwoAxisPuppet, Tr("Puppet 2 eixos", "Two Axis Puppet")),
            (ControlType.FourAxisPuppet, Tr("Puppet 4 eixos", "Four Axis Puppet")),
        };

        private const int ForecastColumns = 3;
        private const int MaxNamesInTooltip = 30;
        private const int HistoryRows = 10;
        private const float TightRatio = 0.9f;
        private static string SceneSuffix => Tr(" (cena)", " (scene)");

        private const int ForecastRowCount = 6;

        private static string[] ForecastRows => new[] {
            Tr("Comprimidos", "Compressed"), Tr("Protegidos", "Protected"), "Bits", Tr("Lotes", "Batches"), Tr("Atraso", "Delay"), Tr("Cabe", "Fits")
        };

        private static readonly ParamChoice[] Choices = { ParamChoice.Auto, ParamChoice.Protect, ParamChoice.Compress };

        private static (Filter filter, string label)[] Filters => new[] {
            (Filter.All, Tr("Todos", "All")),
            (Filter.Compressed, Tr("Comprimidos", "Compressed")),
            (Filter.Protected, Tr("Protegidos", "Protected")),
            (Filter.Unavailable, Tr("Sem compressão", "Not compressed")),
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
        private Label[,] forecastCells;
        private Label forecastStatus;
        private Label forecastDiff;
        private Button orphansButton;
        private VisualElement historyList;

        private Snapshot snapshot;
        private string selectedAvatar;
        // The scene's avatar when it has no build yet, so the window isn't empty before the first one
        private string sceneAvatarKey;
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
            DoskerTools.EditorUi.DoskerLocale.Changed += RebuildGUI;
        }

        private void OnDisable() {
            CompressorExclusionSettings.Changed -= Refresh;
            CompressorSnapshotStore.Updated -= OnSnapshotUpdated;
            DoskerTools.EditorUi.DoskerLocale.Changed -= RebuildGUI;
        }

        // The UI Toolkit labels keep their text, so a language change rebuilds the window
        private void RebuildGUI() {
            if (table == null) return;
            var search = searchField.value;
            rootVisualElement.Clear();
            patternViews.Clear();
            CreateGUI();
            searchField.SetValueWithoutNotify(search);
            ApplyFilter();
        }

        private bool ShowsSceneAvatar => selectedAvatar != null && selectedAvatar == sceneAvatarKey;

        private void OnSelectionChange() {
            Refresh();
        }

        private void OnHierarchyChange() {
            Refresh();
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
            root.Add(BuildForecast());
            root.Add(BuildRules());
            root.Add(BuildHistory());
            root.Add(BuildTableToolbar());
            root.Add(BuildBulkBar());
            root.Add(BuildTableHeader());
            root.Add(BuildTable());

            Refresh();
        }

        private VisualElement BuildHeader() {
            var header = new VisualElement();
            header.AddToClassList("ce-header");

            var title = new Label(Tr("Compressão de parâmetros", "Parameter compression"));
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

            avatarDropdown = new DropdownField { tooltip = Tr("Avatar usado para mostrar os parâmetros. As regras valem para o projeto todo.", "Avatar used to show the parameters. The rules apply to the whole project.") };
            avatarDropdown.style.minWidth = 140;
            avatarDropdown.RegisterValueChangedCallback(evt => {
                selectedAvatar = evt.newValue;
                Refresh();
            });
            header.Add(avatarDropdown);

            activeToggle = new Toggle(Tr("Ativo", "Active")) {
                tooltip = Tr("Desligado, o VRCFury comprime como faria sem esta ferramenta.", "When off, VRCFury compresses as it would without this tool.")
            };
            activeToggle.labelElement.style.minWidth = 36;
            activeToggle.style.marginLeft = 10;
            activeToggle.RegisterValueChangedCallback(evt =>
                Settings.Mutate("Toggle Compressor Exclusions", s => s.active = evt.newValue));
            header.Add(activeToggle);

            Button language = null;
            // A built-in Editor icon, because the UI Toolkit font has no globe character
            language = new Button(() => ShowMenu(language.worldBound)) {
                tooltip = "Language / Idioma"
            };
            language.Add(new Label(ShortCode) { pickingMode = PickingMode.Ignore });
            language.style.marginLeft = 6;
            language.style.flexDirection = FlexDirection.Row;
            language.style.alignItems = Align.Center;
            var globe = DoskerTools.EditorUi.DoskerEditorUi.Icon(GlobeIcon);
            if (globe != null) {
                var image = new Image { image = globe, pickingMode = PickingMode.Ignore };
                image.style.width = 16;
                image.style.height = 16;
                image.style.marginRight = 3;
                language.Insert(0, image);
            }
            header.Add(language);
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
            statCompressed = AddStat(stats, Tr("Comprimidos", "Compressed"), Tr("Parâmetros que sincronizam em lotes, com atraso", "Parameters that sync in batches, with a delay"));
            statProtected = AddStat(stats, Tr("Protegidos", "Protected"), Tr("Parâmetros que seriam elegíveis, mas sincronizam direto", "Parameters that would be eligible, but sync directly"));
            statDelay = AddStat(stats, Tr("Atraso estimado", "Estimated delay"), Tr("Tempo até quem vê o avatar receber uma mudança comprimida", "Time until people watching the avatar receive a compressed change"));
            statBuild = AddStat(stats, Tr("Último build", "Last build"), Tr("Resultado real do solver do VRCFury no último build", "Actual result of the VRCFury solver in the last build"));
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

        private VisualElement BuildForecast() {
            var foldout = new Foldout { text = Tr("Previsão do build", "Build forecast"), value = true };
            foldout.AddToClassList("ce-rules");

            var header = new VisualElement();
            header.AddToClassList("ce-forecast-row");
            foreach (var (title, tip) in new[] {
                ("", ""),
                (Tr("Próximo build", "Next build"), Tr("O que a configuração atual deve produzir", "What the current settings should produce")),
                (Tr("Previsto", "Predicted"), Tr("O que se esperava do último build, antes de ele rodar", "What was expected from the last build, before it ran")),
                (Tr("Real", "Actual"), Tr("O que o VRCFury fez no último build", "What VRCFury did in the last build")),
            }) {
                var label = new Label(title) { tooltip = tip };
                label.AddToClassList("ce-forecast-cell");
                label.AddToClassList("ce-forecast-head");
                header.Add(label);
            }
            foldout.Add(header);

            var rowTitles = ForecastRows;
            forecastCells = new Label[ForecastRowCount, ForecastColumns];
            for (var row = 0; row < ForecastRowCount; row++) {
                var line = new VisualElement();
                line.AddToClassList("ce-forecast-row");
                var title = new Label(rowTitles[row]);
                title.AddToClassList("ce-forecast-cell");
                title.AddToClassList("ce-muted");
                line.Add(title);
                for (var column = 0; column < ForecastColumns; column++) {
                    forecastCells[row, column] = new Label("—");
                    forecastCells[row, column].AddToClassList("ce-forecast-cell");
                    line.Add(forecastCells[row, column]);
                }
                foldout.Add(line);
            }

            forecastStatus = new Label();
            forecastStatus.AddToClassList("ce-forecast-status");
            foldout.Add(forecastStatus);
            forecastDiff = new Label();
            forecastDiff.AddToClassList("ce-muted");
            forecastDiff.style.whiteSpace = WhiteSpace.Normal;
            foldout.Add(forecastDiff);
            return foldout;
        }

        private VisualElement BuildHistory() {
            var foldout = new Foldout { text = Tr("Histórico de builds", "Build history"), value = false };
            foldout.AddToClassList("ce-rules");
            historyList = new VisualElement();
            foldout.Add(historyList);
            return foldout;
        }

        private VisualElement BuildRules() {
            var foldout = new Foldout { text = Tr("Regras gerais", "General rules"), value = true };
            foldout.AddToClassList("ce-rules");

            var types = RuleRow(Tr("Proteger todos os", "Protect every"));
            protectBool = RuleToggle(types, "Bool", (s, v) => s.protectBool = v);
            protectInt = RuleToggle(types, "Int", (s, v) => s.protectInt = v);
            protectFloat = RuleToggle(types, "Float", (s, v) => s.protectFloat = v);
            foldout.Add(types);

            var controls = RuleRow(Tr("Proteger controles", "Protect controls"));
            controlToggles = new Dictionary<ControlType, Toggle>();
            foreach (var (type, label) in ControlRules) {
                controlToggles[type] = RuleToggle(controls, label, (s, v) => {
                    s.protectedControls.Remove(type);
                    if (v) s.protectedControls.Add(type);
                });
            }
            foldout.Add(controls);

            var patterns = RuleRow(Tr("Padrões de nome", "Name patterns"));
            var hint = new Label(Tr("* e ? são curingas. !Fur* libera das regras e menu:Roupas/* usa o caminho no menu", "* and ? are wildcards. !Fur* releases from the rules and menu:Clothes/* uses the menu path"));
            hint.AddToClassList("ce-muted");
            hint.tooltip = Tr("Exemplos: Fur*Off protege por nome. !Fur*Off libera esses nomes de todas as regras gerais. "
                              + "menu:Roupas/* protege o que está no submenu Roupas.",
                              "Examples: Fur*Off protects by name. !Fur*Off releases those names from every general rule. "
                              + "menu:Clothes/* protects what is in the Clothes submenu.");
            patterns.Add(hint);
            var spacer = new VisualElement();
            spacer.AddToClassList("ce-spacer");
            patterns.Add(spacer);
            patterns.Add(new Button(() => Settings.Mutate("Add Name Pattern", s => s.namePatterns.Add(""))) {
                text = Tr("Adicionar padrão", "Add pattern")
            });
            foldout.Add(patterns);

            patternList = new VisualElement();
            foldout.Add(patternList);

            orphansButton = new Button(RemoveOrphans);
            orphansButton.style.alignSelf = Align.FlexStart;
            foldout.Add(orphansButton);
            return foldout;
        }

        private List<string> OrphanChoices() {
            var known = CompressorSnapshotStore.KnownNames();
            return known.Count == 0
                ? new List<string>()
                : Settings.overrides.Select(entry => entry.name).Where(name => !known.Contains(name)).ToList();
        }

        private void RemoveOrphans() {
            var orphans = OrphanChoices();
            var message = Tr("Estas escolhas não aparecem no último build de nenhum avatar. Se existe um avatar que ainda "
                             + "não passou por um build, as escolhas dele também somem.",
                             "These choices don't appear in the last build of any avatar. If an avatar hasn't been "
                             + "built yet, its choices are removed too.")
                          + "\n\n" + string.Join(", ", orphans.Take(MaxNamesInTooltip));
            if (EditorUtility.DisplayDialog(Tr("Limpar escolhas", "Clear choices"), message, Tr("Limpar", "Clear"), Tr("Cancelar", "Cancel"))) {
                Settings.SetChoice(orphans, ParamChoice.Auto);
            }
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
                text = Tr("Remover", "Remove")
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
                (Tr("Parâmetro", "Parameter"), "ce-col-name"),
                (Tr("Tipo", "Type"), "ce-col-type"),
                (Tr("Controle", "Control"), "ce-col-control"),
                ("Bits", "ce-col-bits"),
                (Tr("Situação", "Status"), "ce-col-status"),
                (Tr("Escolha", "Choice"), "ce-col-choice"),
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
            view.status.tooltip = row.details;

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
            snapshot = LoadSelected();

            RefreshRuleControls();
            var estimate = BuildRows();
            RefreshPatterns();
            RefreshStatus();
            RefreshSummary(estimate);
            RefreshForecast();
            RefreshHistory();
            ApplyFilter();
        }

        private Snapshot LoadSelected() {
            if (selectedAvatar == null) return null;
            return ShowsSceneAvatar
                ? CompressorPolicy.SnapshotFromScene(SceneAvatar(), CompressorExclusionPatch.MaxCost())
                : CompressorSnapshotStore.Load(selectedAvatar);
        }

        private void RefreshAvatarList() {
            var avatars = CompressorSnapshotStore.Keys();
            var scene = SceneAvatar();
            var hasBuild = scene != null && avatars.Any(key => key == scene.name || key.StartsWith(scene.name + " · "));
            sceneAvatarKey = scene != null && !hasBuild ? scene.name + SceneSuffix : null;
            if (sceneAvatarKey != null) avatars.Add(sceneAvatarKey);

            if (selectedAvatar == null || !avatars.Contains(selectedAvatar)) {
                selectedAvatar = avatars.FirstOrDefault();
            }
            avatarDropdown.choices = avatars;
            avatarDropdown.SetValueWithoutNotify(selectedAvatar ?? Tr("Sem avatar", "No avatar"));
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
                var verdict = CompressorPolicy.Evaluate(settings, param.name, (ParamType)param.valueType, param.control, param.menuPath);
                verdicts[param] = verdict;
                if (!verdict.isProtected) candidates.Add(CompressorPolicy.ToEstimator(param));
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
            var canCompress = CompressorPolicy.CanCompress(param);
            var row = new Row {
                param = param,
                displayName = CompressorPolicy.Normalize(param.name),
                typeLabel = type.ToString(),
                controlLabel = CompressorPolicy.ControlLabel(param.control),
                cost = param.cost > 0 ? param.cost : CompressorPolicy.CostOf(type),
                canCompress = canCompress,
                choice = verdict.choice
            };

            if (!canCompress) {
                row.status = RowStatus.Unavailable;
                row.reason = param.nonStandardEncoding
                    ? Tr("Codificação especial: o VRCFury não comprime", "Special encoding: VRCFury doesn't compress it")
                    : param.control == CompressorPolicy.NoControl
                        ? Tr("Fora do menu: o VRCFury não comprime", "Not in the menu: VRCFury doesn't compress it")
                        : Tr("O VRCFury não comprime este controle", "VRCFury doesn't compress this control");
            } else if (verdict.isProtected) {
                row.status = RowStatus.Protected;
                row.reason = verdict.reason;
            } else if (estimate.compressed.Contains(param.name)) {
                row.status = RowStatus.Compressed;
                row.reason = verdict.choice == ParamChoice.Compress ? verdict.reason : Tr("Comprimido", "Compressed");
                row.reason += Tr($" · lote {estimate.batchOf[param.name] + 1}/{estimate.batches}", $" · batch {estimate.batchOf[param.name] + 1}/{estimate.batches}");
            } else {
                row.status = RowStatus.Direct;
                row.reason = Tr("Sincroniza direto: não precisou comprimir", "Syncs directly: no compression needed");
            }
            row.details = verdict.details == verdict.reason ? row.reason : row.reason + "\n" + verdict.details;
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

                var matches = allRows
                    .Where(row => CompressorPolicy.MatchesPattern(pattern, row.param.name, row.param.menuPath))
                    .Select(row => row.displayName)
                    .ToList();
                var isEmpty = !string.IsNullOrWhiteSpace(pattern) && snapshot != null && matches.Count == 0;
                view.count.text = string.IsNullOrWhiteSpace(pattern) ? "" : isEmpty ? Tr("Nenhum resultado", "No matches") : Tr($"{matches.Count} parâmetros", $"{matches.Count} parameters");
                view.count.tooltip = string.Join("\n", matches.Take(MaxNamesInTooltip));
                view.count.EnableInClassList("ce-pattern-count--empty", isEmpty);
            }

            var orphans = OrphanChoices().Count;
            orphansButton.text = Tr($"Limpar {orphans} escolhas sem parâmetro", $"Clear {orphans} choices without a parameter");
            orphansButton.style.display = orphans > 0 ? DisplayStyle.Flex : DisplayStyle.None;
        }

        private void RefreshStatus() {
            var solverError = CompressorExclusionPatch.SolverError;
            var reportingError = CompressorExclusionPatch.ReportingError;
            var style = solverError != null ? "ce-pill--error" : reportingError != null ? "ce-pill--warn" : "ce-pill--ok";
            statusPill.text = solverError != null ? Tr("Patch com erro", "Patch failed") : reportingError != null ? Tr("Patch ativo, com avisos", "Patch active, with warnings") : Tr("Patch ativo", "Patch active");
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
            if (CompressorExclusionPatch.IsNewerThanTested) {
                AddBanner("info", Tr($"O VRCFury {CompressorExclusionPatch.VrcfuryVersion} é mais novo que o testado. Confira o último build abaixo.", $"VRCFury {CompressorExclusionPatch.VrcfuryVersion} is newer than the tested version. Check the last build below."));
            }

            if (snapshot == null) {
                ShowBar(0, CompressorExclusionPatch.MaxCost(), "");
                barText.text = Tr("Sem dados de build", "No build data");
                ClearStats();
                AddBanner("info", Tr("Faça um Build & Test do avatar para carregar os parâmetros. As regras acima já podem ser editadas.", "Run a Build & Test of the avatar to load its parameters. The rules above can already be edited."));
                return;
            }

            AddPlatformBanner();
            AddCollisionBanner();
            if (ShowsSceneAvatar) {
                AddBanner("info", Tr("Este avatar ainda não passou por um build. A lista só tem o que já está no avatar: Toggles do VRCFury e prefabs entram no primeiro Build & Test.", "This avatar hasn't been built yet. The list only has what is already on the avatar: VRCFury Toggles and prefabs come in with the first Build & Test."));
            }

            if (!snapshot.needsCompression) {
                var free = snapshot.maxCost - snapshot.originalCost;
                var used = snapshot.maxCost <= 0 ? 0 : Mathf.RoundToInt(100f * snapshot.originalCost / snapshot.maxCost);
                ShowBar(snapshot.originalCost, snapshot.maxCost, Tr($"{used}% usado · cabe sem compressão, sobram {free} bits", $"{used}% used · fits without compression, {free} bits left"));
                ClearStats();
                if (!ShowsSceneAvatar) {
                    AddBanner("info", Tr("No último build o avatar coube sem compressão, então nada foi comprimido.", "In the last build the avatar fit without compression, so nothing was compressed."));
                }
                return;
            }

            var hint = estimate.fits
                ? Tr($"estimativa, antes: {snapshot.originalCost} bits sem compressão", $"estimate, before: {snapshot.originalCost} bits without compression")
                : Tr($"melhor resultado possível com a escolha atual ({snapshot.originalCost} bits sem compressão)", $"best possible result with the current choice ({snapshot.originalCost} bits without compression)");
            ShowBar(estimate.finalCost, snapshot.maxCost, hint);
            barFill.EnableInClassList("ce-bar-fill--over", !estimate.fits);
            // The solver fills the limit on purpose, so a full bar here is not a warning
            barFill.RemoveFromClassList("ce-bar-fill--tight");

            statCompressed.text = allRows.Count(row => row.status == RowStatus.Compressed).ToString();
            statProtected.text = allRows.Count(row => row.status == RowStatus.Protected).ToString();
            statDelay.text = estimate.fits && estimate.batches > 0
                ? $"{estimate.MinDelay:0.00}–{estimate.MaxDelay:0.00}s"
                : "—";
            statBuild.text = snapshot.hasResult
                ? Tr($"{snapshot.resultCompressed} em {snapshot.resultBatches} lotes", $"{snapshot.resultCompressed} in {snapshot.resultBatches} batches")
                : Tr("Sem resultado", "No result");
            statBuild.parent.tooltip = $"{snapshot.builtAt}, VRCFury {snapshot.vrcfuryVersion}"
                                       + (snapshot.hasResult ? $", {snapshot.resultCost}/{snapshot.maxCost} bits" : "");

            if (!estimate.fits) {
                AddBanner("error", Tr($"A escolha atual não cabe: precisa de {estimate.finalCost}/{snapshot.maxCost} bits e o VRCFury vai recusar o upload."
                                      + " Libere parâmetros para compressão, de preferência números, que economizam 8 bits cada.",
                                      $"The current choice doesn't fit: it needs {estimate.finalCost}/{snapshot.maxCost} bits and VRCFury will refuse the upload."
                                      + " Release parameters for compression, preferably numbers, which save 8 bits each."));
                AddFitButton();
            }

            if (!ShowsSceneAvatar && snapshot.settingsFingerprint != Settings.Fingerprint()) {
                AddBanner("info", Tr("A configuração mudou desde o último build. Ao enviar, envie PC e depois Quest, para os dois sincronizarem entre si.", "The settings changed since the last build. When uploading, upload PC and then Quest, so both sync with each other."));
            }
        }

        private void AddFitButton() {
            CompressorPolicy.Split(Settings, snapshot.parameters, out var candidates, out var protectedParams);
            var release = CompressorEstimator.SuggestRelease(candidates, protectedParams, snapshot.originalCost, snapshot.maxCost);
            if (release == null) {
                AddBanner("error", Tr("Mesmo liberando tudo, o avatar não cabe. Remova parâmetros.", "Even releasing everything, the avatar doesn't fit. Remove parameters."));
                return;
            }

            banners.Add(new Button(() => {
                var names = release.Select(CompressorPolicy.Normalize);
                var message = Tr("Marcar como Comprimir:", "Mark as Compress:") + "\n\n" + string.Join(", ", names.Take(MaxNamesInTooltip));
                if (EditorUtility.DisplayDialog(Tr("Fazer caber", "Make it fit"), message, Tr("Marcar", "Mark"), Tr("Cancelar", "Cancel"))) {
                    Settings.SetChoice(release, ParamChoice.Compress);
                }
            }) { text = Tr($"Fazer caber: liberar {release.Count} parâmetros", $"Make it fit: release {release.Count} parameters") });
        }

        private void AddPlatformBanner() {
            var other = CompressorSnapshotStore.Counterpart(snapshot);
            if (other == null) return;

            var compressed = new HashSet<string>(CompressedNames(snapshot));
            if (other.settingsFingerprint != snapshot.settingsFingerprint) {
                AddBanner("warn", Tr($"Os builds de {snapshot.platform} e {other.platform} usaram configurações diferentes. Envie PC e depois Quest, ou eles não sincronizam.", $"The {snapshot.platform} and {other.platform} builds used different settings. Upload PC and then Quest, or they won't sync."));
            } else if (snapshot.hasResult && other.hasResult && !compressed.SetEquals(CompressedNames(other))) {
                AddBanner("warn", Tr($"{snapshot.platform} e {other.platform} comprimiram parâmetros diferentes. Confira se o avatar é igual nas duas versões.", $"{snapshot.platform} and {other.platform} compressed different parameters. Check that the avatar is the same in both versions."));
            }
        }

        private void AddCollisionBanner() {
            var repeated = allRows.GroupBy(row => row.displayName).Where(group => group.Count() > 1).Select(group => group.Key).ToList();
            if (repeated.Count > 0) {
                AddBanner("info", Tr("Sem o prefixo do VRCFury, estes nomes se repetem e dividem a mesma escolha: ", "Without the VRCFury prefix, these names repeat and share the same choice: ") + string.Join(", ", repeated.Take(5)) + ".");
            }
        }

        private static IEnumerable<string> CompressedNames(Snapshot source) {
            return source.parameters.Where(param => param.compressed).Select(param => CompressorPolicy.Normalize(param.name));
        }

        private void RefreshForecast() {
            var next = snapshot != null
                ? CompressorPolicy.Predict(Settings, snapshot.parameters, snapshot.originalCost, snapshot.maxCost, ShowsSceneAvatar)
                : new Prediction();
            FillColumn(0, next);

            var predicted = snapshot == null
                ? new Prediction()
                : snapshot.predictionBefore.available ? snapshot.predictionBefore : snapshot.predictionAtBuild;
            FillColumn(1, predicted);
            FillRealColumn();

            forecastDiff.text = "";
            forecastDiff.style.display = DisplayStyle.None;
            forecastStatus.text = next.partial
                ? Tr("Previsão parcial: faltam os parâmetros que o VRCFury só cria no build.", "Partial forecast: the parameters VRCFury only creates during the build are missing.")
                : ForecastText(next);
        }

        private string ForecastText(Prediction next) {
            if (snapshot == null) return Tr("Sem avatar com parâmetros na cena para prever.", "No avatar with parameters in the scene to forecast.");
            if (!snapshot.hasResult) return Tr("O último build não chegou ao resultado do VRCFury.", "The last build didn't reach the VRCFury result.");

            var report = CompressorEstimator.Compare(
                snapshot.predictionBefore,
                snapshot.predictionAtBuild,
                CompressedNames(snapshot),
                snapshot.resultBatches,
                snapshot.resultCost,
                snapshot.resultCost <= snapshot.maxCost
            );

            var differences = new List<string>();
            if (report.unexpected.Count > 0) differences.Add(Tr("Comprimiu sem previsão: ", "Compressed without being predicted: ") + string.Join(", ", report.unexpected.Take(MaxNamesInTooltip)) + ".");
            if (report.notCompressed.Count > 0) differences.Add(Tr("Previsto, mas não comprimiu: ", "Predicted, but not compressed: ") + string.Join(", ", report.notCompressed.Take(MaxNamesInTooltip)) + ".");
            forecastDiff.text = string.Join("\n", differences);
            forecastDiff.style.display = differences.Count > 0 ? DisplayStyle.Flex : DisplayStyle.None;

            switch (report.outcome) {
                case ForecastOutcome.Match: return Tr("A previsão acertou o último build.", "The forecast matched the last build.");
                case ForecastOutcome.ParametersChanged:
                    return Tr("Os parâmetros do avatar mudaram desde o build anterior. Com a lista nova, a previsão acertou.", "The avatar parameters changed since the previous build. With the new list, the forecast matched.");
                case ForecastOutcome.SolverDiverged:
                    return Tr("O resultado real difere da previsão. Vale o real: o VRCFury pode ter mudado o solver.", "The actual result differs from the forecast. The actual one is what counts: VRCFury may have changed its solver.");
                default: return Tr("Este build não tem previsão salva. O próximo terá.", "This build has no saved forecast. The next one will.");
            }
        }

        private static VRC.SDK3.Avatars.Components.VRCAvatarDescriptor SceneAvatar() {
            var selected = Selection.activeGameObject;
            var avatar = selected != null ? selected.GetComponentInParent<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>() : null;
            return avatar != null ? avatar : FindObjectOfType<VRC.SDK3.Avatars.Components.VRCAvatarDescriptor>();
        }

        private void FillColumn(int column, Prediction prediction) {
            if (!prediction.available) {
                for (var row = 0; row < ForecastRowCount; row++) forecastCells[row, column].text = "—";
                return;
            }

            var delay = prediction.batches > 0 ? $"{prediction.minDelay:0.00}–{prediction.maxDelay:0.00}s" : Tr("Nenhum", "None");
            var values = new[] {
                prediction.compressed.ToString(),
                prediction.protectedCount.ToString(),
                prediction.cost.ToString(),
                prediction.batches.ToString(),
                delay,
                prediction.fits ? Tr("Sim", "Yes") : Tr("Não", "No")
            };
            for (var row = 0; row < values.Length; row++) forecastCells[row, column].text = values[row];
        }

        private void FillRealColumn() {
            if (snapshot == null || !snapshot.hasResult) {
                for (var row = 0; row < ForecastRowCount; row++) forecastCells[row, 2].text = "—";
                return;
            }

            var timing = new EstimateResult { batches = snapshot.resultBatches };
            var values = new[] {
                snapshot.resultCompressed.ToString(),
                "—",
                snapshot.resultCost.ToString(),
                snapshot.resultBatches.ToString(),
                snapshot.resultBatches > 0 ? $"{timing.MinDelay:0.00}–{timing.MaxDelay:0.00}s" : Tr("Nenhum", "None"),
                snapshot.resultCost <= snapshot.maxCost ? Tr("Sim", "Yes") : Tr("Não", "No")
            };
            for (var row = 0; row < values.Length; row++) forecastCells[row, 2].text = values[row];
        }

        private void RefreshHistory() {
            historyList.Clear();
            var history = snapshot == null ? new List<Snapshot>() : CompressorSnapshotStore.History(snapshot.Key);
            if (history.Count == 0) {
                var empty = new Label(Tr("Nenhum build registrado.", "No builds recorded."));
                empty.AddToClassList("ce-muted");
                historyList.Add(empty);
                return;
            }

            for (var index = 0; index < history.Count && index < HistoryRows; index++) {
                var entry = history[index];
                var text = Tr($"{entry.builtAt}: {entry.resultCost}/{entry.maxCost} bits, {entry.resultCompressed} em {entry.resultBatches} lotes",
                              $"{entry.builtAt}: {entry.resultCost}/{entry.maxCost} bits, {entry.resultCompressed} in {entry.resultBatches} batches");
                if (index + 1 < history.Count) {
                    var older = new HashSet<string>(CompressedNames(history[index + 1]));
                    var newer = new HashSet<string>(CompressedNames(entry));
                    var added = newer.Except(older).ToList();
                    var removed = older.Except(newer).ToList();
                    if (added.Count + removed.Count > 0) text += $" (+{added.Count} −{removed.Count})";
                }
                historyList.Add(new Label(text));
            }
        }

        private void ShowBar(int cost, int maxCost, string hint) {
            barText.text = $"{cost} / {maxCost} bits";
            barHint.text = hint;
            var ratio = maxCost <= 0 ? 0 : (float)cost / maxCost;
            barFill.style.width = Length.Percent(Mathf.Clamp01(ratio) * 100);
            barFill.RemoveFromClassList("ce-bar-fill--over");
            // Little room left: one more number parameter or a few bools can push it over
            barFill.EnableInClassList("ce-bar-fill--tight", ratio > TightRatio && ratio <= 1);
            barFill.parent.tooltip = maxCost <= 0 ? "" : Tr($"{cost} de {maxCost} bits sincronizados ({Mathf.RoundToInt(ratio * 100)}%)", $"{cost} of {maxCost} synced bits ({Mathf.RoundToInt(ratio * 100)}%)");
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
                ? Tr("Abra uma cena com o avatar ou faça um Build & Test para ver os parâmetros.", "Open a scene with the avatar or run a Build & Test to see its parameters.")
                : Tr("Nenhum parâmetro nesta busca ou filtro.", "No parameters match this search or filter.");
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
            bulkLabel.text = Tr($"{count} selecionados:", $"{count} selected:");
        }
    }
}

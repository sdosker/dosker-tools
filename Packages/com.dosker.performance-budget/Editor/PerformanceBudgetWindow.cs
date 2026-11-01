using System;
using System.Collections.Generic;
using System.Linq;
using DoskerTools.EditorUi;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDKBase.Validation.Performance;
using VRC.SDKBase.Validation.Performance.Stats;

namespace DoskerTools.PerformanceBudget
{
    // Ranking do avatar antes do upload. O avatar da cena não é o enviado, porque o VRCFury e outros
    // pré-processadores o mudam no build, então cada número diz de onde veio.
    public sealed class PerformanceBudgetWindow : EditorWindow
    {
        private const string DocsUrl = "https://github.com/sdosker/dosker-tools/tree/main/Packages/com.dosker.performance-budget#readme";
        private const int HeaviestCount = 5;

        private static readonly DoskerEditorUi.TableColumn[] Columns =
        {
            new DoskerEditorUi.TableColumn("Categoria", 3f, false),
            new DoskerEditorUi.TableColumn("Cena", 1f, false),
            new DoskerEditorUi.TableColumn("Previsão", 1f, false),
            new DoskerEditorUi.TableColumn("Real", 1f, false),
        };

        private VRCAvatarDescriptor avatar;
        private int platformTab;
        private bool onlyProblems;
        private Vector2 scroll;
        private int sortColumn = -1;
        private bool ascending;

        private BudgetMeasurement scene;
        private BudgetCalibration calibration;
        private BudgetMeasurement predicted;
        private AvatarPerformanceStats sceneStats;
        private AvatarPerformanceStats predictedStats;
        private AvatarPerformanceStats realStats;
        private BudgetConfidence confidence;
        private List<(string name, int triangles)> heaviest = new List<(string name, int triangles)>();

        private bool IsMobile
        {
            get { return platformTab == 1; }
        }

        [MenuItem("Tools/Dosker Tools/Performance Budget")]
        public static void Open()
        {
            var window = GetWindow<PerformanceBudgetWindow>("Performance Budget");
            window.minSize = new Vector2(420f, 400f);
        }

        private void OnEnable()
        {
            wantsMouseMove = true;
            platformTab = BudgetStore.IsMobileBuild ? 1 : 0;
            BudgetStore.Updated += HandleBuildFinished;
        }

        private void OnDisable()
        {
            BudgetStore.Updated -= HandleBuildFinished;
        }

        private void HandleBuildFinished()
        {
            // Chega no meio do build, então espera ele terminar.
            EditorApplication.delayCall += () =>
            {
                if (this != null && avatar != null)
                {
                    Measure();
                    Repaint();
                }
            };
        }

        private void Measure()
        {
            if (avatar == null)
            {
                return;
            }

            scene = BudgetProbe.Measure(avatar.gameObject, IsMobile);
            calibration = BudgetStore.Load(avatar.name, IsMobile);
            predicted = BudgetMath.Predict(scene, calibration);
            confidence = BudgetMath.ConfidenceOf(calibration, BudgetProbe.ComponentsFingerprint(avatar.gameObject));
            sceneStats = BudgetProbe.Rate(scene, IsMobile);
            predictedStats = BudgetProbe.Rate(predicted, IsMobile);
            realStats = calibration != null ? BudgetProbe.Rate(calibration.after, IsMobile) : null;
            heaviest = BudgetProbe.HeaviestMeshes(avatar.gameObject, HeaviestCount);
        }

        private void OnGUI()
        {
            using (DoskerEditorUi.ActionBar())
            {
                using (var check = new EditorGUI.ChangeCheckScope())
                {
                    avatar = (VRCAvatarDescriptor)EditorGUILayout.ObjectField("Avatar", avatar, typeof(VRCAvatarDescriptor), true);
                    platformTab = DoskerEditorUi.Tabs(platformTab, "PC", "Quest");
                    if (check.changed)
                    {
                        Clear();
                    }
                }

                if (avatar == null)
                {
                    avatar = FindSceneAvatar();
                }

                using (new EditorGUI.DisabledScope(avatar == null))
                {
                    if (DoskerEditorUi.PrimaryButton(DoskerLocale.Tr("Medir", "Measure"), "Refresh", DoskerEditorUi.Blue))
                    {
                        Measure();
                    }
                }
            }

            scroll = EditorGUILayout.BeginScrollView(scroll);
            if (avatar == null)
            {
                DoskerEditorUi.EmptyState(
                    DoskerLocale.Tr("Nenhum avatar na cena", "No avatar in the scene"),
                    DoskerLocale.Tr("Abra uma cena com um VRC Avatar Descriptor.", "Open a scene with a VRC Avatar Descriptor."));
            }
            else if (scene == null)
            {
                DoskerEditorUi.EmptyState(
                    DoskerLocale.Tr("Ainda sem medição", "Not measured yet"),
                    DoskerLocale.Tr("Clique em Medir. Um Build & Test também calibra a previsão.", "Click Measure. A Build & Test also calibrates the prediction."));
            }
            else
            {
                DrawConfidence();
                DrawOverall();
                DrawTable();
                DrawHeaviest();
            }
            EditorGUILayout.EndScrollView();
            DoskerEditorUi.AboutFooter(typeof(PerformanceBudgetWindow), DocsUrl);
        }

        private void Clear()
        {
            scene = null;
            calibration = null;
        }

        private static VRCAvatarDescriptor FindSceneAvatar()
        {
            var selected = Selection.activeGameObject;
            var found = selected != null ? selected.GetComponentInParent<VRCAvatarDescriptor>() : null;
            return found != null ? found : FindObjectOfType<VRCAvatarDescriptor>();
        }

        private void DrawConfidence()
        {
            switch (confidence)
            {
                case BudgetConfidence.SceneOnly:
                    DoskerEditorUi.Banner(
                        DoskerLocale.Tr(
                            "Sem build nesta plataforma: a previsão é igual à cena, e o VRCFury ainda pode mudar o ranking. Faça um Build & Test para calibrar.",
                            "No build on this platform: the prediction equals the scene, and VRCFury can still change the rank. Run a Build & Test to calibrate."),
                        DoskerEditorUi.BannerKind.Warning);
                    break;
                case BudgetConfidence.Stale:
                    DoskerEditorUi.Banner(
                        DoskerLocale.Tr(
                            "Componentes do VRCFury mudaram desde o último build, então a previsão pode estar errada. Faça outro Build & Test.",
                            "VRCFury components changed since the last build, so the prediction may be wrong. Run another Build & Test."),
                        DoskerEditorUi.BannerKind.Warning);
                    break;
                default:
                    DoskerEditorUi.Banner(
                        DoskerLocale.Tr(
                            $"Previsão calibrada pelo build de {calibration.builtAt}. Mudanças dentro de componentes do VRCFury não são detectadas.",
                            $"Prediction calibrated by the build of {calibration.builtAt}. Changes inside VRCFury components aren't detected."),
                        DoskerEditorUi.BannerKind.Info);
                    break;
            }
        }

        private void DrawOverall()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                DrawOverallPill("Cena", sceneStats);
                DrawOverallPill("Previsão", predictedStats);
                if (realStats != null)
                {
                    DrawOverallPill("Real", realStats);
                }
            }
        }

        private static void DrawOverallPill(string label, AvatarPerformanceStats stats)
        {
            var rating = stats.GetPerformanceRatingForCategory(AvatarPerformanceCategory.Overall);
            DoskerEditorUi.StatusPill($"{label}: {AvatarPerformanceStats.GetPerformanceRatingDisplayName(rating)}", RatingColor(rating));
        }

        private static Color RatingColor(PerformanceRating rating)
        {
            switch (rating)
            {
                case PerformanceRating.Excellent:
                case PerformanceRating.Good:
                    return DoskerEditorUi.Green;
                case PerformanceRating.Medium:
                    return DoskerEditorUi.Yellow;
                case PerformanceRating.Poor:
                case PerformanceRating.VeryPoor:
                    return DoskerEditorUi.Red;
                default:
                    return DoskerEditorUi.Neutral;
            }
        }

        private void DrawTable()
        {
            onlyProblems = EditorGUILayout.ToggleLeft(DoskerLocale.Tr("Só categorias abaixo de Good", "Only categories below Good"), onlyProblems);
            DoskerEditorUi.TableHeader(Columns, ref sortColumn, ref ascending);
            foreach (var category in BudgetProbe.Categories)
            {
                var rating = BudgetProbe.RatingOf(predictedStats, category);
                if (onlyProblems && rating <= PerformanceRating.Good)
                {
                    continue;
                }

                var row = GUILayoutUtility.GetRect(1f, 18f, GUILayout.ExpandWidth(true));
                var cells = DoskerEditorUi.TableCells(row, Columns);
                GUI.Label(cells[0], category, EditorStyles.miniLabel);
                DrawValue(cells[1], scene.Get(category), sceneStats, category);
                DrawValue(cells[2], predicted.Get(category), predictedStats, category);
                if (realStats != null)
                {
                    DrawValue(cells[3], calibration.after.Get(category), realStats, category);
                }
            }
        }

        private static void DrawValue(Rect rect, float value, AvatarPerformanceStats stats, string category)
        {
            var rating = BudgetProbe.RatingOf(stats, category);
            EditorGUI.DrawRect(new Rect(rect.x, rect.y + 2f, 3f, rect.height - 4f), RatingColor(rating));
            var text = value == Mathf.Round(value) ? value.ToString("N0") : value.ToString("0.0");
            GUI.Label(new Rect(rect.x + 6f, rect.y, rect.width - 6f, rect.height), new GUIContent(text, rating.ToString()), EditorStyles.miniLabel);
        }

        private void DrawHeaviest()
        {
            using (var card = new DoskerEditorUi.CardScope("budget.heaviest", DoskerLocale.Tr("Meshes mais pesadas", "Heaviest meshes"), "Mesh Icon", DoskerEditorUi.Purple))
            {
                if (!card.Expanded)
                {
                    return;
                }
                foreach (var (name, triangles) in heaviest)
                {
                    EditorGUILayout.LabelField(name, triangles.ToString("N0"));
                }
            }
        }
    }
}

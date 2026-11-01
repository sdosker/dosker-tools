using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.ScriptableObjects;
using ControlType = VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionsMenu.Control.ControlType;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace CompressorExclusions {
    /**
     * Keeps protected parameters out of the VRCFury compressor, without modifying the VRCFury package.
     *
     * The solver's inner GetParamsToOptimize only uses paramz to find which parameters are eligible, and
     * originalCost is passed separately. So handing it a copy without the protected parameters is enough:
     * they still count towards the bit total, and the attempt loop keeps working unmodified.
     */
    [InitializeOnLoad]
    internal static class CompressorExclusionPatch {
        private const string SolverTypeName = "VF.Service.Compressor.ParameterCompressorSolverService";
        private const string SolverMethodName = "GetParamsToOptimize";
        private const string MenuMethodName = "GetParamsUsedInMenu";
        private const string LogPrefix = "[Compressor Exclusions]";
        // Same version as scripts/vrcfury_compat.json
        private const string TestedVrcfury = "1.1436.0";

        private static readonly ControlType[] MenuControls = {
            ControlType.RadialPuppet,
            ControlType.Toggle,
            ControlType.TwoAxisPuppet,
            ControlType.FourAxisPuppet,
            ControlType.Button,
            ControlType.SubMenu,
        };

        // If set, protections are NOT being applied and the build must warn about it
        public static string SolverError { get; private set; }
        // If set, protections still work, but something else (like the control rules or the window data) doesn't
        public static string ReportingError { get; private set; }
        public static string VrcfuryVersion { get; private set; } = "?";

        public static bool IsNewerThanTested {
            get {
                if (!Version.TryParse(VrcfuryVersion.Split('-')[0], out var current)) return false;
                return current > Version.Parse(TestedVrcfury);
            }
        }

        private static readonly MethodInfo BitWidthMethod =
            AccessTools.Method(typeof(VRCExpressionParameters.Parameter), "GetBitWidthByType", Type.EmptyTypes);
        private static readonly FieldInfo FlippedField =
            AccessTools.Field(typeof(VRCExpressionParameters.Parameter), "flippedRepresentation");

        private static MethodInfo menuMethod;
        private static VRCExpressionParameters filteredParams;
        private static object currentSolver;
        private static Snapshot currentSnapshot;
        private static Dictionary<string, int> currentControls = new Dictionary<string, int>();
        private static Dictionary<string, string> currentMenuPaths = new Dictionary<string, string>();

        static CompressorExclusionPatch() {
            try {
                Apply();
            } catch (Exception e) {
                SolverError = $"Não foi possível aplicar o patch no VRCFury: {e.Message}";
                Debug.LogError($"{LogPrefix} {SolverError}\n{e}");
            }
        }

        private static void Apply() {
            var solverType = AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType(SolverTypeName))
                .FirstOrDefault(type => type != null);
            if (solverType == null) {
                throw new Exception($"Tipo {SolverTypeName} não encontrado.");
            }

            VrcfuryVersion = PackageInfo.FindForAssembly(solverType.Assembly)?.version ?? "?";

            var innerMethod = AccessTools.GetDeclaredMethods(solverType).FirstOrDefault(method =>
                method.Name == SolverMethodName
                && method.GetParameters().FirstOrDefault()?.ParameterType == typeof(VRCExpressionParameters));
            if (innerMethod == null) {
                throw new Exception($"Método {SolverMethodName}(VRCExpressionParameters, ...) não encontrado.");
            }

            CheckSolverShape(innerMethod);

            var harmony = new Harmony("compressor-exclusions");
            harmony.Patch(innerMethod, prefix: new HarmonyMethod(Method(nameof(SolverPrefix))));

            // Everything below only feeds reports and control rules, so failing here must not stop the protections
            menuMethod = AccessTools.GetDeclaredMethods(solverType).FirstOrDefault(method =>
                method.Name == MenuMethodName && method.GetParameters().Length == 1);
            if (menuMethod == null) {
                ReportingError = $"Método {MenuMethodName} não encontrado: as regras por controle não funcionam.";
                Debug.LogWarning($"{LogPrefix} {ReportingError}");
            }

            try {
                var outerMethod = solverType.GetMethod(SolverMethodName, Type.EmptyTypes);
                if (outerMethod == null) throw new Exception($"Método {SolverMethodName}() não encontrado.");
                CheckResultShape(outerMethod.ReturnType);
                harmony.Patch(outerMethod, postfix: new HarmonyMethod(Method(nameof(SolverResultPostfix))));
            } catch (Exception e) {
                ReportingError = $"O resultado real do build não será mostrado: {e.Message}";
                Debug.LogWarning($"{LogPrefix} {ReportingError}");
            }
        }

        /**
         * The checklist for VRCFury updates, done at load. Without paramz the protections can't work, so this fails
         * before patching and the build check warns about it.
         */
        private static void CheckSolverShape(MethodInfo innerMethod) {
            var names = innerMethod.GetParameters().Select(parameter => parameter.Name).ToList();
            var missing = new[] { "paramz", "addDriven", "originalCost" }.Where(name => !names.Contains(name)).ToList();
            if (missing.Count > 0) {
                throw new Exception($"O método {SolverMethodName} não tem mais: {string.Join(", ", missing)}.");
            }
        }

        private static void CheckResultShape(Type outputType) {
            var decisionType = AccessTools.Field(outputType, "decision")?.FieldType;
            if (decisionType == null) throw new Exception("O resultado do solver não tem mais o campo decision.");

            var missing = new[] { "compress", "numberSlots", "boolSlots" }
                .Where(name => AccessTools.Field(decisionType, name) == null)
                .Concat(new[] { "GetBatchCount", "GetFinalCost" }.Where(name => AccessTools.Method(decisionType, name) == null))
                .ToList();
            if (missing.Count > 0) throw new Exception($"O resultado do solver não tem mais: {string.Join(", ", missing)}.");
        }

        private static MethodInfo Method(string name) {
            return typeof(CompressorExclusionPatch).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic);
        }

        private static void SolverPrefix(
            object __instance,
            ref VRCExpressionParameters paramz,
            ISet<string> addDriven,
            int originalCost
        ) {
            var settings = CompressorExclusionSettings.Instance;

            // The solver calls this once per attempt, and each build gets a new solver
            if (!ReferenceEquals(__instance, currentSolver)) {
                currentSolver = __instance;
                currentControls = ReadMenuControls(__instance);
                currentMenuPaths = ReadMenuPaths(__instance);
                Report(__instance, settings, paramz, addDriven, originalCost);
            }

            if (!settings.active) return;

            if (filteredParams == null) {
                filteredParams = ScriptableObject.CreateInstance<VRCExpressionParameters>();
                filteredParams.hideFlags = HideFlags.HideAndDontSave;
            }
            filteredParams.parameters = paramz.parameters
                .Where(p => !CompressorPolicy.Evaluate(settings, p.name, p.valueType, ControlOf(p.name), MenuPathOf(p.name)).isProtected)
                .ToArray();
            paramz = filteredParams;
        }

        private static int ControlOf(string paramName) {
            return currentControls.TryGetValue(paramName, out var control) ? control : CompressorPolicy.NoControl;
        }

        private static string MenuPathOf(string paramName) {
            return currentMenuPaths.TryGetValue(paramName, out var path) ? path : "";
        }

        private static Dictionary<string, string> ReadMenuPaths(object solver) {
            var output = new Dictionary<string, string>();
            try {
                var menu = Traverse.Create(solver).Property("menuReadOnly").GetValue<VRCExpressionsMenu>();
                CompressorPolicy.WalkMenu(menu, (control, path) => {
                    foreach (var param in new[] { control.parameter }.Concat(control.subParameters ?? new VRCExpressionsMenu.Control.Parameter[0])) {
                        if (!string.IsNullOrEmpty(param?.name) && !output.ContainsKey(param.name)) output[param.name] = path;
                    }
                });
            } catch (Exception e) {
                ReportingError = $"Não foi possível ler os caminhos do menu: os padrões menu: não funcionam. {e.Message}";
                Debug.LogWarning($"{LogPrefix} {ReportingError}");
            }
            return output;
        }

        private static Dictionary<string, int> ReadMenuControls(object solver) {
            var output = new Dictionary<string, int>();
            if (menuMethod == null) return output;
            try {
                foreach (var control in MenuControls) {
                    var filter = new object[] { new HashSet<ControlType> { control } };
                    var names = (IEnumerable<string>)menuMethod.Invoke(solver, filter);
                    foreach (var name in names) {
                        output[name] = (int)control;
                    }
                }
            } catch (Exception e) {
                ReportingError = $"Não foi possível ler o menu: as regras por controle não funcionam. {e.Message}";
                Debug.LogWarning($"{LogPrefix} {ReportingError}");
            }
            return output;
        }

        private static void Report(
            object solver,
            CompressorExclusionSettings settings,
            VRCExpressionParameters paramz,
            ISet<string> addDriven,
            int originalCost
        ) {
            try {
                var synced = paramz.parameters.Where(p => p.networkSynced).ToList();
                var snapshot = NewSnapshot(solver, settings);
                snapshot.needsCompression = true;
                snapshot.originalCost = originalCost;
                snapshot.parameters = synced.Select(p => ToSnapshotParam(p, addDriven)).ToList();
                AddPredictions(snapshot, settings);
                // Saved now, because if the avatar doesn't fit then VRCFury throws before the result is available
                CompressorSnapshotStore.Save(snapshot);
                currentSnapshot = snapshot;

                if (!settings.active) return;

                var protectedNames = synced
                    .Where(p => CompressorPolicy.Evaluate(settings, p.name, p.valueType, ControlOf(p.name), MenuPathOf(p.name)).isProtected)
                    .Select(p => p.name)
                    .ToList();
                Debug.Log($"{LogPrefix} {protectedNames.Count} parameters protected from compression: "
                          + string.Join(", ", protectedNames));

                foreach (var pattern in settings.namePatterns.Where(p => !string.IsNullOrWhiteSpace(p))) {
                    if (!synced.Any(p => CompressorPolicy.MatchesPattern(pattern, p.name, MenuPathOf(p.name)))) {
                        Debug.LogWarning($"{LogPrefix} The name pattern \"{pattern}\" did not match any synced parameter.");
                    }
                }
            } catch (Exception e) {
                ReportingError = $"Não foi possível gravar o retrato do build: {e.Message}";
                Debug.LogWarning($"{LogPrefix} {ReportingError}");
            }
        }

        private static void AddPredictions(Snapshot snapshot, CompressorExclusionSettings settings) {
            var previous = CompressorSnapshotStore.Load(snapshot.Key);
            if (previous != null && previous.parameters.Count > 0) {
                snapshot.predictionBefore = CompressorPolicy.Predict(settings, previous.parameters, previous.originalCost, previous.maxCost);
            }
            snapshot.predictionAtBuild = CompressorPolicy.Predict(settings, snapshot.parameters, snapshot.originalCost, snapshot.maxCost);
        }

        private static void WarnIfPlatformsDiffer(Snapshot snapshot) {
            var other = CompressorSnapshotStore.Counterpart(snapshot);
            if (other != null && other.settingsFingerprint != snapshot.settingsFingerprint) {
                Debug.LogWarning($"{LogPrefix} {other.platform} was built with different settings. Upload PC first, then Quest, or they won't sync.");
            }
        }

        private static SnapshotParam ToSnapshotParam(VRCExpressionParameters.Parameter param, ISet<string> addDriven) {
            var isBool = param.valueType == VRCExpressionParameters.ValueType.Bool;
            // Older SDKs don't have dynamic bit widths. Same rule as VRCFury's HasStandardNetworkEncoding.
            var cost = isBool ? 1 : BitWidthMethod == null ? 8 : Convert.ToInt32(BitWidthMethod.Invoke(param, null));
            var flipped = !isBool && FlippedField != null && (bool)FlippedField.GetValue(param);
            return new SnapshotParam {
                name = param.name,
                valueType = (int)param.valueType,
                control = ControlOf(param.name),
                menuPath = MenuPathOf(param.name),
                addDriven = addDriven.Contains(param.name),
                cost = cost,
                nonStandardEncoding = !isBool && (cost != 8 || flipped)
            };
        }

        private static Snapshot FitsWithoutCompression(object solver) {
            var snapshot = NewSnapshot(solver, CompressorExclusionSettings.Instance);
            try {
                currentControls = ReadMenuControls(solver);
                currentMenuPaths = ReadMenuPaths(solver);
                var paramz = Traverse.Create(solver).Field("paramsService").Method("GetReadOnlyParams")
                    .GetValue<VRCExpressionParameters>();
                snapshot.originalCost = paramz.CalcTotalCost();
                snapshot.parameters = paramz.parameters.Where(p => p.networkSynced)
                    .Select(p => ToSnapshotParam(p, new HashSet<string>()))
                    .ToList();
                AddPredictions(snapshot, CompressorExclusionSettings.Instance);
                snapshot.hasResult = true;
                snapshot.resultCost = snapshot.originalCost;
            } catch (Exception e) {
                // The list is only for display, so the one from the previous build is good enough
                snapshot.parameters = CompressorSnapshotStore.Load(snapshot.Key)?.parameters ?? snapshot.parameters;
                ReportingError = $"Não foi possível ler os parâmetros do build: {e.Message}";
                Debug.LogWarning($"{LogPrefix} {ReportingError}");
            }
            return snapshot;
        }

        private static void SolverResultPostfix(object __instance, object __result) {
            try {
                if (!ReferenceEquals(__instance, currentSolver)) {
                    // The avatar fits without compression, so the solver returned before any attempt
                    var fitting = FitsWithoutCompression(__instance);
                    CompressorSnapshotStore.Save(fitting, true);
                    WarnIfPlatformsDiffer(fitting);
                    return;
                }

                var decision = Traverse.Create(__result).Field("decision");
                var compressed = new HashSet<string>(
                    decision.Field("compress").GetValue<IEnumerable<VRCExpressionParameters.Parameter>>().Select(p => p.name)
                );
                var snapshot = currentSnapshot;
                foreach (var param in snapshot.parameters) {
                    param.compressed = compressed.Contains(param.name);
                }
                snapshot.hasResult = true;
                snapshot.resultCompressed = compressed.Count;
                snapshot.resultNumberSlots = decision.Field("numberSlots").GetValue<int>();
                snapshot.resultBoolSlots = decision.Field("boolSlots").GetValue<int>();
                snapshot.resultBatches = decision.Method("GetBatchCount").GetValue<int>();
                snapshot.resultCost = compressed.Count == 0
                    ? snapshot.originalCost
                    : decision.Method("GetFinalCost", snapshot.originalCost).GetValue<int>();
                CompressorSnapshotStore.Save(snapshot, true);
                WarnIfPlatformsDiffer(snapshot);
            } catch (Exception e) {
                ReportingError = $"Não foi possível ler o resultado do build: {e.Message}";
                Debug.LogWarning($"{LogPrefix} {ReportingError}");
            } finally {
                currentSolver = null;
                currentSnapshot = null;
            }
        }

        private static Snapshot NewSnapshot(object solver, CompressorExclusionSettings settings) {
            return new Snapshot {
                avatarName = AvatarName(solver),
                platform = CompressorSnapshotStore.CurrentPlatform(),
                vrcfuryVersion = VrcfuryVersion,
                builtAt = DateTime.Now.ToString("dd/MM/yyyy HH:mm"),
                settingsFingerprint = settings.Fingerprint(),
                maxCost = MaxCost()
            };
        }

        private static string AvatarName(object solver) {
            var name = Traverse.Create(solver).Field("avatarObject").Property("name").GetValue<string>();
            return string.IsNullOrEmpty(name) ? "Avatar" : name.Replace("(Clone)", "").Trim();
        }

        public static int MaxCost() {
            // Same guard as VRCFury: some modified SDKs have a broken value for this
            return VRCExpressionParameters.MAX_PARAMETER_COST > 9999 ? 256 : VRCExpressionParameters.MAX_PARAMETER_COST;
        }
    }
}

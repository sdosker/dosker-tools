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

        private static MethodInfo menuMethod;
        private static VRCExpressionParameters filteredParams;
        private static object currentSolver;
        private static Snapshot currentSnapshot;
        private static Dictionary<string, int> currentControls = new Dictionary<string, int>();

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
                harmony.Patch(outerMethod, postfix: new HarmonyMethod(Method(nameof(SolverResultPostfix))));
            } catch (Exception e) {
                ReportingError = $"O resultado real do build não será mostrado: {e.Message}";
                Debug.LogWarning($"{LogPrefix} {ReportingError}");
            }
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
                Report(__instance, settings, paramz, addDriven, originalCost);
            }

            if (!settings.active) return;

            if (filteredParams == null) {
                filteredParams = ScriptableObject.CreateInstance<VRCExpressionParameters>();
                filteredParams.hideFlags = HideFlags.HideAndDontSave;
            }
            filteredParams.parameters = paramz.parameters
                .Where(p => !CompressorPolicy.Evaluate(settings, p.name, p.valueType, ControlOf(p.name)).isProtected)
                .ToArray();
            paramz = filteredParams;
        }

        private static int ControlOf(string paramName) {
            return currentControls.TryGetValue(paramName, out var control) ? control : CompressorPolicy.NoControl;
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
                snapshot.parameters = synced.Select(p => new SnapshotParam {
                    name = p.name,
                    valueType = (int)p.valueType,
                    control = ControlOf(p.name),
                    addDriven = addDriven.Contains(p.name)
                }).ToList();
                // Saved now, because if the avatar doesn't fit then VRCFury throws before the result is available
                CompressorSnapshotStore.Save(snapshot);
                currentSnapshot = snapshot;

                if (!settings.active) return;

                var protectedNames = synced
                    .Where(p => CompressorPolicy.Evaluate(settings, p.name, p.valueType, ControlOf(p.name)).isProtected)
                    .Select(p => p.name)
                    .ToList();
                Debug.Log($"{LogPrefix} {protectedNames.Count} parameters protected from compression: "
                          + string.Join(", ", protectedNames));

                foreach (var pattern in settings.namePatterns.Where(p => !string.IsNullOrWhiteSpace(p))) {
                    if (!synced.Any(p => CompressorPolicy.MatchesPattern(pattern, p.name))) {
                        Debug.LogWarning($"{LogPrefix} The name pattern \"{pattern}\" did not match any synced parameter.");
                    }
                }
            } catch (Exception e) {
                ReportingError = $"Não foi possível gravar o retrato do build: {e.Message}";
                Debug.LogWarning($"{LogPrefix} {ReportingError}");
            }
        }

        private static void SolverResultPostfix(object __instance, object __result) {
            try {
                if (!ReferenceEquals(__instance, currentSolver)) {
                    // The avatar fits without compression, so the solver returned before any attempt
                    CompressorSnapshotStore.Save(NewSnapshot(__instance, CompressorExclusionSettings.Instance));
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
                CompressorSnapshotStore.Save(snapshot);
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

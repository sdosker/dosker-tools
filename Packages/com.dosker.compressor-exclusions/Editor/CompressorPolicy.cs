using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using static DoskerTools.EditorUi.DoskerLocale;
using ControlType = VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionsMenu.Control.ControlType;
using ParamType = VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionParameters.ValueType;

namespace CompressorExclusions {
    internal readonly struct Verdict {
        public readonly bool isProtected;
        public readonly string reason;
        public readonly ParamChoice choice;
        // Every rule that matches, while reason only has the one that decided
        public readonly string details;

        public Verdict(bool isProtected, string reason, ParamChoice choice, string details = null) {
            this.isProtected = isProtected;
            this.reason = reason;
            this.choice = choice;
            this.details = details ?? reason;
        }
    }

    /**
     * The one place that decides if a parameter is protected from compression.
     * The build, the window and the inspector must all agree, so they all ask here.
     */
    internal static class CompressorPolicy {
        public const int NoControl = -1;
        private const string MenuPrefix = "menu:";
        private const char NegationPrefix = '!';

        // Same order as VRCFury's MenuTypePriority: a parameter in several controls counts as the last one here
        private static readonly ControlType[] ControlPriority = {
            ControlType.RadialPuppet,
            ControlType.Toggle,
            ControlType.TwoAxisPuppet,
            ControlType.FourAxisPuppet,
            ControlType.Button,
            ControlType.SubMenu,
        };

        private static readonly int[] CompressibleControls = {
            (int)ControlType.Toggle,
            (int)ControlType.RadialPuppet,
            (int)ControlType.TwoAxisPuppet,
            (int)ControlType.FourAxisPuppet,
        };

        private static readonly Regex VrcfuryPrefix = new Regex(@"^VF\d+_", RegexOptions.Compiled);
        private static readonly Dictionary<string, Regex> PatternCache = new Dictionary<string, Regex>();

        // VRCFury adds a VF<number>_ prefix to parameter names, and that number changes between builds
        public static string Normalize(string name) {
            return string.IsNullOrEmpty(name) ? "" : VrcfuryPrefix.Replace(name, "");
        }

        public static bool IsNegated(string pattern) {
            return !string.IsNullOrWhiteSpace(pattern) && pattern.Trim()[0] == NegationPrefix;
        }

        /**
         * * and ? are wildcards. menu:Roupas/* looks at the menu path instead of the parameter name,
         * and a leading ! is handled by Evaluate, so it doesn't change what the pattern matches.
         */
        public static bool MatchesPattern(string pattern, string name, string menuPath = "") {
            if (string.IsNullOrWhiteSpace(pattern) || string.IsNullOrEmpty(name)) return false;
            pattern = pattern.Trim().TrimStart(NegationPrefix).Trim();
            var usesMenu = pattern.StartsWith(MenuPrefix, StringComparison.OrdinalIgnoreCase);
            if (usesMenu) pattern = pattern.Substring(MenuPrefix.Length);
            if (!PatternCache.TryGetValue(pattern, out var regex)) {
                var escaped = Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".");
                regex = new Regex("^" + escaped + "$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
                PatternCache[pattern] = regex;
            }
            if (usesMenu) return !string.IsNullOrEmpty(menuPath) && regex.IsMatch(menuPath);
            return regex.IsMatch(name) || regex.IsMatch(Normalize(name));
        }

        public static Verdict Evaluate(CompressorExclusionSettings settings, string name, ParamType type, int control, string menuPath = "") {
            var choice = settings.GetChoice(name);
            if (!settings.active) {
                return new Verdict(false, Tr("Exceções desligadas: segue o VRCFury", "Exclusions off: follows VRCFury"), choice);
            }
            if (choice == ParamChoice.Protect) {
                return new Verdict(true, Tr("Protegido por escolha individual", "Protected by an individual choice"), choice);
            }
            if (choice == ParamChoice.Compress) {
                return new Verdict(false, Tr("Liberado por escolha individual", "Released by an individual choice"), choice);
            }

            var matches = new List<string>();
            if (type == ParamType.Bool && settings.protectBool) matches.Add(Tr("Protegido pela regra Bool", "Protected by the Bool rule"));
            if (type == ParamType.Int && settings.protectInt) matches.Add(Tr("Protegido pela regra Int", "Protected by the Int rule"));
            if (type == ParamType.Float && settings.protectFloat) matches.Add(Tr("Protegido pela regra Float", "Protected by the Float rule"));
            if (control != NoControl && settings.protectedControls.Contains((ControlType)control)) {
                matches.Add(Tr($"Protegido pela regra {ControlLabel(control)}", $"Protected by the {ControlLabel(control)} rule"));
            }
            foreach (var pattern in settings.namePatterns.Where(p => !IsNegated(p))) {
                if (MatchesPattern(pattern, name, menuPath)) matches.Add(Tr($"Protegido pelo padrão \"{pattern.Trim()}\"", $"Protected by the pattern \"{pattern.Trim()}\""));
            }

            var details = string.Join("\n", matches);
            foreach (var pattern in settings.namePatterns.Where(IsNegated)) {
                if (MatchesPattern(pattern, name, menuPath)) {
                    var reason = Tr($"Liberado pelo padrão \"{pattern.Trim()}\"", $"Released by the pattern \"{pattern.Trim()}\"");
                    return new Verdict(false, reason, choice, matches.Count == 0 ? reason : reason + "\n" + details);
                }
            }
            return matches.Count > 0
                ? new Verdict(true, matches[0], choice, details)
                : new Verdict(false, Tr("Pode ser comprimido", "Can be compressed"), choice);
        }

        public static bool CanCompress(SnapshotParam param) {
            return !param.nonStandardEncoding && CompressibleControls.Contains(param.control);
        }

        public static EstimatorParam ToEstimator(SnapshotParam param) {
            return new EstimatorParam {
                name = param.name,
                isBool = (ParamType)param.valueType == ParamType.Bool,
                control = param.control,
                addDriven = param.addDriven,
                nonStandardEncoding = param.nonStandardEncoding
            };
        }

        public static void Split(
            CompressorExclusionSettings settings,
            IEnumerable<SnapshotParam> parameters,
            out List<EstimatorParam> candidates,
            out List<EstimatorParam> protectedParams
        ) {
            candidates = new List<EstimatorParam>();
            protectedParams = new List<EstimatorParam>();
            foreach (var param in parameters) {
                var verdict = Evaluate(settings, param.name, (ParamType)param.valueType, param.control, param.menuPath);
                (verdict.isProtected ? protectedParams : candidates).Add(ToEstimator(param));
            }
        }

        public static Prediction Predict(
            CompressorExclusionSettings settings,
            IEnumerable<SnapshotParam> parameters,
            int originalCost,
            int maxCost,
            bool partial = false
        ) {
            var list = parameters.ToList();
            Split(settings, list, out var candidates, out var protectedParams);
            var protectedCount = protectedParams.Count(p => CompressibleControls.Contains(p.control) && !p.nonStandardEncoding);
            return Prediction.From(CompressorEstimator.Solve(candidates, originalCost, maxCost), protectedCount, Normalize, partial);
        }

        /**
         * Parameters from VRCFury only exist during a build, so this only sees what is already on the descriptor.
         * Returns null if the avatar has no parameters asset.
         */
        public static Snapshot SnapshotFromScene(VRCAvatarDescriptor avatar, int maxCost) {
            var paramz = avatar.expressionParameters;
            if (paramz == null || paramz.parameters == null) return null;

            var controls = new Dictionary<string, int>();
            var paths = new Dictionary<string, string>();
            void Use(VRCExpressionsMenu.Control.Parameter param, ControlType type, string path) {
                if (string.IsNullOrEmpty(param?.name)) return;
                if (controls.TryGetValue(param.name, out var old)
                    && Array.IndexOf(ControlPriority, type) < Array.IndexOf(ControlPriority, (ControlType)old)) return;
                controls[param.name] = (int)type;
                paths[param.name] = path;
            }
            WalkMenu(avatar.expressionsMenu, (control, path) => {
                var isPuppet = control.type == ControlType.RadialPuppet
                               || control.type == ControlType.TwoAxisPuppet
                               || control.type == ControlType.FourAxisPuppet;
                Use(control.parameter, isPuppet ? ControlType.SubMenu : control.type, path);
                if (isPuppet && control.subParameters != null) {
                    foreach (var sub in control.subParameters) Use(sub, control.type, path);
                }
            });

            var originalCost = paramz.CalcTotalCost();
            return new Snapshot {
                avatarName = avatar.name,
                originalCost = originalCost,
                maxCost = maxCost,
                needsCompression = originalCost > maxCost,
                parameters = paramz.parameters.Where(p => p.networkSynced).Select(p => new SnapshotParam {
                    name = p.name,
                    valueType = (int)p.valueType,
                    control = controls.TryGetValue(p.name, out var control) ? control : NoControl,
                    menuPath = paths.TryGetValue(p.name, out var path) ? path : "",
                    cost = CostOf(p.valueType)
                }).ToList()
            };
        }

        public static void WalkMenu(VRCExpressionsMenu menu, Action<VRCExpressionsMenu.Control, string> visit) {
            var seen = new HashSet<VRCExpressionsMenu>();
            void Walk(VRCExpressionsMenu current, string prefix) {
                if (current == null || !seen.Add(current)) return;
                foreach (var control in current.controls) {
                    var path = prefix + control.name;
                    visit(control, path);
                    if (control.type == ControlType.SubMenu) Walk(control.subMenu, path + "/");
                }
            }
            Walk(menu, "");
        }

        public static string ControlLabel(int control) {
            switch ((ControlType)control) {
                case ControlType.Toggle: return "Toggle";
                case ControlType.RadialPuppet: return "Radial";
                case ControlType.TwoAxisPuppet: return Tr("2 eixos", "2 axes");
                case ControlType.FourAxisPuppet: return Tr("4 eixos", "4 axes");
                case ControlType.Button: return Tr("Botão", "Button");
                case ControlType.SubMenu: return "Submenu";
                default: return Tr("Fora do menu", "Not in menu");
            }
        }

        public static int CostOf(ParamType type) {
            return type == ParamType.Bool ? 1 : 8;
        }
    }
}

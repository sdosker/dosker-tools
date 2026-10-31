using System.Collections.Generic;
using System.Text.RegularExpressions;
using ControlType = VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionsMenu.Control.ControlType;
using ParamType = VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionParameters.ValueType;

namespace CompressorExclusions {
    internal readonly struct Verdict {
        public readonly bool isProtected;
        public readonly string reason;
        public readonly ParamChoice choice;

        public Verdict(bool isProtected, string reason, ParamChoice choice) {
            this.isProtected = isProtected;
            this.reason = reason;
            this.choice = choice;
        }
    }

    /**
     * The one place that decides if a parameter is protected from compression.
     * The build, the window and the inspector must all agree, so they all ask here.
     */
    internal static class CompressorPolicy {
        public const int NoControl = -1;

        private static readonly Regex VrcfuryPrefix = new Regex(@"^VF\d+_", RegexOptions.Compiled);
        private static readonly Dictionary<string, Regex> PatternCache = new Dictionary<string, Regex>();

        // VRCFury adds a VF<number>_ prefix to parameter names, and that number changes between builds
        public static string Normalize(string name) {
            return string.IsNullOrEmpty(name) ? "" : VrcfuryPrefix.Replace(name, "");
        }

        public static bool MatchesPattern(string pattern, string name) {
            if (string.IsNullOrWhiteSpace(pattern) || string.IsNullOrEmpty(name)) return false;
            pattern = pattern.Trim();
            if (!PatternCache.TryGetValue(pattern, out var regex)) {
                var escaped = Regex.Escape(pattern).Replace("\\*", ".*");
                regex = new Regex("^" + escaped + "$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
                PatternCache[pattern] = regex;
            }
            return regex.IsMatch(name) || regex.IsMatch(Normalize(name));
        }

        public static Verdict Evaluate(CompressorExclusionSettings settings, string name, ParamType type, int control) {
            var choice = settings.GetChoice(name);
            if (!settings.active) {
                return new Verdict(false, "Exceções desligadas: segue o VRCFury", choice);
            }
            if (choice == ParamChoice.Protect) {
                return new Verdict(true, "Protegido por escolha individual", choice);
            }
            if (choice == ParamChoice.Compress) {
                return new Verdict(false, "Liberado por escolha individual", choice);
            }

            if (type == ParamType.Bool && settings.protectBool) {
                return new Verdict(true, "Protegido pela regra Bool", choice);
            }
            if (type == ParamType.Int && settings.protectInt) {
                return new Verdict(true, "Protegido pela regra Int", choice);
            }
            if (type == ParamType.Float && settings.protectFloat) {
                return new Verdict(true, "Protegido pela regra Float", choice);
            }
            if (control != NoControl && settings.protectedControls.Contains((ControlType)control)) {
                return new Verdict(true, $"Protegido pela regra {ControlLabel(control)}", choice);
            }
            foreach (var pattern in settings.namePatterns) {
                if (MatchesPattern(pattern, name)) {
                    return new Verdict(true, $"Protegido pelo padrão \"{pattern.Trim()}\"", choice);
                }
            }
            return new Verdict(false, "Pode ser comprimido", choice);
        }

        public static string ControlLabel(int control) {
            switch ((ControlType)control) {
                case ControlType.Toggle: return "Toggle";
                case ControlType.RadialPuppet: return "Radial";
                case ControlType.TwoAxisPuppet: return "2 eixos";
                case ControlType.FourAxisPuppet: return "4 eixos";
                case ControlType.Button: return "Botão";
                case ControlType.SubMenu: return "Submenu";
                default: return "Fora do menu";
            }
        }

        public static int CostOf(ParamType type) {
            return type == ParamType.Bool ? 1 : 8;
        }
    }
}

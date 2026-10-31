using System.Linq;
using UnityEditor;
using UnityEngine.UIElements;

namespace CompressorExclusions {
    internal static class CompressorUi {
        public const string Version = "1.1.2";

        private const string StyleSheetName = "CompressorExclusions";

        public static void ApplyStyles(VisualElement root) {
            // Searched by name so that the tool folder can be moved
            var path = AssetDatabase.FindAssets($"{StyleSheetName} t:StyleSheet")
                .Select(AssetDatabase.GUIDToAssetPath)
                .FirstOrDefault();
            var sheet = path == null ? null : AssetDatabase.LoadAssetAtPath<StyleSheet>(path);
            if (sheet != null) root.styleSheets.Add(sheet);
        }

        public static string ChoiceLabel(ParamChoice choice) {
            switch (choice) {
                case ParamChoice.Protect: return "Proteger";
                case ParamChoice.Compress: return "Comprimir";
                default: return "Auto";
            }
        }
    }
}

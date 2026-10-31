using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using ControlType = VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionsMenu.Control.ControlType;

namespace CompressorExclusions {
    public enum ParamChoice {
        Auto,
        Protect,
        Compress
    }

    [Serializable]
    public class ParamOverride {
        public string name;
        public ParamChoice choice;
    }

    /**
     * Project-wide settings. The window and the parameters inspector both read and write this asset,
     * and every write goes through Mutate so that both stay in sync.
     */
    public class CompressorExclusionSettings : ScriptableObject {
        private const string AssetPath = "Assets/CompressorExclusions/CompressorExclusions.asset";

        public bool active = true;
        public bool protectBool = true;
        public bool protectInt;
        public bool protectFloat;
        public List<ControlType> protectedControls = new List<ControlType>();
        public List<string> namePatterns = new List<string>();
        public List<ParamOverride> overrides = new List<ParamOverride>();

        public static event Action Changed;

        private static CompressorExclusionSettings instance;

        public static CompressorExclusionSettings Instance {
            get {
                if (instance != null) return instance;
                var loaded = AssetDatabase.LoadAssetAtPath<CompressorExclusionSettings>(AssetPath);
                if (loaded != null) {
                    instance = loaded;
                    return instance;
                }

                // The asset can live elsewhere: older versions kept it inside the tool folder
                var existing = AssetDatabase.FindAssets($"t:{nameof(CompressorExclusionSettings)}")
                    .Select(AssetDatabase.GUIDToAssetPath)
                    .Select(AssetDatabase.LoadAssetAtPath<CompressorExclusionSettings>)
                    .FirstOrDefault(asset => asset != null);
                if (existing != null) {
                    instance = existing;
                    return instance;
                }

                var created = CreateInstance<CompressorExclusionSettings>();
                // If the file exists, the database just isn't ready yet. Don't overwrite the user's settings.
                if (File.Exists(AssetPath)) return created;
                Directory.CreateDirectory(Path.GetDirectoryName(AssetPath));
                AssetDatabase.CreateAsset(created, AssetPath);
                AssetDatabase.SaveAssets();
                instance = created;
                return instance;
            }
        }

        [InitializeOnLoadMethod]
        private static void Init() {
            EditorApplication.delayCall += () => { var _ = Instance; };
            Undo.undoRedoPerformed += () => {
                if (instance == null) return;
                AssetDatabase.SaveAssetIfDirty(instance);
                Changed?.Invoke();
            };
        }

        public void Mutate(string undoName, Action<CompressorExclusionSettings> change) {
            Undo.RecordObject(this, undoName);
            change(this);
            EditorUtility.SetDirty(this);
            AssetDatabase.SaveAssetIfDirty(this);
            Changed?.Invoke();
        }

        public ParamChoice GetChoice(string paramName) {
            var key = CompressorPolicy.Normalize(paramName);
            foreach (var entry in overrides) {
                if (entry.name == key) return entry.choice;
            }
            return ParamChoice.Auto;
        }

        public void SetChoice(IEnumerable<string> paramNames, ParamChoice choice) {
            Mutate("Change Compression Choice", s => {
                foreach (var paramName in paramNames) {
                    var key = CompressorPolicy.Normalize(paramName);
                    s.overrides.RemoveAll(entry => entry.name == key);
                    if (choice != ParamChoice.Auto) {
                        s.overrides.Add(new ParamOverride { name = key, choice = choice });
                    }
                }
            });
        }

        public string Fingerprint() {
            return Hash128.Compute(JsonUtility.ToJson(this)).ToString();
        }
    }
}

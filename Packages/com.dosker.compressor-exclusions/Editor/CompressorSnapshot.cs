using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace CompressorExclusions {
    [Serializable]
    internal class SnapshotParam {
        public string name;
        public int valueType;
        public int control = CompressorPolicy.NoControl;
        public bool addDriven;
        public bool compressed;
    }

    /**
     * What the last build of an avatar looked like. Params from VRCFury toggles and prefabs only exist
     * during a build, so this is the only complete list of parameters that the window can show.
     */
    [Serializable]
    internal class Snapshot {
        public string avatarName;
        public string vrcfuryVersion;
        public string builtAt;
        public string settingsFingerprint;
        public int originalCost;
        public int maxCost;
        public bool needsCompression;
        public bool hasResult;
        public int resultCompressed;
        public int resultNumberSlots;
        public int resultBoolSlots;
        public int resultBatches;
        public int resultCost;
        public List<SnapshotParam> parameters = new List<SnapshotParam>();
    }

    internal static class CompressorSnapshotStore {
        private const string Folder = "Library/CompressorExclusions";

        public static event Action Updated;

        private static Dictionary<string, int> controlLookup;

        public static List<string> AvatarNames() {
            if (!Directory.Exists(Folder)) return new List<string>();
            return Directory.GetFiles(Folder, "*.json")
                .Select(path => Load(Path.GetFileNameWithoutExtension(path))?.avatarName)
                .Where(name => name != null)
                .OrderBy(name => name)
                .ToList();
        }

        public static Snapshot Load(string avatarName) {
            var path = PathFor(avatarName);
            if (!File.Exists(path)) return null;
            try {
                return JsonUtility.FromJson<Snapshot>(File.ReadAllText(path));
            } catch (Exception e) {
                Debug.LogWarning($"[Compressor Exclusions] Could not read {path}: {e.Message}");
                return null;
            }
        }

        public static void Save(Snapshot snapshot) {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(PathFor(snapshot.avatarName), JsonUtility.ToJson(snapshot, true));
            controlLookup = null;
            Updated?.Invoke();
        }

        /**
         * The parameters inspector doesn't know which menu control uses a parameter, so it looks it up from past builds.
         */
        public static int ControlOf(string paramName) {
            if (controlLookup == null) {
                controlLookup = new Dictionary<string, int>();
                foreach (var avatar in AvatarNames()) {
                    foreach (var param in Load(avatar)?.parameters ?? new List<SnapshotParam>()) {
                        controlLookup[CompressorPolicy.Normalize(param.name)] = param.control;
                    }
                }
            }
            return controlLookup.TryGetValue(CompressorPolicy.Normalize(paramName), out var control)
                ? control
                : CompressorPolicy.NoControl;
        }

        private static string PathFor(string avatarName) {
            var fileName = string.Concat(avatarName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            return Path.Combine(Folder, fileName + ".json");
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace CompressorExclusions {
    [Serializable]
    internal class SnapshotParam {
        public string name;
        public int valueType;
        public int control = CompressorPolicy.NoControl;
        public bool addDriven;
        public bool compressed;
        // Zero in snapshots from older versions, which only knew the cost by type
        public int cost;
        public bool nonStandardEncoding;
        public string menuPath = "";
    }

    /**
     * What the last build of an avatar looked like. Params from VRCFury toggles and prefabs only exist
     * during a build, so this is the only complete list of parameters that the window can show.
     */
    [Serializable]
    internal class Snapshot {
        public string avatarName;
        // Empty in snapshots from older versions
        public string platform;
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
        // With the rules of this build, over the parameters of the previous one. This is what the window was showing.
        public Prediction predictionBefore = new Prediction();
        // With the rules and the parameters of this build, to tell a changed avatar from a changed solver
        public Prediction predictionAtBuild = new Prediction();

        public string Key => string.IsNullOrEmpty(platform) ? avatarName : $"{avatarName} · {platform}";
    }

    /**
     * Writes the snapshots to a file, for scripts and bug reports.
     * Usage: Unity -batchmode -quit -projectPath ... -executeMethod CompressorExclusions.CompressorReport.Export
     */
    public static class CompressorReport {
        private const string ReportPath = "Logs/CompressorExclusionsReport.json";

        [Serializable]
        private class Report {
            public string vrcfuryVersion;
            public string solverError;
            public string reportingError;
            public List<Snapshot> snapshots;
        }

        public static void Export() {
            var report = new Report {
                vrcfuryVersion = CompressorExclusionPatch.VrcfuryVersion,
                solverError = CompressorExclusionPatch.SolverError,
                reportingError = CompressorExclusionPatch.ReportingError,
                snapshots = CompressorSnapshotStore.Keys().Select(CompressorSnapshotStore.Load).ToList()
            };
            Directory.CreateDirectory(Path.GetDirectoryName(ReportPath));
            File.WriteAllText(ReportPath, JsonUtility.ToJson(report, true));
            Debug.Log($"[Compressor Exclusions] Report written to {ReportPath}");
        }
    }

    internal static class CompressorSnapshotStore {
        private const string Folder = "Library/CompressorExclusions";
        private const string HistoryFolder = Folder + "/History";
        private const int MaxHistory = 20;

        public static event Action Updated;

        private static Dictionary<string, SnapshotParam> paramLookup;

        // PC and Quest are built separately and can differ, so each one keeps its own snapshot
        public static string CurrentPlatform() {
            switch (EditorUserBuildSettings.activeBuildTarget) {
                case BuildTarget.Android: return "Quest";
                case BuildTarget.iOS: return "iOS";
                default: return "PC";
            }
        }

        public static List<string> Keys() {
            if (!Directory.Exists(Folder)) return new List<string>();
            return Directory.GetFiles(Folder, "*.json")
                .Select(path => Load(Path.GetFileNameWithoutExtension(path))?.Key)
                .Where(key => key != null)
                .OrderBy(key => key)
                .ToList();
        }

        public static Snapshot Load(string key) {
            return Read(PathFor(key));
        }

        private static Snapshot Read(string path) {
            if (!File.Exists(path)) return null;
            try {
                return JsonUtility.FromJson<Snapshot>(File.ReadAllText(path));
            } catch (Exception e) {
                Debug.LogWarning($"[Compressor Exclusions] Could not read {path}: {e.Message}");
                return null;
            }
        }

        public static void Save(Snapshot snapshot, bool finished = false) {
            Directory.CreateDirectory(Folder);
            var json = JsonUtility.ToJson(snapshot, true);
            File.WriteAllText(PathFor(snapshot.Key), json);
            if (finished) AddToHistory(snapshot.Key, json);
            paramLookup = null;
            Updated?.Invoke();
        }

        private static void AddToHistory(string key, string json) {
            var folder = HistoryFolderFor(key);
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, DateTime.Now.Ticks + ".json"), json);
            foreach (var old in Directory.GetFiles(folder, "*.json").OrderByDescending(path => path).Skip(MaxHistory)) {
                File.Delete(old);
            }
        }

        // Newest first
        public static List<Snapshot> History(string key) {
            var folder = HistoryFolderFor(key);
            if (!Directory.Exists(folder)) return new List<Snapshot>();
            return Directory.GetFiles(folder, "*.json")
                .OrderByDescending(path => path)
                .Select(Read)
                .Where(snapshot => snapshot != null)
                .ToList();
        }

        // The same avatar on the other platform. Both need the same compression to sync with each other.
        public static Snapshot Counterpart(Snapshot snapshot) {
            if (string.IsNullOrEmpty(snapshot.platform)) return null;
            var other = snapshot.platform == "PC" ? "Quest" : "PC";
            return Load(new Snapshot { avatarName = snapshot.avatarName, platform = other }.Key);
        }

        public static HashSet<string> KnownNames() {
            return new HashSet<string>(
                Keys().SelectMany(key => Load(key)?.parameters ?? new List<SnapshotParam>())
                    .Select(param => CompressorPolicy.Normalize(param.name))
            );
        }

        /**
         * The parameters inspector doesn't know which menu control uses a parameter, so it looks it up from past builds.
         */
        public static SnapshotParam Find(string paramName) {
            if (paramLookup == null) {
                paramLookup = new Dictionary<string, SnapshotParam>();
                foreach (var key in Keys()) {
                    foreach (var param in Load(key)?.parameters ?? new List<SnapshotParam>()) {
                        paramLookup[CompressorPolicy.Normalize(param.name)] = param;
                    }
                }
            }
            return paramLookup.TryGetValue(CompressorPolicy.Normalize(paramName), out var found) ? found : null;
        }

        private static string PathFor(string key) {
            return Path.Combine(Folder, FileNameFor(key) + ".json");
        }

        private static string HistoryFolderFor(string key) {
            return Path.Combine(HistoryFolder, FileNameFor(key));
        }

        private static string FileNameFor(string key) {
            return string.Concat(key.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        }
    }
}

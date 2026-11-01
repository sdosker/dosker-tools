using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using VRC.SDKBase.Editor.BuildPipeline;

namespace DoskerTools.PerformanceBudget
{
    internal static class BudgetStore
    {
        private const string Folder = "Library/PerformanceBudget";

        public static event Action Updated;

        public static bool IsMobileBuild
        {
            get { return EditorUserBuildSettings.activeBuildTarget == BuildTarget.Android || EditorUserBuildSettings.activeBuildTarget == BuildTarget.iOS; }
        }

        public static string PlatformLabel(bool mobile)
        {
            return mobile ? "Quest" : "PC";
        }

        public static BudgetCalibration Load(string avatarName, bool mobile)
        {
            var path = PathFor(avatarName, PlatformLabel(mobile));
            if (!File.Exists(path))
            {
                return null;
            }

            try
            {
                return JsonUtility.FromJson<BudgetCalibration>(File.ReadAllText(path));
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"[Performance Budget] Could not read {path}: {exception.Message}");
                return null;
            }
        }

        public static void Save(BudgetCalibration calibration)
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(PathFor(calibration.avatarName, calibration.platform), JsonUtility.ToJson(calibration, true));
            Updated?.Invoke();
        }

        private static string PathFor(string avatarName, string platform)
        {
            var name = string.Concat($"{avatarName} {platform}".Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            return Path.Combine(Folder, name + ".json");
        }
    }

    // Só lê. Uma falha aqui nunca pode impedir o build: ela apenas fica sem calibração.
    internal static class BudgetBuildCapture
    {
        private static BudgetCalibration pending;

        public static void Before(GameObject avatar)
        {
            pending = null;
            try
            {
                var mobile = BudgetStore.IsMobileBuild;
                pending = new BudgetCalibration
                {
                    avatarName = avatar.name.Replace("(Clone)", "").Trim(),
                    platform = BudgetStore.PlatformLabel(mobile),
                    builtAt = DateTime.Now.ToString("dd/MM/yyyy HH:mm"),
                    componentsFingerprint = BudgetProbe.ComponentsFingerprint(avatar),
                    before = BudgetProbe.Measure(avatar, mobile)
                };
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"[Performance Budget] Could not measure the avatar before the build: {exception.Message}");
            }
        }

        public static void After(GameObject avatar)
        {
            if (pending == null)
            {
                return;
            }

            try
            {
                pending.after = BudgetProbe.Measure(avatar, BudgetStore.IsMobileBuild);
                BudgetStore.Save(pending);
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"[Performance Budget] Could not measure the avatar after the build: {exception.Message}");
            }
            pending = null;
        }
    }

    public sealed class PerformanceBudgetBeforeBuild : IVRCSDKPreprocessAvatarCallback
    {
        public int callbackOrder
        {
            get { return int.MinValue; }
        }

        public bool OnPreprocessAvatar(GameObject avatarGameObject)
        {
            BudgetBuildCapture.Before(avatarGameObject);
            return true;
        }
    }

    public sealed class PerformanceBudgetAfterBuild : IVRCSDKPreprocessAvatarCallback
    {
        public int callbackOrder
        {
            get { return int.MaxValue; }
        }

        public bool OnPreprocessAvatar(GameObject avatarGameObject)
        {
            BudgetBuildCapture.After(avatarGameObject);
            return true;
        }
    }
}

using System;
using System.IO;
using UnityEngine;

namespace DoskerTools.EditorUi
{
    // Preferências de uma janela em UserSettings: ficam fora da cena, do Git e do pacote.
    public static class DoskerSettings<T> where T : class, new()
    {
        public static T Load(string fileName)
        {
            var path = PathFor(fileName);
            if (!File.Exists(path))
            {
                return new T();
            }

            try
            {
                return JsonUtility.FromJson<T>(File.ReadAllText(path)) ?? new T();
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"[Dosker Tools] Could not read {path}: {exception.Message}");
                return new T();
            }
        }

        public static void Save(string fileName, T settings)
        {
            var path = PathFor(fileName);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, JsonUtility.ToJson(settings, true));
        }

        private static string PathFor(string fileName)
        {
            return Path.Combine("UserSettings", fileName + ".json");
        }
    }
}

using UnityEditor;
using UnityEngine;

namespace DoskerTools.EditorUi
{
    public enum DoskerLanguage
    {
        Automatico,
        Portugues,
        English
    }

    // Tradução direta no ponto de uso: o texto em português segue legível no código e não existe
    // arquivo de chaves para ficar fora de sincronia.
    public static class DoskerLocale
    {
        public const string IssuesUrl = "https://github.com/sdosker/dosker-tools/issues";

        private const string PreferenceKey = "DoskerTools.Language";

        public static DoskerLanguage Language
        {
            get { return (DoskerLanguage)EditorPrefs.GetInt(PreferenceKey, (int)DoskerLanguage.Automatico); }
            set { EditorPrefs.SetInt(PreferenceKey, (int)value); }
        }

        public static bool IsPortuguese
        {
            get
            {
                var language = Language;
                return language == DoskerLanguage.Automatico
                    ? Application.systemLanguage == SystemLanguage.Portuguese
                    : language == DoskerLanguage.Portugues;
            }
        }

        public static string Tr(string portuguese, string english)
        {
            return IsPortuguese ? portuguese : english;
        }
    }
}

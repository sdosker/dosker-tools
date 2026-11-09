using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace DoskerTools.EditorUi
{
    // Os valores ficam fixos porque são gravados em EditorPrefs.
    public enum DoskerLanguage
    {
        Automatic = 0,
        Portuguese = 1,
        English = 2
    }

    // Tradução direta no ponto de uso: o texto em português segue legível no código e não existe
    // arquivo de chaves para ficar fora de sincronia.
    //
    // A troca de idioma fica em lugares que não exigem ler português: o menu
    // Tools > Dosker Tools > Language, Preferences > Dosker Tools e o botão com o globo nas janelas.
    // Os nomes dos idiomas aparecem sempre no próprio idioma.
    public static class DoskerLocale
    {
        public const string IssuesUrl = "https://github.com/sdosker/dosker-tools/issues";

        // Ícone embutido do Editor com um globo, usado nos botões de idioma.
        public const string GlobeIcon = "BuildSettings.Web.Small";

        private const string PreferenceKey = "DoskerTools.Language";
        private const string MenuRoot = "Tools/Dosker Tools/Language/";
        private const string MenuAutomatic = MenuRoot + "Automatic (system language)";
        private const string MenuEnglish = MenuRoot + "English";
        private const string MenuPortuguese = MenuRoot + "Português";
        private const int MenuPriority = 1000;

        // Avisa as janelas que guardam texto pronto (UI Toolkit, títulos de aba) para refazerem a interface.
        public static event Action Changed;

        private static DoskerLanguage? cachedLanguage;

        public static DoskerLanguage Language
        {
            get
            {
                if (!cachedLanguage.HasValue)
                {
                    var stored = EditorPrefs.GetInt(PreferenceKey, (int)DoskerLanguage.Automatic);
                    cachedLanguage = Enum.IsDefined(typeof(DoskerLanguage), stored) ? (DoskerLanguage)stored : DoskerLanguage.Automatic;
                }
                return cachedLanguage.Value;
            }
            set
            {
                if (value == Language)
                {
                    return;
                }
                cachedLanguage = value;
                EditorPrefs.SetInt(PreferenceKey, (int)value);
                Changed?.Invoke();
                InternalEditorUtility.RepaintAllViews();
            }
        }

        public static bool IsPortuguese
        {
            get
            {
                var language = Language;
                return language == DoskerLanguage.Automatic
                    ? Application.systemLanguage == SystemLanguage.Portuguese
                    : language == DoskerLanguage.Portuguese;
            }
        }

        // Sigla do idioma em uso, para o botão compacto.
        public static string ShortCode
        {
            get { return IsPortuguese ? "PT" : "EN"; }
        }

        public static string Tr(string portuguese, string english)
        {
            return IsPortuguese ? portuguese : english;
        }

        public static string DisplayName(DoskerLanguage language)
        {
            switch (language)
            {
                case DoskerLanguage.English: return "English";
                case DoskerLanguage.Portuguese: return "Português";
                default:
                    var resolved = Application.systemLanguage == SystemLanguage.Portuguese ? "Português" : "English";
                    return Tr("Automático", "Automatic") + " (" + resolved + ")";
            }
        }

        // Menu com os três idiomas, usado pelo botão com o globo e por quem quiser abrir em outro lugar.
        public static void ShowMenu()
        {
            if (Event.current != null)
            {
                BuildMenu().ShowAsContext();
            }
            else
            {
                BuildMenu().DropDown(new Rect(0f, 0f, 0f, 0f));
            }
        }

        // Para UI Toolkit, onde Event.current pode ser nulo: abre abaixo do botão (use worldBound).
        public static void ShowMenu(Rect anchor)
        {
            BuildMenu().DropDown(anchor);
        }

        private static GenericMenu BuildMenu()
        {
            var menu = new GenericMenu();
            foreach (DoskerLanguage language in Enum.GetValues(typeof(DoskerLanguage)))
            {
                var captured = language;
                menu.AddItem(new GUIContent(DisplayName(language)), Language == language, () => Language = captured);
            }
            menu.AddSeparator("");
            menu.AddItem(new GUIContent(Tr("Preferências do Dosker Tools...", "Dosker Tools Preferences...")), false,
                () => SettingsService.OpenUserPreferences(PreferencesPath));
            return menu;
        }

        [MenuItem(MenuAutomatic, false, MenuPriority)]
        private static void UseAutomatic()
        {
            Language = DoskerLanguage.Automatic;
        }

        [MenuItem(MenuAutomatic, true)]
        private static bool UseAutomaticCheck()
        {
            Menu.SetChecked(MenuAutomatic, Language == DoskerLanguage.Automatic);
            return true;
        }

        [MenuItem(MenuEnglish, false, MenuPriority + 1)]
        private static void UseEnglish()
        {
            Language = DoskerLanguage.English;
        }

        [MenuItem(MenuEnglish, true)]
        private static bool UseEnglishCheck()
        {
            Menu.SetChecked(MenuEnglish, Language == DoskerLanguage.English);
            return true;
        }

        [MenuItem(MenuPortuguese, false, MenuPriority + 2)]
        private static void UsePortuguese()
        {
            Language = DoskerLanguage.Portuguese;
        }

        [MenuItem(MenuPortuguese, true)]
        private static bool UsePortugueseCheck()
        {
            Menu.SetChecked(MenuPortuguese, Language == DoskerLanguage.Portuguese);
            return true;
        }

        public const string PreferencesPath = "Preferences/Dosker Tools";

        [SettingsProvider]
        private static SettingsProvider CreatePreferences()
        {
            return new SettingsProvider(PreferencesPath, SettingsScope.User)
            {
                label = "Dosker Tools",
                keywords = new HashSet<string> { "Dosker", "Language", "Idioma", "English", "Português", "Portuguese" },
                guiHandler = _ =>
                {
                    EditorGUIUtility.labelWidth = 200f;
                    var options = (DoskerLanguage[])Enum.GetValues(typeof(DoskerLanguage));
                    var names = Array.ConvertAll(options, DisplayName);
                    var selected = Array.IndexOf(options, Language);
                    // Rótulo nos dois idiomas: quem abriu esta página pode não ler o idioma atual.
                    var picked = EditorGUILayout.Popup("Language / Idioma", selected, names);
                    if (picked != selected)
                    {
                        Language = options[picked];
                    }
                    EditorGUILayout.HelpBox(
                        Tr("Vale para todas as janelas do Dosker Tools. Automático segue o idioma do sistema.",
                           "Applies to every Dosker Tools window. Automatic follows the system language."),
                        MessageType.None);
                }
            };
        }
    }
}

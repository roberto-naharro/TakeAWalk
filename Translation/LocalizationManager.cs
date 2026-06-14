using System;
using System.Collections.Generic;
using System.IO;
using ColossalFramework.Globalization;
using TakeAWalk.Util;

namespace TakeAWalk.Translation
{
    // Loads every language file from the mod's Locale folder and serves translations
    // for the game's current language, falling back to English when no match exists.
    public class LocalizationManager
    {
        private readonly List<ILanguage> _languages = new List<ILanguage>();
        private ILanguage _currentLanguage;
        private ILanguage _fallback;
        private bool _languagesLoaded;
        private readonly string _fallbackLanguage;
        private readonly ILanguageDeserializer _deserializer;
        private readonly Type _modType;

        public LocalizationManager(Type modType, ILanguageDeserializer deserializer, string fallbackLanguage = "en")
        {
            _modType = modType;
            _deserializer = deserializer;
            _fallbackLanguage = fallbackLanguage;
            LocaleManager.eventLocaleChanged += SetCurrentLanguage;
        }

        private void SetCurrentLanguage()
        {
            if (_languages.Count == 0 || !LocaleManager.exists)
                return;

            _fallback = _languages.Find(l => l.LocaleName() == _fallbackLanguage);
            _currentLanguage =
                _languages.Find(l => l.LocaleName() == LocaleManager.instance.language) ??
                _fallback;
        }

        private void LoadLanguages()
        {
            if (_languagesLoaded) return;
            _languagesLoaded = true;
            RefreshLanguages();
            SetCurrentLanguage();
        }

        public void RefreshLanguages()
        {
            _languages.Clear();

            string basePath;
            try
            {
                basePath = TranslationUtil.AssemblyPath(_modType);
            }
            catch (Exception e)
            {
                Log.Error("Could not resolve mod path for localization: " + e.Message);
                return;
            }

            string languagePath = basePath + Path.DirectorySeparatorChar + "Locale";
            if (!Directory.Exists(languagePath))
            {
                Log.Warning("No Locale folder found at " + languagePath);
                return;
            }

            foreach (string file in Directory.GetFiles(languagePath, "*.txt"))
            {
                try
                {
                    ILanguage lang = _deserializer.DeserialiseLanguage(file);
                    if (lang != null) _languages.Add(lang);
                }
                catch (Exception e)
                {
                    Log.Error("Error deserializing language file " + file + ": " + e);
                }
            }
        }

        // Returns the translation for id, or id itself when no translation exists
        // (so missing keys stay readable in the UI rather than showing blank).
        public string GetTranslation(string translationId)
        {
            LoadLanguages();
            if (translationId == null) return "null";

            if (_currentLanguage != null && _currentLanguage.HasTranslation(translationId))
                return _currentLanguage.GetTranslation(translationId);

            // Per-key fallback to the fallback language (English) so a key that a translation
            // file hasn't caught up on yet stays readable instead of showing the raw id.
            if (_fallback != null && _fallback.HasTranslation(translationId))
                return _fallback.GetTranslation(translationId);

            return translationId;
        }
    }
}

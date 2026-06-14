using TakeAWalk.Translation;

namespace TakeAWalk
{
    // Static facade for translations. Use Localization.Get("KEY") in UI code.
    public static class Localization
    {
        private static readonly LocalizationManager Manager =
            new LocalizationManager(typeof(TakeAWalkMod), new PlainTextLanguageDeserializer());

        public static string Get(string translationId)
        {
            return Manager.GetTranslation(translationId);
        }

        public static string Get(string translationId, params object[] args)
        {
            return string.Format(Manager.GetTranslation(translationId), args);
        }
    }
}

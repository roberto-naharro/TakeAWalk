namespace TakeAWalk.Translation
{
    public interface ILanguage
    {
        bool HasTranslation(string id);

        string GetTranslation(string id);

        string LocaleName();
    }

    public interface ILanguageDeserializer
    {
        ILanguage DeserialiseLanguage(string fileName);
    }
}

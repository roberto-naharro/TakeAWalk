using System;
using System.Collections.Generic;

namespace TakeAWalk.Translation
{
    // A single loaded language backed by a "KEY value" dictionary.
    public class LanguageDictionaryWrapper : ILanguage
    {
        private readonly string _localeName;
        private readonly Dictionary<string, string> _dictionary;

        public LanguageDictionaryWrapper(string localeName, Dictionary<string, string> dictionary)
        {
            if (localeName == null) throw new ArgumentNullException("localeName");
            if (dictionary == null) throw new ArgumentNullException("dictionary");
            _localeName = localeName;
            _dictionary = dictionary;
        }

        public bool HasTranslation(string id) => _dictionary.ContainsKey(id);

        public string GetTranslation(string id) => _dictionary[id];

        public string LocaleName() => _localeName;
    }
}

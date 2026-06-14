using System.Collections.Generic;
using System.IO;
using TakeAWalk.Util;

namespace TakeAWalk.Translation
{
    // Reads "KEY value" plain-text language files; the locale name is the file
    // name without extension (e.g. en.txt -> "en"). "\n" sequences become newlines.
    public class PlainTextLanguageDeserializer : ILanguageDeserializer
    {
        public ILanguage DeserialiseLanguage(string fileName)
        {
            var fileInfo = new FileInfo(fileName);
            var localeName = fileInfo.Name.Replace(".txt", "");
            Log.DebugLog("Loading localization file: " + fileName + " (locale: " + localeName + ")");
            return new LanguageDictionaryWrapper(localeName, Load(fileName));
        }

        private static Dictionary<string, string> Load(string path)
        {
            var dictionary = new Dictionary<string, string>();
            if (!File.Exists(path))
            {
                Log.Warning("Localization file does not exist: " + path);
                return dictionary;
            }

            foreach (string line in File.ReadAllLines(path))
            {
                if (line == null) continue;
                string str = line.Trim();
                if (str.Length == 0) continue;

                int split = str.IndexOf(' ');
                if (split > 0 && !dictionary.ContainsKey(str.Substring(0, split)))
                {
                    dictionary.Add(
                        str.Substring(0, split),
                        str.Substring(split + 1).Replace("\\n", "\n"));
                }
            }
            return dictionary;
        }
    }
}

using System;
using System.Linq;
using ColossalFramework.Plugins;
using ICities;

namespace TakeAWalk.Translation
{
    public static class TranslationUtil
    {
        // Filesystem path of the mod's own plugin folder (where Locale/ lives).
        public static string AssemblyPath(Type modType)
        {
            return PluginInfo(modType).modPath;
        }

        private static PluginManager.PluginInfo PluginInfo(Type modType)
        {
            var plugins = PluginManager.instance.GetPluginsInfo();
            try
            {
                foreach (var item in plugins)
                {
                    try
                    {
                        var instances = item.GetInstances<IUserMod>();
                        if (modType != instances.FirstOrDefault()?.GetType())
                            continue;
                        return item;
                    }
                    catch
                    {
                        // ignore plugins that fail to instantiate
                    }
                }
            }
            catch (Exception e)
            {
                throw new Exception("Failed to find assembly of type " + modType, e);
            }
            throw new Exception("Failed to find assembly of type " + modType);
        }
    }
}

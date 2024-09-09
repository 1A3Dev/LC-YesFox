using System;
using LobbyCompatibility.Enums;
using LobbyCompatibility.Features;

namespace YesFox.Compatibility
{
    internal class LobbyCompatibility
    {
        public static void Init(int versionNum)
        {
            Plugin.logSource.LogWarning("LobbyCompatibility detected, registering plugin with LobbyCompatibility.");

            Version pluginVersion = Version.Parse(MyPluginInfo.PLUGIN_VERSION);

            PluginHelper.RegisterPlugin(MyPluginInfo.PLUGIN_GUID, pluginVersion, versionNum >= 64 ? CompatibilityLevel.Everyone : CompatibilityLevel.ServerOnly, VersionStrictness.Minor);
        }
    }
}

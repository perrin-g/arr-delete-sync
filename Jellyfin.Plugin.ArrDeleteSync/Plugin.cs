using System;
using Jellyfin.Plugin.ArrDeleteSync.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.ArrDeleteSync;

public class Plugin : BasePlugin<PluginConfiguration>
{
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public override string Name => "ArrDeleteSync";

    public override Guid Id => Guid.Parse("8f2c1e4a-6b3d-4a9e-9c1a-1a2b3c4d5e6f");

    public static Plugin? Instance { get; private set; }
}

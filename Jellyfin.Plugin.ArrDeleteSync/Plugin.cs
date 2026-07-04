using System;
using System.Collections.Generic;
using System.IO;
using Jellyfin.Plugin.ArrDeleteSync.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.AspNetCore.DataProtection;

namespace Jellyfin.Plugin.ArrDeleteSync;

public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;

        // The Data Protection key ring is deliberately stored in a directory SEPARATE from
        // PluginConfiguration.xml (which holds the encrypted keys) — co-locating them would put
        // the decryption key next to the data it protects, defeating the point against a raw
        // filesystem/backup read. Verify this path resolves correctly inside the Jellyfin
        // container during Task 13's staging pass.
        var keyRingPath = Path.Combine(applicationPaths.DataPath, "arrdeletesync-keyring");
        Directory.CreateDirectory(keyRingPath);
        var dataProtectionProvider = DataProtectionProvider.Create(new DirectoryInfo(keyRingPath));
        KeyProtector = new Services.ApiKeyProtector(dataProtectionProvider);
    }

    public override string Name => "ArrDeleteSync";

    public override Guid Id => Guid.Parse("8f2c1e4a-6b3d-4a9e-9c1a-1a2b3c4d5e6f");

    public static Plugin? Instance { get; private set; }

    public Services.IApiKeyProtector KeyProtector { get; private set; } = null!;

    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = string.Format("{0}.Configuration.configPage.html", GetType().Namespace)
            },
            new PluginPageInfo
            {
                Name = "deleteManager",
                EmbeddedResourcePath = string.Format("{0}.Web.deleteManager.html", GetType().Namespace),
                DisplayName = "Delete Manager",
                EnableInMainMenu = true,
                MenuSection = "server",
                MenuIcon = "delete_sweep"
            },
            // Registered separately, keyed by the exact literal string used in
            // deleteManager.html's <script src="deleteManager.js">. Jellyfin's dashboard fetches
            // an embedded page's script tags by resolving their src against a page Name, not a
            // filesystem path — PluginPageInfo has no dedicated "associated script" member (see
            // Task 12 report for the reflection that confirmed this), so an external .js file
            // needs its own entry whose Name matches the <script> src verbatim.
            new PluginPageInfo
            {
                Name = "deleteManager.js",
                EmbeddedResourcePath = string.Format("{0}.Web.deleteManager.js", GetType().Namespace)
            }
        };
    }

    // BasePlugin<PluginConfiguration>.UpdateConfiguration(BasePluginConfiguration) is the
    // exact hook the Jellyfin dashboard's "save configuration" API call invokes: it is passed
    // the freshly-deserialized PluginConfiguration from the incoming JSON body, and the base
    // implementation assigns it to `Configuration` and immediately persists it to
    // PluginConfiguration.xml via IXmlSerializer.SerializeToFile (verified empirically against
    // the installed Jellyfin.Common 10.11.11 package with a throwaway reflection/instantiation
    // probe — not against decompiled source). Intercepting here, before calling base, lets us
    // encrypt any freshly-submitted plaintext key and blank out the plaintext field so it is
    // never the value written to disk.
    public override void UpdateConfiguration(BasePluginConfiguration configuration)
    {
        if (configuration is PluginConfiguration config)
        {
            if (!string.IsNullOrEmpty(config.RadarrApiKeyPlaintext))
            {
                config.RadarrApiKeyEncrypted = KeyProtector.Protect(config.RadarrApiKeyPlaintext);
                config.RadarrApiKeyPlaintext = string.Empty;
            }

            if (!string.IsNullOrEmpty(config.SonarrApiKeyPlaintext))
            {
                config.SonarrApiKeyEncrypted = KeyProtector.Protect(config.SonarrApiKeyPlaintext);
                config.SonarrApiKeyPlaintext = string.Empty;
            }

            if (!string.IsNullOrEmpty(config.SeerrApiKeyPlaintext))
            {
                config.SeerrApiKeyEncrypted = KeyProtector.Protect(config.SeerrApiKeyPlaintext);
                config.SeerrApiKeyPlaintext = string.Empty;
            }
        }

        base.UpdateConfiguration(configuration);
    }
}

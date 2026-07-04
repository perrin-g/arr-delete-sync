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
            // jellyfin-web's findBestConfigurationPage() (client-side) resolves the "Settings"
            // link on Dashboard -> Plugins by preferring whichever of this plugin's registered
            // pages has EnableInMainMenu set, when more than one page exists for the same plugin
            // ID — it does NOT match by Name. Without EnableInMainMenu here, that link incorrectly
            // resolved to the Delete Manager page below once it gained the flag. Setting it on
            // BOTH pages (this one is listed first, so Array.prototype.find in that client code
            // returns this one first among menu-enabled candidates) fixes routing AND gives the
            // settings page its own persistent sidebar entry, which is reasonable for a plugin an
            // admin needs to revisit for config, not just deletes.
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = string.Format("{0}.Configuration.configPage.html", GetType().Namespace),
                DisplayName = "ArrDeleteSync Settings",
                EnableInMainMenu = true,
                MenuSection = "server",
                MenuIcon = "settings"
            },
            // deleteManager.html's script is inlined directly in the page (not a separate
            // <script src> file) — an earlier version used an external deleteManager.js with its
            // own PluginPageInfo entry, keyed by the literal <script src> string, on the theory
            // that Jellyfin resolves an embedded page's script tags that way. Live staging
            // verification found the real bug: jellyfin-web's page loader
            // (viewContainer.js/normalizeNewView/parseHtml) extracts ONLY the
            // div[data-role="page"] subtree from the fetched HTML — any <script> sibling AFTER
            // that div's closing tag, inline or external, is silently discarded and never reaches
            // the live DOM at all, so nothing in either page's script ever ran. Fixed by nesting
            // the script INSIDE the page div (both here and in configPage.html) and inlining
            // deleteManager's script entirely, removing the external-file/second-PluginPageInfo
            // workaround altogether since it's no longer needed.
            new PluginPageInfo
            {
                Name = "deleteManager",
                EmbeddedResourcePath = string.Format("{0}.Web.deleteManager.html", GetType().Namespace),
                DisplayName = "Delete Manager",
                EnableInMainMenu = true,
                MenuSection = "server",
                MenuIcon = "delete_sweep"
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

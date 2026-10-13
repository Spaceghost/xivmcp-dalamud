using XivMcp.Core;

namespace XivMcp.Plugin.Providers.DalamudInfo;

/// <summary>
/// Fail-closed identity matching for mutations, unlike the best-effort name lookups used for display.
/// ExposedPlugin.Manifest is the exact LocalPlugin.Manifest object on the supported Dalamud build.
/// </summary>
internal static class PluginControlIdentity
{
    public static InternalPlugin Resolve(IReadOnlyList<InternalPlugin> plugins, string internalName, bool isDev, object? manifest)
    {
        ArgumentNullException.ThrowIfNull(plugins);
        var matches = manifest is null ? [] : plugins.Where(p =>
            ReferenceEquals(p.ManifestIdentity, manifest) && p.IsDev == isDev &&
            string.Equals(p.InternalName, internalName, StringComparison.OrdinalIgnoreCase)).ToList();
        return matches.Count == 1 ? matches[0] : throw McpToolException.WithCode(McpErrorCodes.Unavailable,
            $"The selected installation of {internalName} could not be matched uniquely to Dalamud's plugin manager ({matches.Count} exact matches). Nothing was changed. Refresh list_plugins or use the plugin installer.");
    }

    public static InternalPlugin Refresh(IReadOnlyList<InternalPlugin> plugins, InternalPlugin selected)
    {
        ArgumentNullException.ThrowIfNull(plugins);
        ArgumentNullException.ThrowIfNull(selected);
        // ReloadManifest can replace the manifest, but LocalPlugin itself survives ReloadAsync.
        // Never switch to a sibling install that happened to become loaded while the operation ran.
        var matches = plugins.Where(p => ReferenceEquals(p.Instance, selected.Instance) &&
            p.WorkingPluginId == selected.WorkingPluginId && p.IsDev == selected.IsDev &&
            string.Equals(p.InternalName, selected.InternalName, StringComparison.OrdinalIgnoreCase)).ToList();
        return matches.Count == 1 ? matches[0] : throw McpToolException.WithCode(McpErrorCodes.Unavailable,
            $"The operation on {selected.InternalName} may have completed, but its exact installation could not be read afterwards ({matches.Count} exact matches). Inspect the plugin installer before retrying; a sibling installation's state is not a result for this operation.");
    }
}

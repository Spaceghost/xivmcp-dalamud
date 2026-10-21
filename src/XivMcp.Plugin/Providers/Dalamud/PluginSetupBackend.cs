using System.Text;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using XivMcp.Core;

namespace XivMcp.Plugin.Providers.DalamudInfo;

/// <summary>Reads/plans without mutations; delegates writes and installation to reviewed native installer methods.</summary>
internal sealed class PluginSetupBackend(IGameThread game, string ownInternalName, IPluginLog log) : IPluginSetupBackend, IDisposable
{
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public async Task<PluginSetupPlan> PlanAsync(PluginSetupRequest request, CancellationToken token)
    {
        var host = await game.InvokeAsync(() => (Api: DalamudInternals.EnsureSetupAvailable(), Installed: DalamudInternals.ReadSetupInstalled()), token).ConfigureAwait(false);
        using var response = await http.GetAsync(request.RepositoryUrl, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var bytes = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await source.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        {
            if (bytes.Length + count > PluginSetupPlanner.MaxCatalogBytes) throw McpToolException.WithCode(McpErrorCodes.Unavailable, "Repository catalogue exceeds 2 MiB.");
            bytes.Write(buffer, 0, count);
        }
        return PluginSetupPlanner.Build(request, Encoding.UTF8.GetString(bytes.GetBuffer(), 0, (int)bytes.Length), host.Installed, host.Api, ownInternalName);
    }

    public async Task ConfigureAsync(PluginSetupPlan plan, CancellationToken token)
    {
        await game.InvokeAsync(() => DalamudInternals.ConfigureSetupRepository(plan), token).ConfigureAwait(false);
        // Like the installer: constructors/loaders may wait on framework ticks.
        // Do not abandon a running native operation on cancellation; stop before the next one.
        await Task.Run(DalamudInternals.RefreshSetupRepositoriesAsync, CancellationToken.None).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        await game.InvokeAsync(() =>
        {
            foreach (var item in plan.Plugins) DalamudInternals.ResolveSetupManifest(plan, item);
        }, token).ConfigureAwait(false);
    }

    public async Task ApplyAsync(PluginSetupPlan plan, PluginSetupPlanItem item, CancellationToken token)
    {
        var resolved = await game.InvokeAsync(() =>
        {
            var manifest = DalamudInternals.ResolveSetupManifest(plan, item);
            var current = DalamudInternals.ReadSetupInstalled();
            // Re-evaluate identity/state/version immediately before any install or unload.
            // Another local installer must not turn a preflight result into an overwrite.
            var package = System.Text.Json.JsonSerializer.Serialize(new[] { new Dictionary<string, object?>
            {
                ["InternalName"] = item.Package.InternalName, ["AssemblyVersion"] = item.Package.Version,
                ["DalamudApiLevel"] = item.Package.ApiLevel, ["DownloadLinkInstall"] = item.Package.DownloadUrl,
            } });
            var recheck = PluginSetupPlanner.Build(new(plan.RepositoryUrl, "stable", [item.Package.InternalName]), package, current,
                item.Package.ApiLevel, ownInternalName).Plugins.Single();
            var installed = DalamudInternals.GetPlugins()!.SingleOrDefault(p => p.InternalName.Equals(item.Package.InternalName, StringComparison.OrdinalIgnoreCase));
            return (Manifest: manifest, Installed: installed, Action: recheck.Action);
        }, token).ConfigureAwait(false);

        if (resolved.Action is "install" or "update")
        {
            log.Information("Plugin setup {Action}: {Plugin} {Version}", resolved.Action, item.Package.InternalName, item.Package.Version);
            await Task.Run(() => DalamudInternals.ApplySetupPackageAsync(resolved.Manifest, item.Package.UseTesting, resolved.Installed, resolved.Action == "update"), CancellationToken.None).ConfigureAwait(false);
        }
        token.ThrowIfCancellationRequested();
        var enable = await game.InvokeAsync(() =>
        {
            var p = DalamudInternals.GetPlugins()?.SingleOrDefault(p => p.InternalName.Equals(item.Package.InternalName, StringComparison.OrdinalIgnoreCase))
                ?? throw McpToolException.WithCode(McpErrorCodes.Unavailable, "Installed plugin was not registered after setup.");
            if (p.IsLoaded) return (Plugin: p, Profile: (object?)null);
            var context = DalamudInternals.GetToggleContext(p)
                ?? throw McpToolException.WithCode(McpErrorCodes.Unavailable, "Default collection state is unavailable.");
            PluginToggleRules.EnsureCanSetEnabled(new(p.InternalName, p.State, p.IsLoaded, p.IsDev, p.IsOutdated, p.IsBanned, p.IsOrphaned,
                context.SafeMode, context.ProfilesBusy, context.WantingProfiles, context.SingleProfileIsDefault, context.SingleProfileEnabled), true, ownInternalName);
            return (Plugin: p, Profile: context.SingleProfile);
        }, token).ConfigureAwait(false);
        if (enable.Profile is not null)
            await Task.Run(() => DalamudInternals.SetEnabledAsync(enable.Plugin, enable.Profile, true), CancellationToken.None).ConfigureAwait(false);
    }

    public Task<PluginSetupVerification> VerifyAsync(PluginSetupPlanItem item, CancellationToken token)
        => game.InvokeAsync(() => DalamudInternals.VerifySetup(item.Package.InternalName), token);

    public void Dispose() => http.Dispose();
}

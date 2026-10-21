using System.Text.Json;
using XivMcp.Core;

namespace XivMcp.Plugin.Providers.DalamudInfo;

public sealed record PluginSetupRequest(string RepositoryUrl, string Channel, IReadOnlyList<string> Plugins)
{
    public const string DefaultRepository = "https://spacegho.st/mods/ffxiv/plugins.json";

    public static PluginSetupRequest Parse(string[] plugins, string repositoryUrl, string channel)
    {
        var url = new Uri(RepositoryUrlValidator.Validate(repositoryUrl)).AbsoluteUri;
        if (new Uri(url).Fragment.Length > 0) throw Invalid("Repository URLs must not contain a fragment.");
        if (channel is not ("stable" or "testing")) throw Invalid("channel must be stable or testing. Development packages need their own explicit repository URL.");
        if (plugins is null || plugins.Length is < 1 or > 12) throw Invalid("Choose between 1 and 12 plugin internal names.");
        var names = plugins.Select(static n => n?.Trim() ?? "").ToArray();
        if (names.Any(static n => !ValidName(n))) throw Invalid("Use exact plugin internal names (letters, digits, underscores, dots and hyphens; up to 120 characters).");
        if (names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Length) throw Invalid("Plugin internal names must not be duplicated.");
        return new(url, channel, names);
    }

    public static bool ValidName(string name) => name.Length is > 0 and <= 120 &&
        name[0] is not ('.' or '-') && name.All(static c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-') && !name.Contains("..", StringComparison.Ordinal);

    private static McpToolException Invalid(string text) => McpToolException.WithCode(McpErrorCodes.InvalidArguments, text);
}

public sealed record PluginSetupCandidate(string InternalName, string Version, int ApiLevel, string DownloadUrl,
    bool UseTesting, IReadOnlyList<string> Dependencies);

public sealed record PluginSetupInstalled(string InternalName, string Version, bool Loaded, bool IsDev, bool Banned,
    bool Outdated, string State, string SourceUrl, bool DefaultCollectionOnly);

public sealed record PluginSetupPlanItem(PluginSetupCandidate Package, string Action, string? InstalledVersion);
public sealed record PluginSetupPlan(string RepositoryUrl, string Channel, IReadOnlyList<PluginSetupPlanItem> Plugins,
    bool EnableTesting, string Note);
public sealed record PluginSetupVerification(string InternalName, string Version, bool Loaded, string State, bool Enabled);

/// <summary>Read-only planning from a bounded repository document. No Dalamud services or constructors.</summary>
public static class PluginSetupPlanner
{
    public const int MaxCatalogBytes = 2 * 1024 * 1024;

    public static PluginSetupPlan Build(PluginSetupRequest request, string catalog, IReadOnlyList<PluginSetupInstalled> installed,
        int apiLevel, string ownInternalName)
    {
        if (catalog.Length > MaxCatalogBytes) throw Invalid("Repository catalogue exceeds its size limit.");
        using var doc = JsonDocument.Parse(catalog, new JsonDocumentOptions { MaxDepth = 32 });
        if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() > 2048)
            throw Invalid("Repository must be a pluginmaster JSON array of at most 2048 manifests.");
        var entries = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in doc.RootElement.EnumerateArray())
        {
            var name = String(entry, "InternalName");
            if (!PluginSetupRequest.ValidName(name)) throw Invalid("Repository contains an invalid internal name.");
            if (!entries.TryAdd(name, entry)) throw Invalid($"Repository contains ambiguous duplicate manifests for {name}.");
        }

        var ordered = new List<PluginSetupPlanItem>();
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in request.Plugins) Visit(name);
        return new(request.RepositoryUrl, request.Channel, ordered, ordered.Any(static p => p.Package.UseTesting),
            "Dependencies declared as XivMcpDependencies are installed first from this same repository. Bundled DLL dependencies are handled by Dalamud's package installer. Existing settings are preserved; no plugin UI clicks are needed.");

        void Visit(string name)
        {
            if (visited.Contains(name)) return;
            if (string.Equals(name, ownInternalName, StringComparison.OrdinalIgnoreCase))
                throw Invalid("This running MCP server cannot replace itself during setup. Its host must deploy the MCP server build.");
            if (!visiting.Add(name)) throw Invalid("Plugin setup dependencies contain a cycle.");
            if (visiting.Count + visited.Count > 24) throw Invalid("A setup may include at most 24 plugins including dependencies.");
            if (!entries.TryGetValue(name, out var entry)) throw Invalid($"{name} is not published in the selected repository. No repository or plugin has been changed.");
            var package = Candidate(entry, request.Channel, apiLevel);
            foreach (var dependency in package.Dependencies) Visit(dependency);
            var copies = installed.Where(p => string.Equals(p.InternalName, name, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (copies.Length > 1 || copies.Any(static p => p.IsDev)) throw Invalid($"{name} has a development or duplicate installation; setup preserves it instead of replacing or loading a sibling.");
            var current = copies.SingleOrDefault();
            if (current is not null)
            {
                if (current.Banned || current.State is "Loading" or "Unloading" or "UnloadError") throw Invalid($"{name} cannot be changed in state {current.State}, or is banned.");
                if (!current.DefaultCollectionOnly) throw Invalid($"{name} is managed by another plugin collection; setup will not rewrite that collection.");
                if (!string.Equals(current.SourceUrl, request.RepositoryUrl, StringComparison.Ordinal)) throw Invalid($"{name} belongs to a different repository; setup will not replace its source.");
            }
            var needsUpdate = current is not null && Version.Parse(current.Version) < Version.Parse(package.Version);
            if (current?.Outdated == true && !needsUpdate) throw Invalid($"{name} is outdated and this repository has no newer compatible package.");
            var action = current is null ? "install" : needsUpdate ? "update" : current.Loaded ? "unchanged" : "enable";
            ordered.Add(new(package, action, current?.Version));
            visiting.Remove(name); visited.Add(name);
        }
    }

    public static PluginSetupCandidate Candidate(JsonElement manifest, string channel, int apiLevel)
    {
        var name = String(manifest, "InternalName");
        var exclusive = manifest.TryGetProperty("IsTestingExclusive", out var ex) && ex.ValueKind == JsonValueKind.True;
        var testingVersion = String(manifest, "TestingAssemblyVersion");
        var useTesting = channel == "testing" && testingVersion.Length > 0 && String(manifest, "DownloadLinkTesting").Length > 0;
        if (exclusive && !useTesting) throw Invalid($"{name} requires its testing channel and a published testing package.");
        var version = useTesting ? testingVersion : String(manifest, "AssemblyVersion");
        if (!Version.TryParse(version, out _)) throw Invalid($"{name} has no valid package version for this channel.");
        var api = manifest.TryGetProperty(useTesting ? "TestingDalamudApiLevel" : "DalamudApiLevel", out var level) && level.ValueKind == JsonValueKind.Number && level.TryGetInt32(out var value)
            ? value : useTesting && manifest.TryGetProperty("DalamudApiLevel", out level) && level.ValueKind == JsonValueKind.Number && level.TryGetInt32(out value) ? value : -1;
        if (api != apiLevel) throw Invalid($"{name} targets Dalamud API {api}; this host requires {apiLevel}.");
        var url = RepositoryUrlValidator.Validate(String(manifest, useTesting ? "DownloadLinkTesting" : "DownloadLinkInstall"));
        var dependencies = new List<string>();
        if (manifest.TryGetProperty("XivMcpDependencies", out var declared))
        {
            if (declared.ValueKind != JsonValueKind.Array || declared.GetArrayLength() > 12) throw Invalid($"{name} has an invalid dependency list.");
            foreach (var item in declared.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String || !PluginSetupRequest.ValidName(item.GetString()!)) throw Invalid($"{name} declares an invalid dependency.");
                dependencies.Add(item.GetString()!);
            }
        }
        return new(name, version, api, url, useTesting, dependencies);
    }

    private static string String(JsonElement item, string name) => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    private static McpToolException Invalid(string text) => McpToolException.WithCode(McpErrorCodes.Unavailable, text);
}

internal interface IPluginSetupBackend
{
    Task<PluginSetupPlan> PlanAsync(PluginSetupRequest request, CancellationToken token);
    Task ConfigureAsync(PluginSetupPlan plan, CancellationToken token);
    Task ApplyAsync(PluginSetupPlan plan, PluginSetupPlanItem item, CancellationToken token);
    Task<PluginSetupVerification> VerifyAsync(PluginSetupPlanItem item, CancellationToken token);
}

public sealed record PluginSetupOperation(string Id, string State, string Phase, PluginSetupPlan? Plan,
    IReadOnlyList<PluginSetupVerification> Verified, string? Error, DateTimeOffset UpdatedAt);

/// <summary>One owner-authorized setup at a time, bounded status history and honest partial results.</summary>
internal sealed class PluginSetupService(IPluginSetupBackend backend, Action<Exception>? onError = null) : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly Dictionary<string, PluginSetupOperation> operations = new(StringComparer.Ordinal);
    private readonly Queue<string> history = new();
    private Task? active;
    private string? activeId, activeKey;
    private bool disposed;

    internal Task WhenIdle { get { lock (gate) return active ?? Task.CompletedTask; } }

    public PluginSetupOperation Start(PluginSetupRequest request)
    {
        var key = JsonSerializer.Serialize(request);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (active is { IsCompleted: false })
            {
                if (key == activeKey) return operations[activeId!];
                throw McpToolException.WithCode(McpErrorCodes.Unavailable, "Another plugin setup is still running; poll its result before starting another.", retryable: true);
            }
            var id = Guid.NewGuid().ToString("N");
            var operation = new PluginSetupOperation(id, "queued", "preflight", null, [], null, DateTimeOffset.UtcNow);
            operations.Add(id, operation); history.Enqueue(id);
            while (history.Count > 32) operations.Remove(history.Dequeue());
            activeId = id; activeKey = key;
            active = Task.Run(() => RunAsync(id, request, lifetime.Token));
            return operation;
        }
    }

    public PluginSetupOperation Read(string id)
    {
        lock (gate) return operations.TryGetValue(id, out var result) ? result
            : throw McpToolException.WithCode(McpErrorCodes.NotFound, "Unknown or expired setup operation. Status history lasts until MCP reload.");
    }

    private void Change(string id, Func<PluginSetupOperation, PluginSetupOperation> update)
    {
        lock (gate) operations[id] = update(operations[id]) with { UpdatedAt = DateTimeOffset.UtcNow };
    }

    private async Task RunAsync(string id, PluginSetupRequest request, CancellationToken token)
    {
        try
        {
            Change(id, static o => o with { State = "running" });
            var plan = await backend.PlanAsync(request, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            Change(id, o => o with { Plan = plan, Phase = "configure_repository" });
            await backend.ConfigureAsync(plan, token).ConfigureAwait(false);
            foreach (var item in plan.Plugins)
            {
                token.ThrowIfCancellationRequested();
                Change(id, o => o with { Phase = item.Action + ":" + item.Package.InternalName });
                await backend.ApplyAsync(plan, item, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                var actual = await backend.VerifyAsync(item, token).ConfigureAwait(false);
                Change(id, o => o with { Verified = o.Verified.Append(actual).ToArray() });
                if (!actual.Loaded || !actual.Enabled || !Version.TryParse(actual.Version, out var version) || version < Version.Parse(item.Package.Version))
                    throw McpToolException.WithCode(McpErrorCodes.Unavailable, $"{item.Package.InternalName} did not reach the requested loaded version (observed {actual.State}, {actual.Version}). Setup stopped; inspect the recorded partial results.");
            }
            Change(id, static o => o with { State = "complete", Phase = "verified" });
        }
        catch (OperationCanceledException)
        {
            Change(id, static o => o with { State = "cancelled", Error = "MCP is unloading. A native installation already in progress may have completed; inspect installed state before retrying." });
        }
        catch (Exception error)
        {
            onError?.Invoke(error);
            var message = error is McpToolException ? error.Message : "Plugin setup failed. Inspect the plugin log and recorded partial results before retrying.";
            Change(id, o => o with { State = "failed", Error = message });
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task? running;
        lock (gate) { if (disposed) return; disposed = true; lifetime.Cancel(); running = active; }
        if (running is not null) await running.ConfigureAwait(false);
        lifetime.Dispose();
    }
}

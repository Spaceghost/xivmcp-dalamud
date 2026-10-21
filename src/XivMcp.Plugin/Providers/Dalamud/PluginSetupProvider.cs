using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using XivMcp.Core;

namespace XivMcp.Plugin.Providers.DalamudInfo;

[McpProvider("dalamud")]
public sealed class PluginSetupProvider : IAsyncDisposable
{
    private readonly IPluginSetupBackend backend;
    private readonly PluginSetupService service;

    public PluginSetupProvider(IDalamudPluginInterface pi, IGameThread game, IPluginLog log)
    {
        backend = new PluginSetupBackend(game, pi.InternalName, log);
        service = new(backend, error => log.Error(error, "Automatic plugin setup failed."));
    }

    internal PluginSetupProvider(IPluginSetupBackend backend, Action<Exception>? onError = null)
    {
        this.backend = backend; service = new(backend, onError);
    }

    [McpTool("get_plugin_setup_plan", Title = "Plan automatic plugin setup", RequiresLogin = false, GameThread = false,
        Sources = ["http:pluginmaster", "dalamud:internal.PluginManager", "dalamud:internal.ProfileManager"],
        Description = "Read a repository catalogue and installed state to plan installation, updates and enabling of exact plugin internal names, including same-repository XivMcpDependencies. No repository, testing setting, profile, plugin or window is changed. Defaults to our published FFXIV repository. channel testing uses a published testing package when present, otherwise stable; stable refuses testing-exclusive plugins. Development builds need an explicitly published development repository URL. Bundled DLL dependencies are supplied by packages. Refuses duplicate/dev installations, source replacement, collection conflicts, incompatible API versions and replacing the running MCP server. Use setup_plugins to apply and get_plugin_setup_status to poll.")]
    public Task<PluginSetupPlan> Plan(
        [McpParam("Exact plugin internal names, 1-12 unique entries.")] string[] plugins,
        [McpParam("HTTPS pluginmaster URL. New plugins in this repository need no new MCP code.")] string repositoryUrl = PluginSetupRequest.DefaultRepository,
        [McpParam("Explicit release channel.", Enum = ["stable", "testing"])] string channel = "testing",
        ToolContext? ctx = null)
        => backend.PlanAsync(PluginSetupRequest.Parse(plugins, repositoryUrl, channel), ctx?.CancellationToken ?? CancellationToken.None);

    [McpTool("setup_plugins", Title = "Set up plugins automatically", RequiresLogin = false, GameThread = false,
        Permission = ToolPermission.Action, RequiresApproval = true, Destructive = true, Idempotent = true,
        Sources = ["http:pluginmaster", "dalamud:internal.DalamudConfiguration", "dalamud:internal.PluginManager", "dalamud:internal.ProfileManager"],
        Description = "Automatically register/enable the selected HTTPS repository, enable testing when requested packages require it, install or update exact named plugins and their declared same-repository dependencies, enable them in the default collection and verify loaded versions. No installer/settings UI, clipboard or manual clicks. This downloads code that runs inside the game and may unload an older plugin during update. Existing plugin settings are preserved. Preflights the complete set before changes, then delegates to Dalamud's installer and eligibility checks. Returns an operation id immediately: queued is not success. Poll get_plugin_setup_status for verified complete or failed with partial results. One setup runs at a time; identical in-flight requests reuse the operation. History is bounded to 32 operations until reload. Missing unpublished packages, dev/duplicate installations, source/collection conflicts, incompatible or banned plugins and self replacement are explicit failures. A native operation already started may finish during MCP shutdown; inspect installed state before retrying.",
        ApprovalSummary = "Download, install/update and enable plugins {plugins}, including declared dependencies, from {repositoryUrl} using channel {channel}; register this repository and enable testing if needed. Plugin code will run in the game.")]
    public PluginSetupOperation Setup(
        [McpParam("Exact plugin internal names, 1-12 unique entries.")] string[] plugins,
        [McpParam("Explicit HTTPS pluginmaster repository URL.")] string repositoryUrl = PluginSetupRequest.DefaultRepository,
        [McpParam("Explicit release channel.", Enum = ["stable", "testing"])] string channel = "testing")
        => service.Start(PluginSetupRequest.Parse(plugins, repositoryUrl, channel));

    [McpTool("get_plugin_setup_status", Title = "Read plugin setup progress", RequiresLogin = false, GameThread = false,
        Sources = ["xivmcp:plugin-setup-operations"],
        Description = "Read one automatic plugin setup operation by id. Includes queued/running/complete/failed/cancelled, current phase, planned packages, individually observed loaded versions, enabled state and partial failure details. Complete means every planned plugin was verified loaded at least at its planned version. A queued/running request is not success; a failed operation may already have registered a repository or installed earlier packages. Status history retains at most 32 operations until MCP reload. This read never starts or retries setup.")]
    public PluginSetupOperation Status([McpParam("Operation id from setup_plugins.")] string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw McpToolException.WithCode(McpErrorCodes.InvalidArguments, "id must be the 32-digit setup operation id.");
        return service.Read(id);
    }

    public async ValueTask DisposeAsync()
    {
        await service.DisposeAsync().ConfigureAwait(false);
        if (backend is IDisposable disposable) disposable.Dispose();
    }
}

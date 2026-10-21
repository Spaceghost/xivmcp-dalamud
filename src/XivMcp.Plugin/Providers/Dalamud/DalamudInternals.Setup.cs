using System.Collections;
using System.Reflection;
using Dalamud.Plugin;
using Newtonsoft.Json;
using XivMcp.Core;

namespace XivMcp.Plugin.Providers.DalamudInfo;

internal static partial class DalamudInternals
{
    private const string RemoteManifest = "Dalamud.Plugin.Internal.Types.Manifest.RemotePluginManifest";
    private const string Manifest = "Dalamud.Plugin.Internal.Types.PluginManifest";
    private const string LocalManifest = "Dalamud.Plugin.Internal.Types.Manifest.LocalPluginManifest";
    private const string Repository = "Dalamud.Plugin.Internal.Types.PluginRepository";
    private const string TestingOptIn = "Dalamud.Configuration.Internal.PluginTestingOptIn";

    private static IReadOnlyList<DalamudInternalName> SetupNames =>
    [
        new("Configuration.DoPluginTest", Configuration, "DoPluginTest"),
        new("Configuration.PluginTestingOptIns", Configuration, "PluginTestingOptIns"),
        new("TestingOptIn.New", TestingOptIn, ".ctor", Parameters: ["String"]),
        new("TestingOptIn.InternalName", TestingOptIn, "InternalName"),
        new("Configuration.QueueSave", Configuration, "QueueSave", Parameters: []),
        new("RepoSettings.New", RepoSettings, ".ctor", Parameters: []),
        new("PluginManager.DalamudApiLevel", PluginManager, "DalamudApiLevel", IsStatic: true),
        new("PluginManager.Repos", PluginManager, "Repos"),
        new("PluginManager.SetPluginReposFromConfigAsync", PluginManager, "SetPluginReposFromConfigAsync", Parameters: ["Boolean"]),
        new("PluginManager.IsManifestEligible", PluginManager, "IsManifestEligible", Parameters: ["RemotePluginManifest"]),
        new("PluginManager.InstallPluginAsync", PluginManager, "InstallPluginAsync", Parameters: ["RemotePluginManifest", "Boolean", "PluginLoadReason"]),
        new("PluginManager.UpdateSinglePluginAsync", PluginManager, "UpdateSinglePluginAsync", Parameters: ["AvailablePluginUpdate", "Boolean", "Boolean"]),
        new("ProfileManager.DefaultProfile", ProfileManager, "DefaultProfile"),
        new("RemoteManifest", RemoteManifest),
        new("RemoteManifest.SourceRepo", RemoteManifest, "SourceRepo"),
        new("Repository.PluginMasterUrl", Repository, "PluginMasterUrl"),
        new("Repository.PluginMaster", Repository, "PluginMaster"),
        new("Repository.State", Repository, "State"),
        new("Manifest.InternalName", Manifest, "InternalName"),
        new("LocalManifest.InstalledFromUrl", LocalManifest, "InstalledFromUrl"),
        new("LocalPlugin.EffectiveVersion", LocalPlugin, "EffectiveVersion"),
        new("AvailableUpdate.New", AvailableUpdate, ".ctor", Parameters: ["LocalPlugin", "RemotePluginManifest", "Boolean"]),
    ];

    public static string[] SetupKeys => [.. ToggleKeys, .. RepositoryKeys, .. SetupNames.Select(static n => n.Key)];

    internal static int EnsureSetupAvailable()
    {
        if (FirstMissing(SetupKeys) is { } missing) throw SetupUnavailable("This Dalamud build lacks the reviewed setup binding: " + missing);
        var manager = RequireService("PluginManager");
        var profiles = RequireService("ProfileManager");
        if (Get("PluginManager.SafeMode", manager) is not false) throw SetupUnavailable("Dalamud is in safe mode; automatic setup cannot load plugins.");
        if (Get("ProfileManager.IsBusy", profiles) is not false) throw SetupUnavailable("Dalamud is applying plugin collections; retry setup when it is idle.");
        var profile = Get("ProfileManager.DefaultProfile", profiles);
        if (profile is null || Get("Profile.IsEnabled", profile) is not true) throw SetupUnavailable("The default plugin collection is disabled; setup preserves that choice.");
        return Get("PluginManager.DalamudApiLevel", null) as int? ?? throw SetupUnavailable("Dalamud API level is unavailable.");
    }

    internal static IReadOnlyList<PluginSetupInstalled> ReadSetupInstalled()
    {
        EnsureSetupAvailable();
        return (GetPlugins() ?? throw SetupUnavailable("Installed plugin state is unavailable.")).Select(p =>
        {
            var context = GetToggleContext(p);
            return new PluginSetupInstalled(p.InternalName, Get("LocalPlugin.EffectiveVersion", p.Instance)?.ToString() ?? "0.0.0.0",
                p.IsLoaded, p.IsDev, p.IsBanned, p.IsOutdated, p.State,
                p.ManifestIdentity is null ? "" : Get("LocalManifest.InstalledFromUrl", p.ManifestIdentity) as string ?? "",
                context is { WantingProfiles: 1, SingleProfileIsDefault: true, SingleProfileEnabled: true });
        }).ToArray();
    }

    /// <summary>Framework thread. Uses the same configuration objects and save queue as Dalamud settings.</summary>
    internal static void ConfigureSetupRepository(PluginSetupPlan plan)
    {
        EnsureSetupAvailable();
        var config = RequireService("Configuration");
        if (Get("Configuration.ThirdRepoList", config) is not IList list) throw SetupUnavailable("Custom repository list is unavailable.");
        var matches = list.Cast<object>().Where(r => string.Equals(Get("RepoSettings.Url", r) as string, plan.RepositoryUrl, StringComparison.Ordinal)).ToArray();
        if (matches.Length > 1) throw SetupUnavailable("The selected repository appears more than once in Dalamud settings.");
        var changed = false;
        if (matches.Length == 0)
        {
            var repo = Construct("RepoSettings.New");
            SetSetup("RepoSettings.Url", repo, plan.RepositoryUrl); SetSetup("RepoSettings.IsEnabled", repo, true);
            list.Add(repo); changed = true;
        }
        else if (Get("RepoSettings.IsEnabled", matches[0]) is not true)
        {
            SetSetup("RepoSettings.IsEnabled", matches[0], true); changed = true;
        }
        if (plan.EnableTesting && Get("Configuration.DoPluginTest", config) is not true)
        { SetSetup("Configuration.DoPluginTest", config, true); changed = true; }
        if (Get("Configuration.PluginTestingOptIns", config) is not IList optIns) throw SetupUnavailable("Testing preferences are unavailable.");
        foreach (var item in plan.Plugins)
        {
            var preferences = optIns.Cast<object>().Where(p => string.Equals(Get("TestingOptIn.InternalName", p) as string, item.Package.InternalName, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (item.Package.UseTesting && preferences.Length == 0)
            { optIns.Add(Construct("TestingOptIn.New", item.Package.InternalName)); changed = true; }
            else if (!item.Package.UseTesting && preferences.Length > 0)
            { foreach (var preference in preferences) optIns.Remove(preference); changed = true; }
        }
        if (changed) Call("Configuration.QueueSave", config);
    }

    internal static Task RefreshSetupRepositoriesAsync()
        => AwaitCall("PluginManager.SetPluginReposFromConfigAsync", RequireService("PluginManager"), true);

    internal static string SetupRepositoryState(string url)
    {
        if (Get("PluginManager.Repos", RequireService("PluginManager")) is not IEnumerable repositories) return "Unavailable";
        var repo = repositories.Cast<object>().SingleOrDefault(r => string.Equals(Get("Repository.PluginMasterUrl", r) as string, url, StringComparison.Ordinal));
        return repo is null ? "Missing" : Get("Repository.State", repo)?.ToString() ?? "Unavailable";
    }

    /// <summary>Framework thread. Resolve from the exact native repository, including native filtering/bans.</summary>
    internal static object ResolveSetupManifest(PluginSetupPlan plan, PluginSetupPlanItem item)
    {
        EnsureSetupAvailable();
        var manager = RequireService("PluginManager");
        if (Get("PluginManager.Repos", manager) is not IEnumerable repositories) throw SetupUnavailable("Native repositories are unavailable.");
        var repo = repositories.Cast<object>().SingleOrDefault(r => string.Equals(Get("Repository.PluginMasterUrl", r) as string, plan.RepositoryUrl, StringComparison.Ordinal))
            ?? throw SetupUnavailable("The requested repository was not registered by Dalamud.");
        if (Get("Repository.State", repo)?.ToString() != "Success" || Get("Repository.PluginMaster", repo) is not IEnumerable manifests)
            throw SetupUnavailable("Dalamud could not load this repository. Its configuration may have been added; inspect repository state before retrying.");
        var manifest = manifests.Cast<object>().SingleOrDefault(m => string.Equals(Get("Manifest.InternalName", m) as string, item.Package.InternalName, StringComparison.OrdinalIgnoreCase))
            ?? throw SetupUnavailable(item.Package.InternalName + " was not accepted by Dalamud's repository validation.");
        if (Call("PluginManager.IsManifestEligible", manager, manifest) is not true) throw SetupUnavailable(item.Package.InternalName + " is not eligible under Dalamud's installation policy.");
        using var json = System.Text.Json.JsonDocument.Parse(JsonConvert.SerializeObject(manifest));
        var native = PluginSetupPlanner.Candidate(json.RootElement, plan.Channel, item.Package.ApiLevel);
        if (native.Version != item.Package.Version || native.DownloadUrl != item.Package.DownloadUrl || native.UseTesting != item.Package.UseTesting)
            throw SetupUnavailable("Repository contents changed after preflight; rerun setup to plan the current packages.");
        return manifest;
    }

    internal static async Task ApplySetupPackageAsync(object manifest, bool testing, InternalPlugin? installed, bool update)
    {
        var manager = RequireService("PluginManager");
        if (installed is null)
            await AwaitCall("PluginManager.InstallPluginAsync", manager, manifest, testing, PluginLoadReason.Installer).ConfigureAwait(false);
        else if (update)
            await AwaitCall("PluginManager.UpdateSinglePluginAsync", manager, Construct("AvailableUpdate.New", installed.Instance, manifest, testing), true, false).ConfigureAwait(false);
    }

    internal static PluginSetupVerification VerifySetup(string name)
    {
        var copies = (GetPlugins() ?? throw SetupUnavailable("Installed state is unavailable.")).Where(p => p.InternalName.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (copies.Length != 1 || copies[0].IsDev) throw SetupUnavailable("Setup cannot verify a unique repository installation of " + name + ".");
        var p = copies[0]; var context = GetToggleContext(p);
        var enabled = context is { WantingProfiles: 1, SingleProfileIsDefault: true, SingleProfileEnabled: true } &&
            Call("Profile.WantsPlugin", context.SingleProfile, p.WorkingPluginId) is true;
        return new(p.InternalName, Get("LocalPlugin.EffectiveVersion", p.Instance)?.ToString() ?? "0.0.0.0", p.IsLoaded, p.State, enabled);
    }

    private static object RequireService(string key) => Service(key) ?? throw SetupUnavailable("Dalamud's " + key + " service is unavailable.");
    private static object Construct(string key, params object?[] arguments)
        => (Lookup(key) as ConstructorInfo)?.Invoke(arguments) ?? throw SetupUnavailable("Missing setup constructor: " + key);
    private static void SetSetup(string key, object instance, object value)
    {
        if (Lookup(key) is not PropertyInfo property || property.GetSetMethod(true) is null) throw SetupUnavailable("Missing setup setter: " + key);
        property.SetValue(instance, value);
    }
    private static McpToolException SetupUnavailable(string text) => McpToolException.WithCode(McpErrorCodes.Unavailable, text);
}

using System.Reflection;
using XivMcp.Core;
using XivMcp.Plugin.Providers.DalamudInfo;

namespace XivMcp.Plugin.Tests;

public class PluginControlIdentityTests
{
    [Theory]
    [InlineData(nameof(DalamudControlProvider.ReloadPlugin))]
    [InlineData(nameof(DalamudControlProvider.SetPluginEnabled))]
    public void MutationsKeepTheirActionPermissionAndOffThreadExecution(string methodName)
    {
        var attribute = typeof(DalamudControlProvider).GetMethod(methodName)!.GetCustomAttribute<McpToolAttribute>();
        Assert.NotNull(attribute);
        Assert.Equal(ToolPermission.Action, attribute.Permission);
        Assert.False(attribute.GameThread);
        Assert.False(attribute.RequiresLogin);
        Assert.False(string.IsNullOrWhiteSpace(attribute.ApprovalSummary));
    }

    private sealed record ManifestMarker(int Value);

    private static InternalPlugin Plugin(bool isDev = true, bool loaded = true) =>
        new(new object(), "XivWayfinder", "Wayfinder", loaded ? "Loaded" : "Unloaded", loaded,
            isDev, false, false, false, Guid.NewGuid(), new ManifestMarker(1));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ExactManifestSelectsTheChosenCopyRegardlessOfListOrder(bool devFirst)
    {
        var dev = Plugin();
        var repo = Plugin(isDev: false, loaded: false);
        InternalPlugin[] plugins = devFirst ? [dev, repo] : [repo, dev];
        Assert.Same(dev, PluginControlIdentity.Resolve(plugins, dev.InternalName, dev.IsDev, dev.ManifestIdentity));
        Assert.Same(repo, PluginControlIdentity.Resolve(plugins, repo.InternalName, repo.IsDev, repo.ManifestIdentity));
    }

    [Fact]
    public void SameKindAndSameValueManifestDoesNotSubstituteForExactIdentity()
    {
        var selected = Plugin();
        var sibling = Plugin();
        Assert.Equal(selected.ManifestIdentity, sibling.ManifestIdentity);
        Assert.NotSame(selected.ManifestIdentity, sibling.ManifestIdentity);
        Assert.Same(selected, PluginControlIdentity.Resolve([sibling, selected], selected.InternalName, true, selected.ManifestIdentity));
        AssertUnavailable(() => PluginControlIdentity.Resolve([sibling], selected.InternalName, true, selected.ManifestIdentity));
    }

    [Fact]
    public void NullManifestAndEmptyListCannotMatch()
    {
        var selected = Plugin() with { ManifestIdentity = null };
        AssertUnavailable(() => PluginControlIdentity.Resolve([selected], selected.InternalName, true, null));
        AssertUnavailable(() => PluginControlIdentity.Resolve([], selected.InternalName, true, new object()));
    }

    [Fact]
    public void DuplicateExactIdentityFailsClosed()
    {
        var selected = Plugin();
        var collision = Plugin() with { ManifestIdentity = selected.ManifestIdentity };
        AssertUnavailable(() => PluginControlIdentity.Resolve([selected, collision], selected.InternalName, true, selected.ManifestIdentity));
    }

    [Fact]
    public void NameAndKindMustStillMatchTheSelectedPublicPlugin()
    {
        var selected = Plugin();
        AssertUnavailable(() => PluginControlIdentity.Resolve([selected], "SomeOtherPlugin", true, selected.ManifestIdentity));
        AssertUnavailable(() => PluginControlIdentity.Resolve([selected], selected.InternalName, false, selected.ManifestIdentity));
        Assert.Same(selected, PluginControlIdentity.Resolve([selected], "xivwayfinder", true, selected.ManifestIdentity));
    }

    [Fact]
    public void EmptyWorkingGuidDoesNotBreakManifestIdentity()
    {
        var selected = Plugin() with { WorkingPluginId = Guid.Empty };
        var sibling = Plugin(isDev: false, loaded: false) with { WorkingPluginId = Guid.Empty };
        Assert.Same(selected, PluginControlIdentity.Resolve([sibling, selected], selected.InternalName, true, selected.ManifestIdentity));
    }

    [Fact]
    public void ManifestReplacedBeforeResolveRequiresARefreshNotAFallback()
    {
        var selected = Plugin();
        var refreshed = selected with { ManifestIdentity = new ManifestMarker(1) };
        AssertUnavailable(() => PluginControlIdentity.Resolve([refreshed], selected.InternalName, true, selected.ManifestIdentity));
    }

    [Fact]
    public void PostActionSnapshotFollowsTheSelectedInstanceNotTheLoadedSibling()
    {
        var selected = Plugin();
        var unloaded = selected with { IsLoaded = false, State = "Unloaded" };
        var sibling = Plugin(isDev: false, loaded: true);
        var after = PluginControlIdentity.Refresh([sibling, unloaded], selected);
        Assert.Same(unloaded, after);
        Assert.False(after.IsLoaded);
    }

    [Theory]
    [InlineData("Loaded", true)]
    [InlineData("LoadError", false)]
    [InlineData("UnloadError", false)]
    public void ReloadMayReplaceTheManifestButKeepsItsOwnState(string state, bool loaded)
    {
        var selected = Plugin();
        var afterReload = selected with { ManifestIdentity = new ManifestMarker(2), State = state, IsLoaded = loaded };
        var sibling = Plugin(isDev: false);
        var after = PluginControlIdentity.Refresh([sibling, afterReload], selected);
        Assert.Same(afterReload, after);
        Assert.Equal(state, after.State);
        Assert.Equal(loaded, after.IsLoaded);
    }

    [Fact]
    public void MissingPostActionInstanceDoesNotReportItsSiblingAsSuccess()
    {
        var selected = Plugin();
        var replacement = selected with { Instance = new object() }; // even a matching ID is not the same object
        var error = AssertUnavailable(() => PluginControlIdentity.Refresh([replacement], selected));
        Assert.Contains("may have completed", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PostActionIdentityCollisionsOrChangedIdsAreRefused()
    {
        var selected = Plugin();
        AssertUnavailable(() => PluginControlIdentity.Refresh([selected, selected], selected));
        AssertUnavailable(() => PluginControlIdentity.Refresh([selected with { WorkingPluginId = Guid.NewGuid() }], selected));
        AssertUnavailable(() => PluginControlIdentity.Refresh([selected with { InternalName = "Other" }], selected));
        AssertUnavailable(() => PluginControlIdentity.Refresh([selected with { IsDev = false }], selected));
    }

    private static McpToolException AssertUnavailable(Action action)
    {
        var error = Assert.Throws<McpToolException>(action);
        Assert.Equal(McpErrorCodes.Unavailable, error.Code);
        return error;
    }
}

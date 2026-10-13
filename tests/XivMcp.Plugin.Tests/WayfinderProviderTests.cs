using System.Reflection;
using XivMcp.Core;
using XivMcp.Plugin.Providers.World;

namespace XivMcp.Plugin.Tests;

public sealed class WayfinderProviderTests
{
    [Theory]
    [InlineData(nameof(WayfinderProvider.AppendStops))]
    [InlineData(nameof(WayfinderProvider.Control))]
    public void JourneyEditsRequireApprovalAndRunOnFramework(string name)
    {
        var tool = typeof(WayfinderProvider).GetMethod(name)!.GetCustomAttribute<McpToolAttribute>()!;
        Assert.True(tool.GameThread);
        Assert.True(tool.NeedsApproval);
        Assert.False(tool.Idempotent);
        Assert.Equal(ToolPermission.Action, tool.Permission);
    }

    [Fact]
    public void InvalidRequestsFailBeforeContactingOptionalPlugin()
    {
        var provider = new WayfinderProvider(null!);
        Assert.Throws<McpToolException>(() => provider.AppendStops(""));
        Assert.Throws<McpToolException>(() => provider.AppendStops(new string('x', 32769)));
        Assert.Throws<McpToolException>(() => provider.Control("teleport"));
    }

    [Fact]
    public void MissingPluginBecomesAnActionableError()
    {
        var error = Assert.Throws<McpToolException>(() => new WayfinderProvider(null!).GetJourney());
        Assert.Contains("Wayfinder", error.Message, StringComparison.Ordinal);
    }
}

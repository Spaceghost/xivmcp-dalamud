using System.Text.Json;
using XivMcp.Core;
using XivMcp.Plugin.Bridges;
using XivMcp.Plugin.Providers.Bridges;
using XivMcp.Plugin.Tests.Infrastructure;

namespace XivMcp.Plugin.Tests;

public class HudBridgeTests
{
    private static readonly string[] ReadMethods = ["api.version", "status"];
    private static FakeBridgeInvoker Fake(string module, string[]? methods = null)
    {
        var fake = new FakeBridgeInvoker();
        fake.Gates[HudBridge.Gate(module)] = input =>
        {
            using var doc = JsonDocument.Parse(input!);
            return doc.RootElement.GetProperty("method").GetString() switch
            {
                "api.version" => JsonSerializer.Serialize(new { ok = true, result = new { version = 1, methods = methods ?? ["window.close", "window.mode"] } }),
                "status" => JsonSerializer.Serialize(new { ok = true, result = new { version = 1, module, state = new { open = true, actualMode = "screen", hostingStatus = "Ghostty unavailable" } } }),
                _ => "{\"ok\":true,\"result\":{\"requestId\":\"abc\",\"state\":\"queued\"}}",
            };
        };
        return fake;
    }

    [Fact]
    public void FamilyDiscoveryKeepsHealthyModulesWhenOthersAreMissingAndNeverChangesUi()
    {
        var fake = Fake("journal");
        var family = new HudProvider(new HudBridge(fake)).Family();
        Assert.Null(family.Modules.Single(m => m.Module == "journal").Error);
        Assert.NotNull(family.Modules.Single(m => m.Module == "character").Error);
        Assert.NotNull(family.Modules.Single(m => m.Module == "hud").Error);
        Assert.All(fake.Calls, c => Assert.Contains(JsonDocument.Parse(c.Argument!).RootElement.GetProperty("method").GetString(), ReadMethods));
    }

    [Fact]
    public void ScreenRecoveryIsExplicitAndQueuedRatherThanClaimingItDisplayed()
    {
        var fake = Fake("journal");
        var provider = new HudProvider(new HudBridge(fake));
        var result = provider.Control("journal", "mode", "screen");
        Assert.Equal("queued", result.GetProperty("state").GetString());
        var command = JsonDocument.Parse(fake.Calls.Last().Argument!).RootElement;
        Assert.Equal("window.mode", command.GetProperty("method").GetString());
        Assert.Equal("screen", command.GetProperty("params").GetProperty("mode").GetString());
        Assert.False(command.GetProperty("params").TryGetProperty("placement", out _));
    }

    [Theory]
    [InlineData("hud", "open", "", "")]
    [InlineData("journal", "toggle", "", "")]
    [InlineData("character", "mode", "screen", "Hud")]
    [InlineData("character", "close", "world", "")]
    [InlineData("journal", "mode", "world", "Unknown")]
    public void BadActionsAreRejectedBeforeAnyIpc(string module, string action, string mode, string placement)
    {
        var fake = Fake("journal");
        Assert.Throws<McpToolException>(() => new HudProvider(new HudBridge(fake)).Control(module, action, mode, placement));
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public void MissingCapabilitiesNeverInvokeTheChange()
    {
        var fake = Fake("journal", []);
        Assert.Throws<McpToolException>(() => new HudProvider(new HudBridge(fake)).Control("journal", "close"));
        Assert.Single(fake.Calls);
    }

    [Theory]
    [InlineData("{\"ok\":true,\"result\":{\"version\":2}}")]
    [InlineData("{\"ok\":false,\"error\":\"unloaded\"}")]
    [InlineData("[]")]
    [InlineData("not json")]
    public void InvalidOrIncompatibleRepliesFailWithoutAnOperation(string reply)
    {
        var fake = Fake("journal"); fake.Gates[HudBridge.Gate("journal")] = _ => reply;
        Assert.NotNull(new HudBridge(fake).ReadModule("journal").Error);
        Assert.Throws<McpToolException>(() => new HudProvider(new HudBridge(fake)).Control("journal", "close"));
        Assert.All(fake.Calls, c => Assert.Contains("api.version", c.Argument));
    }

    [Fact]
    public void AllHudChangesRequireActionPermissionAndApproval()
    {
        var changes = typeof(HudProvider).GetMethods().Select(m => m.GetCustomAttributes(typeof(McpToolAttribute), false).Cast<McpToolAttribute>().SingleOrDefault()).Where(a => a is not null && !a.Name.StartsWith("get_", StringComparison.Ordinal));
        Assert.Equal(6, changes.Count());
        Assert.All(changes, a => { Assert.Equal(ToolPermission.Action, a!.Permission); Assert.True(a.RequiresApproval); });
    }
}

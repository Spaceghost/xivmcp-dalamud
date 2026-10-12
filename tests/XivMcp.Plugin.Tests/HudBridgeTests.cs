using System.Text.Json;
using System.Text;
using System.Net.Http.Headers;
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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HudResourceWorksBeforeLoginButStillRequiresReadPermission(bool allowRead)
    {
        var fake = Fake("journal");
        await using var server = new McpServer(new McpServerOptions { Port = 0 }, new InlineThread(), new LoggedOutHost(allowRead));
        server.RegisterProvider(new HudProvider(new HudBridge(fake)));
        await server.StartAsync();
        using var client = new HttpClient();
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        client.DefaultRequestHeaders.Add("MCP-Protocol-Version", "2026-07-28");
        client.DefaultRequestHeaders.Add("Mcp-Method", "resources/read");
        client.DefaultRequestHeaders.Add("Mcp-Name", "ffxiv://hud");
        using var body = new StringContent("""
            {"jsonrpc":"2.0","id":1,"method":"resources/read","params":{"uri":"ffxiv://hud","_meta":{
            "io.modelcontextprotocol/protocolVersion":"2026-07-28",
            "io.modelcontextprotocol/clientInfo":{"name":"hud-resource-test","version":"1"},
            "io.modelcontextprotocol/clientCapabilities":{}}}}
            """, Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(new Uri($"http://127.0.0.1:{server.ListeningPort}/mcp"), body, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        if (!allowRead)
        {
            Assert.Contains("'Read' permission tier", json.RootElement.GetProperty("error").GetProperty("message").GetString());
            Assert.Empty(fake.Calls);
            return;
        }
        Assert.False(json.RootElement.TryGetProperty("error", out _));
        var text = json.RootElement.GetProperty("result").GetProperty("contents")[0].GetProperty("text").GetString()!;
        using var family = JsonDocument.Parse(text);
        Assert.Equal(3, family.RootElement.GetProperty("modules").GetArrayLength());
        Assert.NotEmpty(fake.Calls);
        Assert.All(fake.Calls, c => Assert.Contains(JsonDocument.Parse(c.Argument!).RootElement.GetProperty("method").GetString(), ReadMethods));
    }

    private sealed class LoggedOutHost(bool allowRead) : IHostState
    {
        public bool IsLoggedIn => false;
        public bool IsPermitted(ToolPermission permission) => permission == ToolPermission.Read && allowRead;
        public bool IsCategoryEnabled(string category) => true;
    }

    private sealed class InlineThread : IGameThread
    {
        public bool IsOnGameThread => true;
        public Task<T> InvokeAsync<T>(Func<T> func, CancellationToken cancellationToken = default) => Task.FromResult(func());
        public Task InvokeAsync(Action action, CancellationToken cancellationToken = default)
        {
            action();
            return Task.CompletedTask;
        }
    }
}

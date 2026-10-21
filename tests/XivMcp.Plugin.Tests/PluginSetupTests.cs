using System.Reflection;
using System.Text.Json;
using XivMcp.Core;
using XivMcp.Plugin.Providers.DalamudInfo;

namespace XivMcp.Plugin.Tests;

public sealed class PluginSetupTests
{
    private const string Repo = "https://example.com/plugins.json";
    private static PluginSetupRequest Request(params string[] names) => PluginSetupRequest.Parse(names, Repo, "testing");
    private static object Manifest(string name, string[]? dependencies = null, int api = 15) => new
    {
        InternalName = name,
        AssemblyVersion = "1.0.0.0",
        DalamudApiLevel = api,
        DownloadLinkInstall = "https://example.com/" + name + ".zip",
        XivMcpDependencies = dependencies ?? [],
    };
    private static string Catalog(params object[] entries) => JsonSerializer.Serialize(entries);
    private static PluginSetupInstalled Installed(string name, string version = "1.0.0.0", bool loaded = true, string source = Repo)
        => new(name, version, loaded, false, false, false, loaded ? "Loaded" : "Unloaded", source, true);
    private static PluginSetupPlan Plan(PluginSetupRequest request, string json, params PluginSetupInstalled[] installed)
        => PluginSetupPlanner.Build(request, json, installed, 15, "XivMcp");

    [Fact]
    public void NewPublishedPluginNeedsNoHardcodedRegistryOrManualSetup()
    {
        var plan = Plan(Request("XivHud.Future"), Catalog(Manifest("XivHud.Future")));
        Assert.Equal("install", Assert.Single(plan.Plugins).Action);
        Assert.Equal("XivHud.Future", plan.Plugins[0].Package.InternalName);
    }

    [Fact]
    public void DependenciesAreTopologicallyOrderedAndDeduplicated()
    {
        var plan = Plan(Request("XivHud.Journal", "XivHud.Character"), Catalog(
            Manifest("XivHud.Journal", ["XivHud"]), Manifest("XivHud.Character", ["XivHud"]), Manifest("XivHud")));
        Assert.Equal(["XivHud", "XivHud.Journal", "XivHud.Character"], plan.Plugins.Select(static p => p.Package.InternalName));
    }

    [Theory]
    [InlineData("cycle")]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("api")]
    [InlineData("self")]
    public void InvalidCompletePlanFailsBeforeAnyMutations(string problem)
    {
        var (request, json) = problem switch
        {
            "cycle" => (Request("A"), Catalog(Manifest("A", ["B"]), Manifest("B", ["A"]))),
            "missing" => (Request("A", "B"), Catalog(Manifest("A"))),
            "duplicate" => (Request("A"), Catalog(Manifest("A"), Manifest("a"))),
            "api" => (Request("A"), Catalog(Manifest("A", api: 14))),
            _ => (Request("XivMcp"), Catalog(Manifest("XivMcp"))),
        };
        Assert.Throws<McpToolException>(() => Plan(request, json));
    }

    [Fact]
    public void TestingAndStableRemainExplicitIncludingNullTestingApiFallback()
    {
        var json = Catalog(new
        {
            InternalName = "A",
            AssemblyVersion = "1.0.0.0",
            TestingAssemblyVersion = "1.1.0.0",
            DalamudApiLevel = 15,
            TestingDalamudApiLevel = (int?)null,
            IsTestingExclusive = true,
            DownloadLinkInstall = "https://example.com/stable.zip",
            DownloadLinkTesting = "https://example.com/testing.zip",
        });
        var plan = Plan(Request("A"), json);
        Assert.True(plan.EnableTesting); Assert.True(plan.Plugins[0].Package.UseTesting);
        Assert.Equal("1.1.0.0", plan.Plugins[0].Package.Version);
        Assert.Throws<McpToolException>(() => Plan(PluginSetupRequest.Parse(["A"], Repo, "stable"), json));
    }

    [Theory]
    [InlineData("0.9.0.0", true, "update")]
    [InlineData("1.0.0.0", false, "enable")]
    [InlineData("1.0.0.0", true, "unchanged")]
    [InlineData("2.0.0.0", true, "unchanged")]
    public void PlansUpdatesOrEnablesAndNeverDowngrades(string version, bool loaded, string action)
        => Assert.Equal(action, Plan(Request("A"), Catalog(Manifest("A")), Installed("A", version, loaded)).Plugins[0].Action);

    [Theory]
    [InlineData("dev")]
    [InlineData("duplicate")]
    [InlineData("source")]
    [InlineData("sourcecase")]
    [InlineData("collection")]
    [InlineData("banned")]
    [InlineData("unloading")]
    public void ExistingInstallationsAndNativeConstraintsArePreserved(string problem)
    {
        var current = Installed("A");
        var installs = problem switch
        {
            "dev" => new[] { current with { IsDev = true } },
            "duplicate" => [current, current],
            "source" => [current with { SourceUrl = "https://other.example.com/plugins.json" }],
            "sourcecase" => [current with { SourceUrl = "https://example.com/Plugins.json" }],
            "collection" => [current with { DefaultCollectionOnly = false }],
            "banned" => [current with { Banned = true }],
            _ => [current with { State = "Unloading" }],
        };
        Assert.Throws<McpToolException>(() => Plan(Request("A"), Catalog(Manifest("A")), installs));
    }

    [Theory]
    [InlineData("../evil")]
    [InlineData("A/B")]
    [InlineData("A\\B")]
    [InlineData("A..B")]
    [InlineData("")]
    [InlineData("A\nB")]
    public void PluginNamesCannotBecomePathsOrCommands(string name)
        => Assert.Throws<McpToolException>(() => Request(name));

    [Fact]
    public void RequestAndTransitiveSizeAreBounded()
    {
        Assert.Throws<McpToolException>(() => Request([]));
        Assert.Throws<McpToolException>(() => Request("A", "a"));
        Assert.Throws<McpToolException>(() => Request(Enumerable.Range(0, 13).Select(static i => "A" + i).ToArray()));
        var entries = Enumerable.Range(0, 25).Select(static i => Manifest("A" + i, i < 24 ? ["A" + (i + 1)] : [])).ToArray();
        Assert.Throws<McpToolException>(() => Plan(Request("A0"), Catalog(entries)));
        Assert.Throws<McpToolException>(() => PluginSetupRequest.Parse(["A"], "https://example.com/master.json#fragment", "stable"));
    }

    [Fact]
    public async Task InvalidPreflightCannotWriteRepositoryOrStartOneOfSeveralPlugins()
    {
        var fake = new FakeBackend { PreflightError = new InvalidOperationException("private server detail") };
        await using var service = new PluginSetupService(fake);
        var ack = service.Start(Request("A", "Missing"));
        await service.WhenIdle.WaitAsync(TestContext.Current.CancellationToken);
        var result = service.Read(ack.Id);
        Assert.Equal("failed", result.State); Assert.Empty(fake.Applied); Assert.Equal(0, fake.Configured);
        Assert.DoesNotContain("private server detail", result.Error!);
    }

    [Fact]
    public async Task SetupCompletesOnlyAfterEveryLoadedVersionWasVerified()
    {
        var fake = new FakeBackend();
        await using var service = new PluginSetupService(fake);
        var ack = service.Start(Request("A", "B"));
        Assert.Equal("queued", ack.State);
        await service.WhenIdle.WaitAsync(TestContext.Current.CancellationToken);
        var result = service.Read(ack.Id);
        Assert.Equal("complete", result.State); Assert.Equal(1, fake.Configured);
        Assert.Equal(["A", "B"], fake.Applied); Assert.Equal(2, result.Verified.Count);
    }

    [Fact]
    public async Task PartialFailureStopsDependentWorkAndReportsObservedState()
    {
        var fake = new FakeBackend { NotLoaded = "B" };
        await using var service = new PluginSetupService(fake);
        var ack = service.Start(Request("A", "B", "C"));
        await service.WhenIdle.WaitAsync(TestContext.Current.CancellationToken);
        var result = service.Read(ack.Id);
        Assert.Equal("failed", result.State); Assert.Equal(["A", "B"], fake.Applied);
        Assert.Equal(2, result.Verified.Count); Assert.False(result.Verified[1].Loaded); Assert.Contains("B", result.Error!);
    }

    [Fact]
    public async Task DuplicateInflightRequestsReuseOperationAndDifferentOnesCannotRace()
    {
        var fake = new FakeBackend { Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var service = new PluginSetupService(fake);
        var first = service.Start(Request("A"));
        Assert.Equal(first.Id, service.Start(Request("A")).Id);
        Assert.Throws<McpToolException>(() => service.Start(Request("B")));
        fake.Hold.SetResult();
        await service.WhenIdle.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, fake.Configured);
    }

    [Fact]
    public async Task DisposalCancelsBeforeConfigurationAndNoSetupCanStartAfterUnload()
    {
        var fake = new FakeBackend { Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var service = new PluginSetupService(fake);
        var ack = service.Start(Request("A"));
        await service.DisposeAsync();
        Assert.Equal("cancelled", service.Read(ack.Id).State); Assert.Equal(0, fake.Configured);
        Assert.Throws<ObjectDisposedException>(() => service.Start(Request("B")));
    }

    [Fact]
    public async Task HistoryIsBoundedAndStatusReadsNeverRunAnotherSetup()
    {
        var fake = new FakeBackend();
        await using var service = new PluginSetupService(fake);
        string? first = null;
        for (var i = 0; i < 33; i++)
        {
            var ack = service.Start(Request("A")); first ??= ack.Id;
            await service.WhenIdle.WaitAsync(TestContext.Current.CancellationToken);
            Assert.Equal("complete", service.Read(ack.Id).State);
        }
        Assert.Throws<McpToolException>(() => service.Read(first!)); Assert.Equal(33, fake.Configured);
    }

    [Fact]
    public void SetupUsesTheExistingActionGateAndReadToolsRemainReadOnly()
    {
        var type = typeof(PluginSetupProvider);
        var setup = type.GetMethod(nameof(PluginSetupProvider.Setup))!.GetCustomAttribute<McpToolAttribute>()!;
        Assert.Equal(ToolPermission.Action, setup.Permission); Assert.True(setup.NeedsApproval); Assert.False(setup.GameThread);
        foreach (var name in new[] { nameof(PluginSetupProvider.Plan), nameof(PluginSetupProvider.Status) })
            Assert.Equal(ToolPermission.Read, type.GetMethod(name)!.GetCustomAttribute<McpToolAttribute>()!.Permission);
    }

    private sealed class FakeBackend : IPluginSetupBackend
    {
        public TaskCompletionSource? Hold { get; init; }
        public Exception? PreflightError { get; init; }
        public string? NotLoaded { get; init; }
        public int Configured { get; private set; }
        public List<string> Applied { get; } = [];
        public async Task<PluginSetupPlan> PlanAsync(PluginSetupRequest request, CancellationToken token)
        {
            if (Hold is not null) await Hold.Task.WaitAsync(token);
            if (PreflightError is not null) throw PreflightError;
            return Plan(request, Catalog(request.Plugins.Select(static p => Manifest(p)).ToArray()));
        }
        public Task ConfigureAsync(PluginSetupPlan plan, CancellationToken token) { Configured++; return Task.CompletedTask; }
        public Task ApplyAsync(PluginSetupPlan plan, PluginSetupPlanItem item, CancellationToken token) { Applied.Add(item.Package.InternalName); return Task.CompletedTask; }
        public Task<PluginSetupVerification> VerifyAsync(PluginSetupPlanItem item, CancellationToken token)
            => Task.FromResult(new PluginSetupVerification(item.Package.InternalName, item.Package.Version, item.Package.InternalName != NotLoaded,
                item.Package.InternalName == NotLoaded ? "LoadError" : "Loaded", true));
    }
}

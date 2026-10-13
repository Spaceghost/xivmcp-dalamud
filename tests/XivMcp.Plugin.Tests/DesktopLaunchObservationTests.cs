using XivMcp.Plugin.Bridges;
using XivMcp.Plugin.Tests.Infrastructure;

namespace XivMcp.Plugin.Tests;

public class DesktopLaunchObservationTests
{
    private static FakeBridgeInvoker Fake(Func<string?, string> windows) => new()
    {
        Gates =
        {
            [DesktopBridge.WindowsGate] = windows,
            [DesktopBridge.LaunchGate] = _ => "ok: launched Editor (editor)",
        },
    };

    [Fact]
    public async Task OnlyNewLivePanelsForResolvedAppAreObserved()
    {
        var reads = 0;
        var fake = Fake(_ => ++reads == 1
            ? """{"windows":[{"id":1,"appId":"editor","state":"live"}]}"""
            : reads == 2
                ? """{"windows":[{"id":1,"appId":"editor","state":"live"},{"id":2,"appId":"editor","state":"pending"},{"id":3,"appId":"browser","state":"live"}]}"""
                : """{"windows":[{"id":1,"appId":"editor","state":"live"},{"id":2,"appId":"editor","state":"live"}]}""");
        var game = new InlineGameThread();
        var result = await new DesktopBridge(fake).LaunchObservedAsync("text editor", game, default, (_, _) => Task.CompletedTask);
        Assert.True(result.Observed);
        Assert.Equal(2, result.PanelId);
        Assert.Equal(4, game.Hops);
        Assert.Single(fake.Calls, c => c.Gate == DesktopBridge.LaunchGate);
    }

    [Fact]
    public async Task AmbiguousOrMaskedPanelsRemainHandedOver()
    {
        var reads = 0;
        var fake = Fake(_ => ++reads == 1 ? """{"windows":[]}""" : """{"windows":[{"id":2,"appId":"editor","state":"live"},{"id":3,"appId":"editor","state":"live"},{"id":4,"state":"live"}]}""");
        var result = await new DesktopBridge(fake).LaunchObservedAsync("editor", new InlineGameThread(), default, (_, _) => Task.CompletedTask);
        Assert.False(result.Observed);
        Assert.Null(result.PanelId);
        Assert.True(result.Launch.Ok);
        Assert.Equal(21, reads);
    }

    [Fact]
    public async Task CancellationDuringObservationDoesNotLaunchAgain()
    {
        var fake = Fake(_ => """{"windows":[]}""");
        var cts = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new DesktopBridge(fake).LaunchObservedAsync("editor", new InlineGameThread(), cts.Token,
            (_, token) => { cts.Cancel(); return Task.FromCanceled(token); }));
        Assert.Single(fake.Calls, c => c.Gate == DesktopBridge.LaunchGate);
    }
}

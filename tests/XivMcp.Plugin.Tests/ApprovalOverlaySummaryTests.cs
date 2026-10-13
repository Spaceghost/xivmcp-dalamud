using XivMcp.Core;
using XivMcp.Plugin.Services;
using XivMcp.Plugin.Tests.Infrastructure;

namespace XivMcp.Plugin.Tests;

public class ApprovalOverlaySummaryTests
{
    [Fact]
    public void ShowsAtMostTwoPendingSummariesAndNeverReasons()
    {
        var tickets = new[]
        {
            Make("teleport to Kugane", "Codex", "secret reason one"),
            Make("write macro 3", "Claude Code", "secret reason two"),
            Make("set target", "Other", "secret reason three"),
            Make("already done", "Old", "old reason") with { State = TicketState.Executed },
        };

        var line = Assert.IsType<ApprovalOverlayLine>(ApprovalOverlaySummary.Create(tickets));
        Assert.Equal(3, line.Count);
        Assert.Equal("3 requests waiting: teleport to Kugane (Codex); write macro 3 (Claude Code); +1 more", line.Text);
        Assert.DoesNotContain("secret", line.Text);
        Assert.DoesNotContain("set target", line.Text);
    }

    [Fact]
    public void PrefersAuthenticatedNameAndMakesUntrustedTextSingleLineVisible()
    {
        var ticket = Make("send\nchat" + U.S("<202E>"), "reported\nname", "do not show") with
        {
            AuthenticatedClient = "trusted\tclient",
        };

        var line = Assert.IsType<ApprovalOverlayLine>(ApprovalOverlaySummary.Create([ticket]));
        Assert.Equal("1 request waiting: send chat\\u202E (trusted client)", line.Text);
    }

    [Fact]
    public void FallsBackToHumanizedToolAndReturnsNullWithoutPendingTickets()
    {
        var ticket = Make(null, null, "reason") with { ToolName = "set_map_flag" };
        Assert.Equal("1 request waiting: set map flag (unnamed client)", ApprovalOverlaySummary.Create([ticket])!.Text);
        Assert.Null(ApprovalOverlaySummary.Create([ticket with { State = TicketState.Denied }]));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \t\r\n ")]
    public void BlankSummaryAndNamesUseReadableFallbacks(string blank)
    {
        var ticket = Make(blank, blank, "not shown") with { ToolName = "set_map_flag", AuthenticatedClient = blank };
        Assert.Equal("1 request waiting: set map flag (unnamed client)", ApprovalOverlaySummary.Create([ticket])!.Text);
        Assert.Equal("1 request waiting: set map flag (reported name)",
            ApprovalOverlaySummary.Create([ticket with { ClientName = "reported name" }])!.Text);
    }

    private static Ticket Make(string? summary, string? client, string reason) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        ToolName = "teleport",
        Permission = ToolPermission.Action,
        Tier = ToolPermission.Action,
        Summary = summary,
        Reason = reason,
        ClientName = client,
        CreatedAt = DateTimeOffset.UnixEpoch,
        State = TicketState.Pending,
    };
}

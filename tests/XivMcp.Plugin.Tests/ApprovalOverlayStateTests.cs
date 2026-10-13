using XivMcp.Core;
using XivMcp.Plugin.Services;

namespace XivMcp.Plugin.Tests;

public class ApprovalOverlayStateTests
{
    [Fact]
    public void FirstRefreshReadsSnapshotAndCleanRefreshReusesLine()
    {
        var reads = 0;
        var state = new ApprovalOverlayState(() =>
        {
            reads++;
            return [Make("teleport to Kugane")];
        });

        Assert.Equal(0, reads);
        var first = Assert.IsType<ApprovalOverlayLine>(state.Refresh());
        Assert.Equal("1 request waiting: teleport to Kugane (test client)", first.Text);
        Assert.Equal(1, first.Count);
        Assert.Same(first, state.Refresh());
        Assert.Equal(1, reads);
    }

    [Fact]
    public void EmptySnapshotIsCachedAndClearedQueueRemovesLine()
    {
        IReadOnlyList<Ticket> tickets = [];
        var reads = 0;
        var state = new ApprovalOverlayState(() =>
        {
            reads++;
            return tickets;
        });

        Assert.Null(state.Refresh());
        Assert.Null(state.Refresh());
        Assert.Equal(1, reads);

        tickets = [Make("set map flag")];
        state.Invalidate();
        Assert.NotNull(state.Refresh());

        tickets = [];
        state.Invalidate();
        Assert.Null(state.Refresh());
        Assert.Null(state.Refresh());
        Assert.Equal(3, reads);
    }

    [Fact]
    public void InvalidationOnlyMarksDirtyAndRepeatedNotificationsCoalesce()
    {
        var reads = 0;
        var state = new ApprovalOverlayState(() =>
        {
            reads++;
            return [];
        });

        state.Refresh();
        for (var i = 0; i < 100; i++)
            state.Invalidate();

        Assert.Equal(1, reads);
        state.Refresh();
        state.Refresh();
        Assert.Equal(2, reads);
    }

    [Fact]
    public void InvalidationDuringSnapshotSurvivesToNextRefresh()
    {
        var reads = 0;
        ApprovalOverlayState state = null!;
        state = new ApprovalOverlayState(() =>
        {
            reads++;
            if (reads == 1)
            {
                // Model a queue notification after this snapshot has captured its
                // contents but before it returns them to the UI.
                state.Invalidate();
                return [Make("old snapshot")];
            }

            return [Make("updated snapshot")];
        });

        Assert.Contains("old snapshot", state.Refresh()!.Text);
        var updated = state.Refresh();
        Assert.Contains("updated snapshot", updated!.Text);
        Assert.Same(updated, state.Refresh());
        Assert.Equal(2, reads);
    }

    [Fact]
    public void WorkerInvalidationDuringSnapshotSurvivesToNextRefresh()
    {
        var reads = 0;
        using var snapshotStarted = new ManualResetEventSlim();
        using var invalidated = new ManualResetEventSlim();
        var state = new ApprovalOverlayState(() =>
        {
            reads++;
            if (reads == 1)
            {
                snapshotStarted.Set();
                Assert.True(invalidated.Wait(TimeSpan.FromSeconds(10)), "Worker did not invalidate the active snapshot.");
            }

            return [];
        });

        var worker = new Thread(() =>
        {
            if (snapshotStarted.Wait(TimeSpan.FromSeconds(10)))
            {
                state.Invalidate();
                invalidated.Set();
            }
        }) { IsBackground = true };
        worker.Start();
        try
        {
            state.Refresh();
            state.Refresh();
            state.Refresh();
            Assert.Equal(2, reads);
        }
        finally
        {
            Assert.True(worker.Join(TimeSpan.FromSeconds(10)), "Invalidation worker did not finish.");
        }
    }

    [Fact]
    public void SnapshotFailureRetriesWithoutAnotherInvalidation()
    {
        var reads = 0;
        var expected = new InvalidOperationException("snapshot failed");
        var state = new ApprovalOverlayState(() =>
        {
            if (++reads == 1)
                throw expected;
            return [Make("retry succeeded")];
        });

        Assert.Same(expected, Assert.Throws<InvalidOperationException>(() => state.Refresh()));
        var line = Assert.IsType<ApprovalOverlayLine>(state.Refresh());
        Assert.Contains("retry succeeded", line.Text);
        Assert.Same(line, state.Refresh());
        Assert.Equal(2, reads);
    }

    [Fact]
    public void FailedRefreshCanRecoverAndClearPreviouslyVisibleLine()
    {
        var reads = 0;
        var state = new ApprovalOverlayState(() => ++reads switch
        {
            1 => [Make("pending request")],
            2 => throw new InvalidOperationException("snapshot failed"),
            _ => [],
        });

        Assert.NotNull(state.Refresh());
        state.Invalidate();
        Assert.Throws<InvalidOperationException>(() => state.Refresh());
        Assert.Null(state.Refresh());
        Assert.Null(state.Refresh());
        Assert.Equal(3, reads);
    }

    [Fact]
    public void NullSnapshotProviderIsRejected()
    {
        Assert.Throws<ArgumentNullException>(() => new ApprovalOverlayState(null!));
    }

    private static Ticket Make(string summary) => new()
    {
        Id = "test-ticket",
        ToolName = "teleport",
        Permission = ToolPermission.Action,
        Tier = ToolPermission.Action,
        Summary = summary,
        Reason = "not shown",
        ClientName = "test client",
        CreatedAt = DateTimeOffset.UnixEpoch,
        State = TicketState.Pending,
    };
}

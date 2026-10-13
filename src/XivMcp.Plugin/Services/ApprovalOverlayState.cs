namespace XivMcp.Plugin.Services;

/// <summary>
/// Coalesces approval notifications into UI-thread snapshot refreshes.
/// <see cref="Invalidate"/> may run on any thread; only the UI thread calls <see cref="Refresh"/>.
/// </summary>
public sealed class ApprovalOverlayState
{
    private readonly Func<IReadOnlyList<Ticket>> snapshot;
    private int dirty = 1;
    private ApprovalOverlayLine? line;

    public ApprovalOverlayState(Func<IReadOnlyList<Ticket>> snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        this.snapshot = snapshot;
    }

    /// <summary>Marks the next UI refresh dirty without reading the queue or formatting text.</summary>
    public void Invalidate() => Interlocked.Exchange(ref dirty, 1);

    /// <summary>Returns the cached line, refreshing it on the UI thread when dirty.</summary>
    public ApprovalOverlayLine? Refresh()
    {
        // Consume before reading: a notification raised during the snapshot must survive
        // to the next frame, even if this snapshot did not include its queue change.
        if (Interlocked.Exchange(ref dirty, 0) == 0)
            return line;

        try
        {
            line = ApprovalOverlaySummary.Create(snapshot());
            return line;
        }
        catch
        {
            // Keep the last successful line and retry next frame without requiring a
            // second queue change to wake the overlay after a transient failure.
            Interlocked.Exchange(ref dirty, 1);
            throw;
        }
    }
}

using System.Text;

namespace XivMcp.Plugin.Services;

/// <summary>An immutable, display-safe summary for the Duty List approval row.</summary>
public sealed record ApprovalOverlayLine(int Count, string Text);

/// <summary>Builds the compact approval row without exposing reasons or arguments.</summary>
public static class ApprovalOverlaySummary
{
    public const int MaxSummaryLength = 80;
    public const int MaxClientLength = 32;

    public static ApprovalOverlayLine? Create(IEnumerable<Ticket> tickets)
    {
        var pending = tickets.Where(ticket => ticket.State == TicketState.Pending).ToArray();
        if (pending.Length == 0)
            return null;

        var shown = pending.Take(2).Select(Describe);
        var more = pending.Length > 2 ? $"; +{pending.Length - 2} more" : "";
        var noun = pending.Length == 1 ? "request" : "requests";
        return new ApprovalOverlayLine(pending.Length, $"{pending.Length} {noun} waiting: {string.Join("; ", shown)}{more}");
    }

    private static string Describe(Ticket ticket)
    {
        var summary = Clean(string.IsNullOrWhiteSpace(ticket.Summary) ? Humanize(ticket.ToolName) : ticket.Summary, MaxSummaryLength);
        var clientName = !string.IsNullOrWhiteSpace(ticket.AuthenticatedClient) ? ticket.AuthenticatedClient
            : !string.IsNullOrWhiteSpace(ticket.ClientName) ? ticket.ClientName : "unnamed client";
        var client = Clean(clientName, MaxClientLength);
        return $"{summary} ({client})";
    }

    private static string Humanize(string value) => value.Replace('_', ' ');

    private static string Clean(string value, int maxLength)
    {
        var compact = new StringBuilder(value.Length);
        var afterSpace = false;
        foreach (var ch in value.Trim())
        {
            if (char.IsWhiteSpace(ch))
            {
                if (compact.Length > 0)
                    afterSpace = true;
                continue;
            }

            if (afterSpace)
                compact.Append(' ');
            compact.Append(ch);
            afterSpace = false;
        }

        var safe = ConfirmationService.ShowInvisible(compact.ToString());
        return safe.Length <= maxLength ? safe : safe[..(maxLength - 1)] + "…";
    }
}

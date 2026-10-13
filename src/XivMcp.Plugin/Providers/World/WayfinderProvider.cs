using System.Text.Json;
using Dalamud.Plugin;
using XivMcp.Core;

namespace XivMcp.Plugin.Providers.World;

/// <summary>Optional guidance integration; respects the player's opt-in Wayfinder auto-Teleport policy.</summary>
[McpProvider("world")]
public sealed class WayfinderProvider(IDalamudPluginInterface pluginInterface)
{
    [McpTool("get_wayfinder_journey",
        Sources = ["ipc:XivWayfinder.v1.GetJourney", "ipc:XivWayfinder.v1.GetState"],
        Title = "Read the shared travel itinerary",
        Description = "Reads XivWayfinder's saved stops, zero-based progress index and running state. Also returns its current walking/teleport instructions and automatic-teleport policy when supported. Check that policy before changing guidance: opted-in auto-Teleport may spend gil. Stops before index have been advanced past, not necessarily completed. Requires a compatible loaded XivWayfinder; no assembly dependency.",
        RequiresLogin = false)]
    public JourneyResult GetJourney() => new(Read("GetJourney"), Read("GetState"));

    [McpTool("append_wayfinder_stops",
        Sources = ["ipc:XivWayfinder.v1.AppendJourney"],
        Title = "Append destinations to the shared itinerary",
        Description = "Appends a batch of world-coordinate destinations to the player's existing Wayfinder itinerary. Does not replace stops, start guidance, move, or teleport. Supply JSON {version:1,stops:[{territoryId,x,z,y?,label?}]}; territoryId must be explicit/nonzero, coordinates within ±5000, optional y is world height. Maximum 64 total stops, 32768 JSON characters. All stops are validated before any are appended. Use get_wayfinder_journey first to avoid duplicates; this operation is not idempotent.",
        Permission = ToolPermission.Action, RequiresApproval = true, Idempotent = false,
        ApprovalSummary = "Append these travel stops to your Wayfinder itinerary: {stopsJson}")]
    public EditResult AppendStops([McpParam("Versioned JSON batch of world-coordinate stops.")] string stopsJson)
    {
        if (string.IsNullOrEmpty(stopsJson) || stopsJson.Length > 32768)
            throw McpToolException.WithCode(McpErrorCodes.InvalidArguments, "stopsJson must contain 1–32768 characters.");
        Write("AppendJourney", stopsJson);
        return new EditResult(true);
    }

    [McpTool("control_wayfinder_journey",
        Sources = ["ipc:XivWayfinder.v1.ControlJourney"],
        Title = "Control itinerary guidance",
        Description = "Pause/resume guidance, explicitly advance to the next stop, or return to the previous stop. Never walks the character. If the player enabled Wayfinder automatic teleporting, changing or resuming guidance can start Teleport and spend gil under that policy; inspect get_wayfinder_journey first. Only use next after the player confirms completion or asks to skip; arriving near a marker is not proof of task completion. Previous restores a stop without resuming paused guidance. Current guide state refreshes on the next game update.",
        Permission = ToolPermission.Action, RequiresApproval = true, Idempotent = false,
        ApprovalSummary = "Apply {action} to your Wayfinder itinerary. If you enabled automatic teleporting, the new guidance may teleport you and spend gil.")]
    public EditResult Control([McpParam("Explicit itinerary action.", Enum = ["resume", "pause", "next", "previous"])] string action)
    {
        if (action is not ("resume" or "pause" or "next" or "previous"))
            throw McpToolException.WithCode(McpErrorCodes.InvalidArguments, "Unknown itinerary action.");
        Write("ControlJourney", action);
        return new EditResult(true);
    }

    private JsonElement Read(string gate)
    {
        try
        {
            var json = pluginInterface.GetIpcSubscriber<string>("XivWayfinder.v1." + gate).InvokeFunc();
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }
        catch (Exception)
        {
            throw McpToolException.WithCode(McpErrorCodes.Unavailable, "Compatible XivWayfinder guidance is unavailable; load/update Wayfinder and retry.");
        }
    }

    private void Write(string gate, string argument)
    {
        bool accepted;
        try
        {
            accepted = pluginInterface.GetIpcSubscriber<string, bool>("XivWayfinder.v1." + gate).InvokeFunc(argument);
        }
        catch (Exception)
        {
            throw McpToolException.WithCode(McpErrorCodes.Unavailable, "Wayfinder itinerary control failed or is unavailable. Read get_wayfinder_journey before retrying: if the failure occurred while saving, the edit may already have been applied.");
        }
        if (!accepted)
            throw McpToolException.WithCode(McpErrorCodes.InvalidArguments, "Wayfinder refused the edit: check stop coordinates, queue capacity and current progress. No edit was applied.");
    }

    public sealed record JourneyResult(JsonElement Journey, JsonElement Guidance);
    public sealed record EditResult(bool Accepted);
}

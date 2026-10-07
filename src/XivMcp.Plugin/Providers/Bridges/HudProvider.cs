using System.Text.Json;
using System.Text.Json.Nodes;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using XivMcp.Core;
using XivMcp.Plugin.Bridges;

namespace XivMcp.Plugin.Providers.Bridges;

[McpProvider("bridges")]
public sealed class HudProvider
{
    private readonly HudBridge bridge;
    public HudProvider(IDalamudPluginInterface pi, IPluginLog log) => bridge = new(new DalamudBridgeInvoker(pi, log));
    internal HudProvider(HudBridge bridge) => this.bridge = bridge;

    [McpTool("get_hud_family_status", RequiresLogin = false, Title = "Read the XivHud family",
        Sources = ["dalamud:InstalledPlugins", "ipc:XivHud.v1.Call", "ipc:XivHud.Journal.v1.Call", "ipc:XivHud.Character.v1.Call"],
        Description = "Read all XivHud modules independently: installed/loaded versions, versioned IPC capabilities, native HUD requested/applied visibility, Journal selection/search/bookmarks, Character page, data readiness, requested versus actual screen/native-frame/world hosting, focus and controller status. Missing or old modules return their own errors without hiding healthy modules. Windowing is a bundled shared library, not a separately installed plugin. Character and quest facts remain available through get_character_sheet/get_quest_journal. Reads neither open windows nor capture controller input; snapshots include observedAt.")]
    public FamilyResult Family() => new(HudBridge.Modules.Select(bridge.ReadModule).ToArray(),
        "HUD visibility is independent of module window hosting. Only a focused local window owns controller input. A queued/applied UI request is not proof that its host displayed a frame; inspect actualMode and hostingStatus. Recovery: /xivhud off, /xivjournal screen, /xivcharacter screen.");

    [McpTool("get_hud_module_status", RequiresLogin = false, Title = "Read one HUD module",
        Sources = ["ipc:XivHud.v1.Call", "ipc:XivHud.Journal.v1.Call", "ipc:XivHud.Character.v1.Call"],
        Description = "Read one XivHud module's capabilities and immutable state snapshot, including window hosting, UI selection, controller focus and data errors. Returns an error field for an absent, unloaded or incompatible module. Does not open its UI.")]
    public HudModuleResult Module([McpParam("HUD module.", Enum = ["hud", "journal", "character"])] string module) => bridge.ReadModule(module);

    [McpResource("ffxiv://hud", Name = "XivHud family", Description = "On-demand snapshot of the HUD host, Journal and Character, including optional hosting and recovery commands.")]
    public FamilyResult Resource() => Family();

    [McpTool("set_hud_enabled", Permission = ToolPermission.Action, RequiresApproval = true, RequiresLogin = false,
        Title = "Set native HUD visibility", Sources = ["ipc:XivHud.v1.Call#hud.set_enabled"],
        Description = "Explicitly enable XivHud's reversible native HUD suppression or restore the game HUD. Preserves the saved native layout. Does not open Journal/Character or acquire controller input. Returns a queued requestId; poll get_hud_request and read get_hud_family_status to confirm applied visibility. Off is the recovery operation.",
        ApprovalSummary = "Set XivHud native HUD suppression to {enabled}. False restores the game HUD.")]
    public JsonElement SetEnabled([McpParam("True hides native HUD categories; false restores them.")] bool enabled) => bridge.Change("hud", "hud.set_enabled", new JsonObject { ["enabled"] = enabled });

    [McpTool("control_hud_window", Permission = ToolPermission.Action, RequiresApproval = true, RequiresLogin = false,
        Title = "Control a HUD family window", Sources = ["ipc:XivHud.v1.Call", "ipc:XivHud.Journal.v1.Call", "ipc:XivHud.Character.v1.Call"],
        Description = "Explicit HUD UI operation: open, close, settings, original, or mode. hud supports settings/close only; Journal/Character support every operation. mode selects screen, native_frame (KamiToolKit), or world (optional Ghostty) and opens the module. World placement is Hud, Companion, World or Ground. Screen recovers the ordinary UI when an optional host fails. original opens the game's own window and closes the replacement. No synthetic key or controller input, no automatic gear changes. Poll get_hud_request, then read actual hosting state.",
        ApprovalSummary = "Apply {action} to the {module} HUD window, with mode {mode} and placement {placement}.")]
    public JsonElement Control(
        [McpParam("HUD module.", Enum = ["hud", "journal", "character"])] string module,
        [McpParam("Explicit operation.", Enum = ["open", "close", "settings", "original", "mode"])] string action,
        [McpParam("Required only for action=mode.", Enum = ["screen", "native_frame", "world"])] string mode = "",
        [McpParam("Optional only for world mode.", Enum = ["Hud", "Companion", "World", "Ground"])] string placement = "")
    {
        HudBridge.Key(module);
        if (action is not ("open" or "close" or "settings" or "original" or "mode") || module == "hud" && action is not ("settings" or "close")) throw Invalid("Unsupported HUD window action.");
        var p = new JsonObject();
        if (action == "mode")
        {
            if (mode is not ("screen" or "native_frame" or "world")) throw Invalid("Choose screen, native_frame, or world.");
            p["mode"] = mode;
            if (placement.Length > 0)
            {
                if (mode != "world" || placement is not ("Hud" or "Companion" or "World" or "Ground")) throw Invalid("Placement requires world mode and a supported placement.");
                p["placement"] = placement;
            }
        }
        else if (mode.Length > 0 || placement.Length > 0) throw Invalid("mode and placement apply only to the mode action.");
        return bridge.Change(module, "window." + action, p);
    }

    [McpTool("select_hud_quest", Permission = ToolPermission.Action, RequiresApproval = true,
        Title = "Select a quest in XivHud Journal", Sources = ["ipc:XivHud.Journal.v1.Call#journal.select"],
        Description = "Open the illustrated Journal on an accepted quest, clearing its UI search/filter to reveal the selection. Does not track the quest, change an objective or start Wayfinder. The Journal checks the quest is still accepted when applying the request. Use get_quest_journal for IDs; poll get_hud_request.",
        ApprovalSummary = "Open your illustrated Journal on quest {questId}.")]
    public JsonElement SelectQuest([McpParam("Accepted Quest sheet row ID.", Minimum = 1)] uint questId) => Quest("journal.select", questId);

    [McpTool("bookmark_hud_quest", Permission = ToolPermission.Action, RequiresApproval = true,
        Title = "Set a Journal bookmark", Sources = ["ipc:XivHud.Journal.v1.Call#journal.bookmark"],
        Description = "Set or remove a local XivHud Journal bookmark for an accepted quest. Does not modify native quest tracking. Returns a requestId for get_hud_request.",
        ApprovalSummary = "Set the local Journal bookmark for quest {questId} to {bookmarked}.")]
    public JsonElement Bookmark([McpParam("Accepted Quest sheet row ID.", Minimum = 1)] uint questId, [McpParam("Explicit desired bookmark state.")] bool bookmarked) => Quest("journal.bookmark", questId, bookmarked);

    [McpTool("show_hud_quest_on_map", Permission = ToolPermission.Action, RequiresApproval = true,
        Title = "Show a Journal quest on the game map", Sources = ["ipc:XivHud.Journal.v1.Call#journal.show_map"],
        Description = "Ask the Journal's existing native map handoff to show an accepted quest, closing the illustrated Journal. No route or movement is started. Poll get_hud_request; applied means the handoff was invoked, not that a map marker was verified.",
        ApprovalSummary = "Show quest {questId} on your game map and close the illustrated Journal.")]
    public JsonElement ShowMap([McpParam("Accepted Quest sheet row ID.", Minimum = 1)] uint questId) => Quest("journal.show_map", questId);

    [McpTool("set_hud_character_page", Permission = ToolPermission.Action, RequiresApproval = true,
        Title = "Open a Character record page", Sources = ["ipc:XivHud.Character.v1.Call#character.page"],
        Description = "Open the illustrated Character record on Equipment, Attributes, ClassesAndJobs or GearSets. This selects a UI page only; it never equips gear. Read get_character_sheet for game data. Poll get_hud_request then inspect module status.",
        ApprovalSummary = "Open your illustrated Character record on {page}.")]
    public JsonElement Page([McpParam("Character record page.", Enum = ["Equipment", "Attributes", "ClassesAndJobs", "GearSets"])] string page)
    {
        if (page is not ("Equipment" or "Attributes" or "ClassesAndJobs" or "GearSets")) throw Invalid("Unknown Character page.");
        return bridge.Change("character", "character.page", new JsonObject { ["page"] = page });
    }

    [McpTool("get_hud_request", RequiresLogin = false, Title = "Read a HUD operation result",
        Sources = ["ipc:XivHud.v1.Call#request.status", "ipc:XivHud.Journal.v1.Call#request.status", "ipc:XivHud.Character.v1.Call#request.status"],
        Description = "Poll a HUD module request by requestId. States: queued, applied, refused (with error). Keeps the latest 64 completed results until plugin reload; not found does not prove an operation failed. Check the module snapshot before retrying. Applied confirms module processing; actual host presentation is reported separately.")]
    public JsonElement Request([McpParam("Originating module.", Enum = ["hud", "journal", "character"])] string module, [McpParam("requestId returned by a HUD change.")] string requestId) => bridge.RequestStatus(module, requestId);

    private JsonElement Quest(string method, uint id, bool? bookmark = null)
    {
        if (id == 0) throw Invalid("questId must be nonzero.");
        var p = new JsonObject { ["questId"] = id };
        if (bookmark.HasValue) p["bookmarked"] = bookmark.Value;
        return bridge.Change("journal", method, p);
    }
    private static McpToolException Invalid(string message) => McpToolException.WithCode(McpErrorCodes.InvalidArguments, message);
    public sealed record FamilyResult(IReadOnlyList<HudModuleResult> Modules, string Note);
}

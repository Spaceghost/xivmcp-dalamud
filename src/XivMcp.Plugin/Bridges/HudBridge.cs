using System.Text.Json;
using System.Text.Json.Nodes;
using XivMcp.Core;

namespace XivMcp.Plugin.Bridges;

/// <summary>Versioned HUD IPC without a HUD assembly dependency. Reads never invoke changes.</summary>
public sealed class HudBridge(IBridgeInvoker invoker)
{
    public static readonly string[] Modules = ["hud", "journal", "character"];
    public static string Key(string module) => module switch
    {
        "hud" => "xivhud", "journal" => "xivhud_journal", "character" => "xivhud_character",
        _ => throw McpToolException.WithCode(McpErrorCodes.InvalidArguments, "module must be hud, journal, or character."),
    };
    public static string Gate(string module) => module switch
    {
        "hud" => "XivHud.v1.Call", "journal" => "XivHud.Journal.v1.Call", "character" => "XivHud.Character.v1.Call",
        _ => throw McpToolException.WithCode(McpErrorCodes.InvalidArguments, "Unknown HUD module."),
    };

    public HudModuleResult ReadModule(string module)
    {
        var key = Key(module);
        var status = invoker.Probe(key);
        if (status?.IpcAvailable != true) return new(module, status, null, null, status?.Unavailable ?? "Module is unavailable.");
        try
        {
            var capabilities = Call(module, "api.version");
            var snapshot = Call(module, "status");
            if (!snapshot.TryGetProperty("version", out var version) || version.GetInt32() != 1 ||
                !snapshot.TryGetProperty("module", out var identity) || identity.GetString() != module)
                throw McpToolException.WithCode(McpErrorCodes.Unavailable, "HUD snapshot has an incompatible version or module identity.");
            return new(module, status, capabilities, snapshot, null);
        }
        catch (Exception e) when (e is McpToolException or InvalidOperationException or JsonException)
        { return new(module, status, null, null, e.Message); }
    }

    public JsonElement Change(string module, string method, JsonObject? parameters = null)
    {
        Key(module);
        // Probe/read capabilities first; never guess that a installed version supports a change.
        invoker.Require(Key(module));
        var caps = Call(module, "api.version");
        if (!caps.TryGetProperty("methods", out var methods) || methods.ValueKind != JsonValueKind.Array ||
            !methods.EnumerateArray().Any(v => v.ValueKind == JsonValueKind.String && v.GetString() == method))
            throw McpToolException.WithCode(McpErrorCodes.Unavailable, $"{module} does not support {method}.");
        return Call(module, method, parameters);
    }

    public JsonElement RequestStatus(string module, string id)
    {
        Key(module);
        if (!Guid.TryParseExact(id, "N", out _)) throw McpToolException.WithCode(McpErrorCodes.InvalidArguments, "requestId must be a 32-digit request identifier returned by the HUD module.");
        invoker.Require(Key(module));
        return Call(module, "request.status", new JsonObject { ["id"] = id });
    }

    private JsonElement Call(string module, string method, JsonObject? parameters = null)
    {
        try
        {
            var request = new JsonObject { ["method"] = method, ["params"] = parameters ?? new JsonObject(), ["caller"] = "XivMcp" };
            var raw = invoker.Call(Gate(module), request.ToJsonString());
            if (raw.Length > 65536) throw McpToolException.WithCode(McpErrorCodes.Unavailable, "HUD response exceeds its size limit.");
            using var doc = JsonDocument.Parse(raw, new JsonDocumentOptions { MaxDepth = 16 });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True)
            {
                var error = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : "invalid_reply";
                throw McpToolException.WithCode(McpErrorCodes.Unavailable, $"{module}: {BridgeJson.Clip(error ?? "refused", 250)}");
            }
            if (!root.TryGetProperty("result", out var result)) throw new JsonException("Missing HUD result.");
            if (method == "api.version" && (!result.TryGetProperty("version", out var v) || v.GetInt32() != 1))
                throw McpToolException.WithCode(McpErrorCodes.Unavailable, "HUD IPC version is unsupported.");
            return result.Clone();
        }
        catch (BridgeCapabilityMissingException)
        { throw McpToolException.WithCode(McpErrorCodes.Unavailable, $"Load or update {module}; its versioned HUD IPC is unavailable."); }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
        { throw McpToolException.WithCode(McpErrorCodes.Unavailable, "HUD IPC returned an invalid response."); }
    }
}

public sealed record HudModuleResult(string Module, BridgeStatus? Plugin, JsonElement? Capabilities, JsonElement? Snapshot, string? Error);

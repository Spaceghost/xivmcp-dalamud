# XivHud family integration

XivHud, XivHud.Journal and XivHud.Character are independent plugins from [one source repository](https://github.com/Spaceghost/xivhud-dalamud). Shared Windowing/Protocol libraries are bundled, not installer identities. Character remains a manual development package until live acceptance. Optional Ghostty provides world hosting; optional XivHud provides focused controller ownership. Mouse/keyboard screen windows work without those optional hosts.

`get_hud_family_status`, `get_hud_module_status` and `ffxiv://hud` read installation/load versions, v1 IPC capabilities and immutable snapshots. Each module reports its own unavailability; missing Character does not hide Journal. Reads never open UI, choose a page or capture input. Snapshots include observation time and may lag the game by about 200 ms. HUD distinguishes desired enabled state from applied suppression. Module windows report open/focused state, requested vs actual mode, host diagnostics, controller status and UI selection. Data readiness/error fields explain why a logged-out or unavailable module has no usable record.

Six tools use the existing Action tier and local approval service: `set_hud_enabled`, `control_hud_window`, `select_hud_quest`, `bookmark_hud_quest`, `show_hud_quest_on_map`, `set_hud_character_page`. Remote operations are explicit UI changes only: no movement, controller synthesis, gear equip, quest tracking or route start. All inputs use allowlists and required types. Invalid parameters are rejected before IPC. Capability discovery precedes a change so an older module is never sent an unsupported verb.

Changes return `requestId` with `state=queued`. Poll `get_hud_request` using the originating module for queued/applied/refused. Applied confirms owner-thread processing, not host presentation: inspect a newer module snapshot and its actualMode/hostingStatus. Requests/results are bounded (32 queued, 8 processed per update, 64 completed retained). Reload or history expiry can lose a result; inspect the state before retrying.

The companion [IPC contract](https://github.com/Spaceghost/xivhud-dalamud/blob/main/docs/IPC.md) documents gate names, envelopes, lifecycle and local caller trust. There is no HUD assembly reference in MCP. In-process Dalamud IPC is local and does not independently authenticate callers; MCP's approval service applies to MCP requests.

Game facts remain available through `get_character_sheet` and `get_quest_journal`, independently of replacement UI. UI bookmarks and selected rows are preferences, not native quest/gear state. Character's page control opens Equipment, Attributes, ClassesAndJobs or GearSets, without equipping.

Recovery: `/xivhud off` restores native HUD visibility; `/xivjournal screen` and `/xivcharacter screen` restore ordinary replacement windows. The `original` operation hands off to the game's native window and closes the replacement. Focused local windows own controller input; reading status never acquires it.

`list_bridges` and `get_bridge_state` also discover Piano playback, Lantern health, Wayfinder target/state and Rug renderer health. Rug accepts the existing FloorMap installation alias. These generic reads are passive; unavailable gates yield availability diagnostics. Stream is a host tool, not a Dalamud bridge.

This publication has offline build/protocol/provider tests. No new live HUD/MCP round trip was run because the game container was stopped. Verify installed/loaded versions, all modes/fallbacks, controller focus release, native recovery, quest/map handoff and page selection live before describing them as accepted.

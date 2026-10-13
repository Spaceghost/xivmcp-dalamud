# xiv-mcp

<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="images/readme/hero-dark.png">
    <source media="(prefers-color-scheme: light)" srcset="images/readme/hero-light.png">
    <img src="images/readme/hero-dark.png" width="100%" alt="XivMcp: your game, for your AI assistant">
  </picture>
</p>

<img src="images/icon.png" width="96" height="96" align="right" alt="XivMcp icon">

**[Site](https://spacegho.st/mods/ffxiv/xivmcp/) · [Install](https://spacegho.st/mods/ffxiv/plugins/) · [Vote on what's next](https://spacegho.st/mods/ffxiv/xivmcp/vote/) · [Screenshots](https://spacegho.st/mods/ffxiv/term/gallery/?mod=xivmcp) · [Changelog](CHANGELOG.md)**

A [Model Context Protocol](https://modelcontextprotocol.io) server that runs **inside FINAL FANTASY XIV** as a
Dalamud plugin. MCP clients such as Claude Code connect to it over Streamable HTTP on loopback
(`http://127.0.0.1:41800/mcp`) and get tools, resources and prompts for game state, game data, the
player's UI and — only when the player opts in — actions and chat. An optional Umbra toolbar widget
shows server status and the agent board.

> **Status, 2026-09-30.** XivMcp is running under Wine with Dalamud API 15 (15.0.3.6, .NET 10) and game build
> `2026.09.15.0000.0000`. The installed 0.1.1 server reports 156 registered tools and no failed providers; selected
> plugin-control and map/Journal-window calls have succeeded in-game. That is a smoke check, not certification of
> the whole catalogue, approval UI, or standalone hand-off. Host tests cover additional development changes, including
> three Wayfinder tools not yet deployed to that server. See individual changelog entries for verification scope.

## HUD family

Read `get_hud_family_status` or `ffxiv://hud` for independent HUD, Journal and Character discovery, capabilities, hosting and controller diagnostics. Six approved UI tools use bounded owner-thread requests; poll `get_hud_request` and inspect actual hosting before claiming success. [HUD integration and recovery](docs/HUD.md) explains optional dependencies and validation limits.

## How it fits together

```
MCP client (Claude Code, ...)  --HTTP POST/GET/DELETE /mcp, Bearer token-->  XivMcp plugin (in game, under Wine)
                                                                               |- McpServer (XivMcp.Core, TcpListener, no ASP.NET)
                                                                               |- providers: [McpProvider] classes -> tools/resources/prompts
                                                                               |- IGameThread -> IFramework (game memory only on the framework thread)
                                                                               |- /xivmcp window, confirmation prompt, DTR entry
                                                                               '- Dalamud IPC  <-- XivMcp.Umbra widget
```

Wine maps `127.0.0.1` inside the game to the host's loopback, so host-side clients reach the plugin
directly. Details: [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

**Before the game is launched** the same endpoint can be served by `xiv-mcp-standalone`: game-data tools work from the
installed game files, everything else answers `game_not_running`, and the plugin takes the port over when the game
starts ([docs/STANDALONE.md](docs/STANDALONE.md)). Host-tested only.

**What this will never do** — play the game for you, snipe the market, touch packets, install plugins silently — and
the one checkbox that governs approval of everything that changes state: [docs/HARD-LINES.md](docs/HARD-LINES.md).

## Install (from the plugin repository)

XivMcp is listed in the author's own third-party Dalamud repository, next to the
other FFXIV mods there. In game:

1. `/xlsettings` → **Experimental** → **Custom Plugin Repositories** → paste
   `https://spacegho.st/mods/ffxiv/plugins.json` → **+** → **Save and close**.
2. `/xlplugins` → **All Plugins** → search **XivMcp** → **Install**.

It has a stable release, so it shows up for everyone. For test builds ahead of a
release, right-click its entry → **Receive plugin testing versions**.
<https://spacegho.st/mods/ffxiv/plugins/> walks through the same steps. It is a
third-party repository: Dalamud will say nobody but the author reviewed it, which is
true. Releases are built on GitHub Actions from a tag (`.github/workflows/release.yml`);
`v1.2.3` is a stable release, `v1.2.3-test.1` moves the floating `testing` release.

You do not need any of that to run it: building it yourself, below, is the path the
author develops on, and it stays supported for anyone who wants to read the code first.

## Install (dev plugin)

Requirements: XIVLauncher.Core with Dalamud 15.0.3.5 (API 15), the .NET 10 SDK on the host (`~/.dotnet/dotnet`
is picked up automatically). Reference assemblies come from `~/.xlcore/dalamud/Hooks/dev/`; rebuild after every
Dalamud update.

```sh
tools/install-dev.sh
```

It builds Release, copies the result into `~/xiv-mcp-build/devplugin/` (so Dalamud never sees a half-written
build) and prints the Wine path of the staged DLL, e.g. `Z:\home\<you>\xiv-mcp-build\devplugin\XivMcp.dll`.
In game, once:

1. `/xlsettings` → **Experimental** → **Dev Plugin Locations**: paste that path, click **+**, then **Save and close**.
2. `/xlplugins` → **Dev Tools** → **Installed Dev Plugins**: Dalamud adds dev plugins **disabled**. Enable
   **XivMcp**, and in its entry tick **Start on boot** (otherwise it stays off after the next game start).
3. `/xivmcp` opens the window. The server starts automatically (Settings → *Start server when the
   plugin loads*).

After rebuilding, run `tools/install-dev.sh` again and reload XivMcp from `/xlplugins` (or tick its **Automatic reloading**
option). If `/xivmcp` is an unknown command, the plugin is not loaded: check steps 1–2 and the Dalamud
log (`~/.xlcore/logs/dalamud.log`, search for `XivMcp`).

The script never edits Dalamud's configuration or copies files into `~/.xlcore`. The optional Umbra
widget has its own installer (`tools/install-umbra.sh`).

## Connect a client

On first load the plugin generates a 256-bit bearer token and stores it in
`~/.xlcore/pluginConfigs/XivMcp.json`. Every request must send `Authorization: Bearer <token>`
(unless you turn *Require bearer token* off).

### Claude Code (recommended)

```sh
tools/claude-mcp-add.sh            # --force to replace an existing "xiv-mcp" entry, --dry-run to preview
```

This registers `xiv-mcp` at **user** scope with `"type": "http"` and a
[`headersHelper`](https://code.claude.com/docs/en/mcp): a small script installed to
`~/.local/share/xiv-mcp/headers-helper` that reads `BearerToken` from the plugin config each time Claude
Code connects. The token is never printed and never stored in Claude's config, and regenerating it in
game only needs a reconnect (`/mcp` in Claude Code).

Pass `--url http://<tailnet-ip>:41800/mcp` to register the tailnet endpoint instead of loopback (see
[Where the server listens](#where-the-server-listens)); on another machine the helper cannot read the
plugin config, so use the generic JSON below with the token instead.

### Anything else

The Status tab has copy buttons for a `claude mcp add --header ...` command and a generic
`mcpServers` JSON block (token masked until you reveal it):

```json
{
  "mcpServers": {
    "xiv-mcp": {
      "type": "http",
      "url": "http://127.0.0.1:41800/mcp",
      "headers": { "Authorization": "Bearer <token>" }
    }
  }
}
```

Smoke test from the host without printing the token:

```sh
TOKEN="$(python3 -c 'import json,os;print(json.load(open(os.path.expanduser("~/.xlcore/pluginConfigs/XivMcp.json"),encoding="utf-8-sig"))["BearerToken"])')"
curl -s http://127.0.0.1:41800/mcp \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -H 'Accept: application/json, text/event-stream' \
  -d '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"curl","version":"0"}}}'
```

## Where the server listens

Settings → **Server** → *Where the server listens*. Changing it applies without reloading the plugin:
**Apply and restart server** stops the listener and binds the new addresses in place.

| Mode | Binds | Who can connect |
| --- | --- | --- |
| **This machine only** (default) | `127.0.0.1` | Only this computer. Under Wine that includes clients on the Linux host. |
| **This machine + my tailnet** | `127.0.0.1` **and** this machine's Tailscale address | This computer, plus anything on your tailnet that has the bearer token. |
| **Tailnet only** | the Tailscale address | Your tailnet only; local clients must use that address too. |
| **Custom address…** | whatever you type | Whatever that address is reachable from. |

The plugin finds the Tailscale address itself: it looks for an address in `100.64.0.0/10` (CGNAT) or
`fd7a:115c:a1e0::/48` on the machine's network adapters, preferring an adapter named `tailscale0`/`ts*`.
Detection runs when the server starts and whenever the settings window opens, so a Tailscale restart or a
changed address is picked up. The detected address and, when it can be read, the MagicDNS name are shown
live in Settings along with the endpoint URLs the mode produces.

**If Tailscale is not running or has no address, a tailnet mode falls back to `127.0.0.1`** and says so
in Settings, the Status tab and the log. The server always starts; it never silently binds something
wider than you asked for.

### Reaching the server from another tailnet machine

```sh
curl -s http://<tailnet-ip>:41800/mcp \
  -H "Authorization: Bearer <token>" -H 'Content-Type: application/json' \
  -H 'Accept: application/json, text/event-stream' \
  -d '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"curl","version":"0"}}}'
```

The MagicDNS name works in place of the address (`http://<machine>.<tailnet>.ts.net:41800/mcp`) when the
plugin was able to read it — that name is added to the accepted `Host` headers. The Status tab's copy
buttons already use the tailnet address when it is bound, so pasting from there gives a client config
that works from any tailnet machine.

> **Security.** Binding the tailnet means *anyone on your tailnet who has the bearer token can drive your
> game*: every tool tier you have enabled, including Action and Chat if you turned those on, and chat is
> visible to other players. The token is mandatory off loopback and the checkbox is held on. Keep
> Action/Chat confirmation on, and treat the token like a password: regenerate it (Settings → Advanced) if
> it leaks, and remember that tailnet ACLs are what decide who can even reach the port.

## Provisioning file (optional)

For config management and unattended machines, the plugin reads an optional file that **overrides** the
saved settings while it exists. The plugin only ever reads it — it is never written back, and removing it
restores exactly what you had configured in game.

Location, in order: `$XIVMCP_PROVISION`, else `$XDG_CONFIG_HOME/xiv-mcp/provision.json`, else
`$HOME/.config/xiv-mcp/provision.json`. (Inside the game these Unix paths are reached through Wine's `Z:`
drive automatically.) It is re-read within a few seconds of changing, and a change to the bind settings
restarts the listener without a plugin reload.

```json
{
  "Enabled": true,
  "BindMode": "LoopbackAndTailnet",
  "CustomHost": "198.51.100.7",
  "Port": 41800,
  "Path": "/mcp",
  "RequireToken": true,
  "BearerToken": "REPLACE_WITH_A_43_CHAR_BASE64URL_TOKEN",
  "AllowedOrigins": ["http://192.0.2.10:3000"],
  "CallTimeoutSeconds": 30,
  "ConfirmTimeoutSeconds": 20,
  "DisabledCategories": ["chat"]
}
```

Every key is optional: only the keys present override anything, and Settings greys out exactly those and
names the file they come from. `BindMode` accepts `Loopback`, `LoopbackAndTailnet`, `TailnetOnly` or
`Custom` (`CustomHost` only matters for `Custom`). Unknown keys are ignored.

Because it can carry the bearer token, **write it with mode 0600**:

```sh
install -d -m 700 ~/.config/xiv-mcp
install -m 600 /dev/null ~/.config/xiv-mcp/provision.json   # then write the JSON into it
```

Its contents are never logged — the log records the path, which settings it provisioned and any parse
error, nothing else. A malformed file keeps the last values that loaded, shows the error in Settings and
in the log, and never falls back to a wider bind.

## Permission model

Every tool declares one tier. Each tier is a separate toggle (Settings → Permissions); disabled tiers
and categories are hidden from `tools/list` and rejected on call.

| Tier | Default | Meaning |
| --- | --- | --- |
| **Read** | on | Observe game state and static game data. |
| **Ui** | on | Local-only visible effects: echo to your own chat log, toasts, map flags, opening windows, the agent board. |
| **Action** | **off** | Changes your client: targeting, gearsets, teleport, arbitrary slash commands. |
| **Chat** | **off** | Sends text other players can see (say, party, tells, FC, ...). |

- **Confirmation.** *Ask me before every Action/Chat call* (default on): each Action or Chat call waits for the
  in-game confirmation window, which shows the tool, the client-reported name and the pretty-printed arguments, with
  **Allow**, **Deny** and **Allow this tool for 10 min** (same tool, tier and client name; revoked when permissions
  change, listed under Settings). Unanswered calls are denied after *Auto-deny after (s)* (default 20). The client
  gets `isError` "denied in game by the player" or "not confirmed in game within N s", and the tool did not run.
  `execute_command` lines that post chat are shown and granted as Chat. Turning confirmation off makes Action/Chat
  follow their tier toggles directly. **Unverified in game:** the window and its buttons have not been exercised
  inside FINAL FANTASY XIV yet; the server-side hook and the service logic are covered by host tests.
- **Approve later (ticket queue).** An agent that may run while you are away calls `request_action` instead of the
  tool: the call becomes a ticket in the **Approvals** tab and waits, across reloads and game restarts, until you
  approve or deny it. Approved tickets run with the normal checks and call timeout, and the agent picks up the result
  with `get_ticket`/`list_tickets` or a resource subscription and resumes its plan. From a ticket you can also
  **Allow everything from this client for 5 min** (1–60 in Settings; Chat only with a separate checkbox; banner with
  countdown and Revoke; never saved). For unattended CI, a **client token** plus **auto-approve rules** pre-approve
  exact command prefixes for that token only. Details, diagrams and the client resume contract:
  [docs/APPROVALS.md](docs/APPROVALS.md). **Game automation through XivMcp is limited to what you approve**: a click,
  a session you opened, or a rule you wrote. **Unverified in game**, like the confirmation window.

```
agent --request_action--> ticket (pending, saved) --you: Approve--> runs in game --> result on the ticket --> agent resumes
                                                  \--you: Deny----> denied (agent does not retry)
agent --tools/call-------> confirmation window (auto-deny after N s)  [unchanged]
session / rule / 10-min grant covers the call --> runs without a prompt, logged
```
- **Categories** (character, chat, gamedata, ui, meta, prompts, ...) can be switched off individually.
- **Resources** follow the Read tier as well as their category; prompts only return text and follow their category.
- **Network.** The listener binds `127.0.0.1` by default. Any bind that reaches past this machine is
  shown with a red warning and **forces the bearer token on**; the server refuses to start such a bind
  without one. Requests carrying an `Origin` header are accepted only from loopback origins or the
  configured allow-list, and the `Host` header must name loopback, an address the server actually bound,
  or its MagicDNS name (DNS-rebinding defence). See [Where the server listens](#where-the-server-listens).
- **Out of scope:** combat rotations, movement, and input automation of any kind.

**Local model and companion plugins.** *Settings → Local model* records an OpenAI-compatible local server (Ollama, LM
Studio, llama.cpp, KoboldCpp) with **Detect** and **Test** buttons. XivMcp does not run the model; companion plugins
(Almanac, the Ghostty terminal's `/ask`) read it over Dalamud IPC (`XivMcp.GetLocalModel`) and can connect themselves
with `XivMcp.ConnectClient`, which issues a per-client token (switch: *Let other plugins connect themselves*). Game
actions from those clients still need your in-game approval. Gates and payloads:
[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md#ipc-plugin--umbra).

All settings: [docs/CONFIGURATION.md](docs/CONFIGURATION.md).

## In game

- `/xivmcp` toggles the window; `/xivmcp start|stop|restart|status|settings`; `/xivmcp quests ...` for custom objectives.
- **Status**: running state, endpoint, sessions, bind errors, provider load failures, masked token,
  client snippets. **Agents**: the agent board with state colours and progress bars. **Approvals** (with the
  pending count): queued tickets to approve or deny, approval sessions and recent results. **Activity**: live
  request feed with filter; failures highlighted. **Tools**: every registered tool by category with its
  tier and whether it is currently available. **Settings**: everything configurable.
- Server info bar entry `MCP ● n` (n = active sessions; `?` when a confirmation is waiting). Click to
  open the window.
- The agent board: agents call `post_status` to show "what I'm doing" in game (and in the Umbra
  widget); a notification appears when an agent posts `done` or `failed`.
- Custom objectives ("quests"): agents call `post_objective` / `update_objective` (or you load a quest pack with
  `/xivmcp quests load <file>`), and each one appears under the game's Duty List with its current step and a live
  *Ready now* / *Next window in N min* line from its zone, spot, Eorzea time window and weather. Click one to flag it
  on the map. They are **not** Journal quests (those are server-side and cannot be added); they are an overlay drawn to
  match the Duty List. Details and the in-game checklist: [docs/OBJECTIVES.md](docs/OBJECTIVES.md).

## Tool catalog

Generated from the `[McpTool]`, `[McpResource]`, `[McpResourceTemplate]` and `[McpPrompt]` attributes of the built
plugin by `tools/catalog` (`dotnet run --project tools/catalog -c Release -- readme --write README.md` after building);
do not edit the block by hand.

<!-- BEGIN GENERATED CATALOG: dotnet run --project tools/catalog -- readme --write README.md -->

165 tools (44 change something and go through the approval switch; 26 also work before the game starts), 20 resources and templates, 10 prompts.

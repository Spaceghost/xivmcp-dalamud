# Automatic plugin setup

The owner requested end-to-end setup of the plugins we create. MCP can now
register a repository, configure its release channel, install or update packages,
enable them, and verify their loaded versions without settings or installer clicks.
This is runtime plugin setup. Building source and publishing release assets happen
on the host/GitHub; a plugin must have a published package in the chosen catalogue.

## One setup request

```json
{
  "plugins": ["XivHud.Vitals"],
  "repositoryUrl": "https://github.com/Spaceghost/xivhud-dalamud/releases/download/development/pluginmaster-development.json",
  "channel": "testing"
}
```

Call `setup_plugins` with those arguments, then poll `get_plugin_setup_status`
with the returned `id`. The request is queued immediately and has these states:

- `queued` / `running`: not successful yet. `phase` identifies preflight,
  repository configuration, or the plugin currently being installed/enabled.
- `complete`: every planned plugin was independently observed loaded, enabled in
  the default collection, and at least at the requested package version.
- `failed`: read the error, plan and verified partial results. A repository or an
  earlier dependency may already have been installed. Existing state is not
  blindly rolled back or removed.
- `cancelled`: MCP unloaded. A native operation already started is awaited and
  may finish; inspect actual installed state before retrying.

The normal default repository is `https://spacegho.st/mods/ffxiv/plugins.json`.
Character and Vitals remain development builds outside that normal catalogue.
Selecting their separate development feed explicitly allows automated installation;
it does not constitute live acceptance or turn them into stable releases.

`get_plugin_setup_plan` accepts the same arguments and only reads the catalogue
and current installed state. It never changes configuration, opens UI or downloads
plugin binaries. It can explain missing packages and conflicting installations
before starting setup. `add_plugin_repository` now performs real registration and
refresh; its legacy `settingsOpened` and `copiedToClipboard` fields remain false.

## Discovery and dependencies

Newly published internal names need no MCP code or hardcoded allowlist. Put a
valid Dalamud manifest and ZIP links in the selected pluginmaster catalogue.
Bundled dependency DLLs are installed by Dalamud's own package installer.
If a plugin needs another separately installed plugin from the same catalogue,
add `"XivMcpDependencies": ["Other.InternalName"]`. This optional catalogue
extension is resolved before any mutation; dependencies are installed once,
first, and cycles/missing dependencies are rejected. Optional window hosts and
controller brokers should not be declared as mandatory dependencies.

Choose 1–12 unique root names; at most 24 plugins including dependencies may be
included. Catalogues are bounded to 2 MiB and 2048 manifests. Names cannot contain
paths. URLs must be explicit HTTPS without embedded credentials or fragments.
`testing` uses a published testing package when available, otherwise stable;
`stable` refuses testing-exclusive packages. Testing preferences are recorded for
selected plugins; the global testing switch is enabled only when needed and is
never turned off for unrelated plugins.

## Consent, compatibility and recovery

Setup uses the existing Action permission and the owner's normal confirmation
switch/rules. With confirmation disabled or an applicable owner rule, it runs
without manual steps. It does not create a second approval system. The approval
summary discloses plugin names, repository, channel and executable code download.
Plan/status reads remain Read tier.

All native reflection bindings are enumerated and checked against the reference
assemblies. Native repository validation, bans, exact API compatibility, safe
mode and default-collection ownership still apply. Setup does not rewrite foreign
collections, replace a plugin from another source, downgrade versions, overwrite
development copies or guess among duplicate installations. Existing plugin
settings are preserved. The running MCP server cannot safely replace itself in
its own operation; the external host deploys that server build.

Only one setup runs at once. Identical in-flight requests reuse the same id;
different ones fail with a retryable busy result. The latest 32 operations are
kept until MCP reload. Native downloads/loads run off the framework thread, while
configuration and state access use the framework scheduler. Cancellation stops
before the next native operation, not halfway through writing an installation.

If a plugin failed to load, `list_plugins`, `read_plugin_log` and the operation's
partial results explain what exists. Retry after correcting the reported cause;
already-loaded current packages remain unchanged. `set_plugin_enabled` can disable
an installed plugin. Native HUD recovery remains `/xivhud off`; Vitals recovery
is `/xivvitals close`.

## Validation

Host tests cover dependency planning, channel selection, version convergence,
source/dev/collection preservation, no mutations after failed preflight, queued
versus verified results, partial failure, repeated calls, bounded history and
unload cancellation. Metadata checks cover the installer/configuration bindings
against Dalamud 15.0.3.6 and the current local 15.0.3.9 references. Live installation
and HUD rendering checks are recorded separately; host tests alone do not prove
in-game operation.

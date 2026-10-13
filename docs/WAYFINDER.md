# Shared journey guidance

Optional integration with XivWayfinder's version-1 itinerary IPC, without an
assembly dependency. These tools are discovered in the existing `world`
category:

- `get_wayfinder_journey`: read the saved stops and current guidance together.
- `append_wayfinder_stops`: append a validated batch after normal action approval.
- `control_wayfinder_journey`: explicitly resume, pause, advance or go back,
  after normal action approval.

Suggested flow: inspect the existing itinerary, collect world-coordinate
destinations from the relevant quest/place data, append only missing stops,
then resume guidance when requested. Re-read after a game update to obtain the
new current action and teleport hint. Do not treat map coordinates as world
coordinates. Do not assume a nearby quest marker means its interaction is done.

Appending does not start the journey, replace the player's list, execute a
teleport or move the character. Append/advance are not idempotent: inspect the
itinerary before retrying an uncertain result. Missing/older Wayfinder produces
an unavailable error; the MCP server remains usable without it.

Wayfinder now has an opt-in automatic-Teleport policy. Read the guidance state
before resuming or changing its active stop: if enabled by the player, the new
guidance may start Teleport and spend gil within the configured limit. The
control tool's action approval discloses this. No walking automation is added.

The bridge is implemented and host-tested, not yet verified in the live game.
It does not yet synchronize objective IDs or automatically refresh moving quest
destinations. Existing objective-board completion remains separate from travel
progress, deliberately avoiding false task completion on arrival.

# Building this Add-In

## What's here vs. what you need to generate

This repo has the *logic* (`Module1.cs`, `BridgeService.cs`, `IpcModels.cs`,
`Config.daml`) but not a hand-rolled `.csproj` — Esri's MSBuild targets that
package a `.esriAddinX` (and the default icon resources a project needs) are
easiest to get right from Visual Studio's own template, not by hand-authoring
project files blind. So:

1. Install **Visual Studio 2022 (17.13+)**, then **Extensions → Manage Extensions →
   search "ArcGIS Pro SDK" → Install**. Restart VS if prompted.
2. **New Project → "ArcGIS Pro Module Add-in"** (under the ArcGIS category). Name it
   `MCPArcGISProAddIn` (matching `defaultAssembly`/`defaultNamespace` in `Config.daml`
   here — rename in `Config.daml` too if you pick a different name).
3. **Replace** the generated `Config.daml` and `Module1.cs` with the ones in this repo.
4. **Add** `BridgeService.cs` and `IpcModels.cs` to the project (copy the files in,
   or add as links).
5. Open `Config.daml` in the designer once (or just build) — accept the auto-generated
   GUID it fills in for `AddInInfo id`, replacing the placeholder in this repo's copy.
6. Build. ArcGIS Pro should auto-load the add-in next time it starts (or immediately,
   if Pro was already running when you built — Pro's add-in reload behavior varies).

## Testing

1. Open ArcGIS Pro, open (or create) a project with at least one map.
2. From this repo's folder: `python test_client.py ping` — should print
   `{'Ok': True, 'Data': 'pong', 'Error': None}` within a few ms if the listener's up.
3. `python test_client.py open_view` — should open a pane for the active/first map.
   `python test_client.py open_view "SomeMapName"` targets a specific map by name.

If `ping` fails: check the add-in actually loaded (ArcGIS Pro → Add-In Manager, or
just check for build errors). If `open_view` fails specifically: that's the one part
of `BridgeService.cs` that's genuinely unverified (the WPF-dispatcher hop for
`OpenMapPaneAsync`, see the comment in that file) — start debugging there.

## Known gaps in this V1 (by design, not oversight)

- No Start/Stop ribbon button — add-in starts the listener automatically on load.
  Add a button via VS's Add-In Designer once the core mechanism is confirmed working;
  the designer generates its own icon resources, safer than hand-authoring image paths.
- One client at a time, no concurrent request handling, no auth on the pipe (matches
  this project's whole trust model: a local, single-user desktop tool, same as
  MCP-ArcGISPro's file/socket IPC).
- `open_view` and `ping` only. Nothing else yet — see the main README for the bigger
  picture (`execute_csharp`-style escape hatch, hosting MCP directly) once this works.

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
3. Copy the generated project's **`.csproj`, `Images/`, and `DarkImages/`** into this
   repo's folder (the generated `.csproj` explicitly requires both icon folders, not
   just `Images/` — missing `DarkImages/` gives `MSB3030` "could not copy" warnings).
4. **Delete** the freshly-generated `Config.daml` and `Module1.cs` — keep this repo's
   versions (they have the actual bridge logic, not the template's defaults).
5. `BridgeService.cs` and `IpcModels.cs` don't need any manual "Add Existing Item" step
   — modern SDK-style `.csproj` files auto-include every `.cs` file in the folder.
6. Open `Config.daml` in the designer once (or just build) — accept the auto-generated
   GUID it fills in for `AddInInfo id`, replacing the placeholder in this repo's copy.
7. Add `<Nullable>enable</Nullable>` to the copied `.csproj`'s first `<PropertyGroup>`
   if it's not already there (avoids `CS8632` warnings on this repo's `?` annotations).
8. Build. Restart ArcGIS Pro afterward if it was already open — add-ins load at
   startup, not hot-reloaded while running.

## Testing

1. Open ArcGIS Pro, open (or create) a project with at least one map.
2. From this repo's folder: `python test_client.py ping` — should print
   `{'Ok': True, 'Data': 'pong', 'Error': None}` within a few ms if the listener's up.
3. `python test_client.py open_view` — should open a pane for the active/first map.
   `python test_client.py open_view "SomeMapName"` targets a specific map by name.

If `ping` fails: check the add-in actually loaded (ArcGIS Pro → Add-In Manager, or
just check for build errors).

**Both confirmed working, live-tested (2026-09-06):**
```
> python test_client.py ping
{'Ok': True, 'Error': None, 'Data': 'pong'}  (36ms)

> python test_client.py open_view
{'Ok': True, 'Error': None, 'Data': "Opened view for map 'Map'."}  (1788ms)
```
The WPF-dispatcher hop for `OpenMapPaneAsync` (see the comment in `BridgeService.cs`)
was the one piece flagged as genuinely unverified going in — it worked correctly on
the first live try.

## Known gaps in this V1 (by design, not oversight)

- No Start/Stop ribbon button — add-in starts the listener automatically on load.
  Add a button via VS's Add-In Designer once the core mechanism is confirmed working;
  the designer generates its own icon resources, safer than hand-authoring image paths.
- One client at a time, no concurrent request handling, no auth on the pipe (matches
  this project's whole trust model: a local, single-user desktop tool, same as
  MCP-ArcGISPro's file/socket IPC).
- `open_view` and `ping` only. Nothing else yet — see the main README for the bigger
  picture (`execute_csharp`-style escape hatch, hosting MCP directly) once this works.

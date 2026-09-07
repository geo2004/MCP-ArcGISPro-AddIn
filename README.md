# MCP-ArcGISPro-AddIn

A native C# ArcGIS Pro Add-In that hosts an MCP server directly inside the Pro process —
a companion to [MCP-ArcGISPro](https://github.com/Geo2004/MCP-ArcGISPro), for the things
the Python bridge genuinely can't reach: anything that needs a live, open window.

## Why this exists

[MCP-ArcGISPro](https://github.com/Geo2004/MCP-ArcGISPro) is a working, pure-Python MCP
bridge for ArcGIS Pro — no C#, no compilation, fast to iterate on. It covers headless
project/data/geoprocessing operations well, and stays as-is: this repo doesn't replace it.

What's architecturally out of reach for arcpy, no matter what:

- **Anything touching a live view or pane** — opening/closing/activating a map view,
  reading or changing its current extent, zooming, interactive selection. arcpy's
  document API has no concept of an open window at all.
- **Project-lifecycle operations** — creating, opening, and saving whole `.aprx`
  projects touches ArcGIS Pro's own application-shell UI internally (dock panes), not
  just CIM data.
- Operations that need the app's true main thread (COM/CIM session state) rather than a
  background thread — `arcpy.server`/`arcpy.mp.CreateWebLayerSDDraft` publishing hit
  exactly this wall on the Python side (worked around there via REST, the `arcgis`
  package, but not every case has a REST escape hatch).

This repo is for those specific gaps, using ArcGIS Pro's native `QueuedTask.Run` (for
CIM/data access) and the WPF dispatcher (for anything UI/pane-affinity) to safely marshal
calls onto the right thread — mechanisms C# Add-Ins get for free that plain arcpy has no
equivalent for.

## Status

✅ **Live and working, 42 tools verified end-to-end.** MCP is hosted directly inside
the Add-in's own process via the official
[C# MCP SDK](https://github.com/modelcontextprotocol/csharp-sdk) — Kestrel listens on
`http://localhost:5057/`.

**Project & map management:**
`ping`, `get_status`, `open_project`, `create_project`, `save_project`,
`insert_map`, `insert_scene`, `list_maps`, `open_view`, `undo`, `redo`

**Layer management:**
`list_layers`, `add_layer`, `remove_layer`

**View/pane interactivity** — the part with no arcpy equivalent at all:
`list_open_views`, `activate_view`, `close_view`, `get_view_extent`, `zoom_to_extent`,
`zoom_in`, `zoom_out`, `select_by_extent`, `export_view`, `list_bookmarks`,
`add_bookmark`, `zoom_to_bookmark`, `get_camera`, `set_camera`

**Application interaction** — also arcpy-unreachable:
`show_message`, `activate_tool`, `get_current_tool`

**Data editing** — via `EditOperation`, not raw cursor writes, so these participate
in the same undo/redo stack as everything else here:
`create_feature`, `update_feature_geometry`, `update_feature_attributes`,
`delete_feature`, `save_edits`, `discard_edits`. Point geometry only for now.

**Snapping, selection, and tables** — more arcpy-unreachable interactive state:
`set_snapping`, `get_selected_features` (reads back whatever's selected, including
selections a human made by clicking), `open_table`, `close_table`, `list_open_tables`

`export_view` in particular is worth knowing about: it renders a view to a PNG file,
which is the only way to actually see what's on screen in a live ArcGIS Pro
window from outside the process.

Every tool auto-registers via `[McpServerTool]` on a plain static method in
`McpTools.cs` — no manual wiring needed to add a new one.

### Trying it yourself

Talk to it as plain MCP-over-HTTP, e.g.:

```bash
curl -s http://localhost:5057/ -H "Content-Type: application/json" \
  -H "Accept: application/json, text/event-stream" \
  -d '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"test","version":"1.0"}}}'
# -> grab the Mcp-Session-Id response header, then:
curl -s http://localhost:5057/ -H "Content-Type: application/json" \
  -H "Accept: application/json, text/event-stream" -H "Mcp-Session-Id: <id>" \
  -d '{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"ping","arguments":{}}}'
```

Or connect a real MCP client. For Claude Code:

```bash
claude mcp add --transport http mcp-arcgispro-addin http://localhost:5057/ -s user
claude mcp list   # should show mcp-arcgispro-addin ... ✓ Connected
```

Takes effect in new Claude Code sessions after adding it (not the one you ran the
command from). For Claude Desktop's own chat interface, add it through Settings →
Connectors instead — same URL, that app doesn't read `claude mcp`'s config.

**Two things worth knowing before debugging further:**
- Every tool call's real exception (not the MCP SDK's generic "An error occurred
  invoking 'x'") lands in `%LOCALAPPDATA%\MCPArcGISProAddIn_error.log` — check that
  first, always.
- `open_project` does **not** auto-open a view, and every ArcGIS Pro restart returns to
  the Home screen with nothing open. Call `get_status` before assuming any state.

**Next, not yet started:** an `execute_csharp`-style escape hatch (mirrors
`execute_python`'s trust model on the Python side), pushing further into editing/
geoprocessing/layout capabilities, or eventually converging with the Python bridge into
one combined architecture.

## Requirements

- ArcGIS Pro 3.x (targeting 3.6.1 — .NET 8)
- Visual Studio 2022 17.13+
- **ArcGIS Pro SDK for .NET — get the exact version matching your Pro release, not
  whatever the Extension Manager's Install button defaults to.** The VS Marketplace
  serves the newest SDK build (e.g. 3.7.x, which requires VS 2026) regardless of which
  Pro version you actually have. For Pro 3.6.x specifically, download
  `proapp-sdk-templates.vsix` from the matching tag on the
  [SDK's GitHub releases](https://github.com/Esri/arcgis-pro-sdk/releases) (e.g.
  `3.6.0.59527`) and install that file directly.
- **`ModelContextProtocol`/`ModelContextProtocol.AspNetCore` pinned to `0.4.0-preview.3`
  specifically — do not casually bump this.** Every version from `0.4.1-preview.1`
  onward (including the current stable 2.x line) is compiled against
  `Microsoft.Extensions.*`/`System.*` v10, which conflicts with the older versions
  ArcGIS Pro's own process already has loaded (a hard `AssemblyRef`, not a loose
  version range — no amount of `PackageReference` pinning fixes it once it's already
  compiled in). If a newer SDK release is tempting later, check its `net8.0` dependency
  group's actual pinned versions on NuGet first.
- Hosting Kestrel as a plugin inside an already-running host process
  (`ArcGISPro.exe`) needs a manual `AssemblyLoadContext.Resolving` hook in
  `McpHostService`'s static constructor — the host's own runtime never declared the
  `Microsoft.AspNetCore.App` shared framework, so those assemblies won't resolve on
  their own. See the comments there before touching it.

## Building a release

Build with `-p:Configuration=Release` instead of the default Debug. Signing an
`.esriAddinX` needs the **Esri Digital Signature Wizard** (`ArcGISSignAddIn.exe` in
the Pro `bin` folder) — it's GUI-only, no command-line flags. It needs a code-signing
certificate already sitting in the Windows Certificate Store; a self-signed one works
fine and doesn't require paying for a commercial certificate:

```powershell
New-SelfSignedCertificate -Type CodeSigningCert -Subject "CN=Your Name" `
  -CertStoreLocation "Cert:\CurrentUser\My" -KeyUsage DigitalSignature `
  -FriendlyName "Your Name" -NotAfter (Get-Date).AddYears(5)
```

Then run the wizard against the built `.esriAddinX`, pick that certificate, and finish.
Re-register the *signed* file directly afterward (`RegisterAddIn.exe <path> /s`) rather
than rebuilding, which would silently overwrite it with an unsigned copy again.

A self-signed certificate shows up in Pro's Add-In Manager as **"Untrusted"**, not
"None" — that's expected and correct. It proves the package wasn't tampered with and
identifies the signer, but won't show as fully trusted on any machine unless that
machine explicitly imports the certificate into its own Trusted Publishers store. A
paid CA-issued certificate is what removes the trust prompt everywhere by default;
not needed for personal or small-audience use.

## Related

- [MCP-ArcGISPro](https://github.com/Geo2004/MCP-ArcGISPro) — the Python bridge this
  complements. Start there for anything that doesn't specifically need a live window.

## License

MIT

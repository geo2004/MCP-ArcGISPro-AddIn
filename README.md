# MCP-ArcGISPro-AddIn

A native C# ArcGIS Pro Add-In exposing Model Context Protocol (MCP) tools — a companion
to [MCP-ArcGISPro](https://github.com/Geo2004/MCP-ArcGISPro), for the things the Python
bridge genuinely can't reach.

## Why this exists

[MCP-ArcGISPro](https://github.com/Geo2004/MCP-ArcGISPro) is a working, pure-Python MCP
bridge for ArcGIS Pro — no C#, no compilation, fast to iterate on. It covers a wide range
of operations well, and stays as-is: this repo doesn't replace it.

Some things are architecturally out of reach for a Python-only approach, though:

- **Opening a map/scene/globe view programmatically** — that's an application-shell
  action that lives in the compiled ArcGIS Pro Framework/SDK, not the arcpy document API.
- Operations that need the app's true main thread (COM/CIM session state) rather than a
  background thread. `arcpy.server`/`arcpy.mp.CreateWebLayerSDDraft` publishing hit
  exactly this wall on the Python side — worked around there via REST (the `arcgis`
  package) instead of the C# SDK, but not every future case will have a REST escape hatch.

This repo is for those specific gaps, using ArcGIS Pro's native `QueuedTask.Run` to
safely marshal calls onto the main thread — the mechanism C# Add-Ins get for free that
plain arcpy has no equivalent for.

## Status

✅ **V1 works, live-tested.** Opening a map view on command — proven end-to-end:
Python client → named pipe → `BridgeService` → WPF dispatcher hop → `QueuedTask.Run` →
`OpenMapPaneAsync`. ArcGIS Pro actually opens the view. See `DEV-NOTES.md` for build
instructions and `test_client.py` for how to try it yourself.

```
> python test_client.py ping
{'Ok': True, 'Error': None, 'Data': 'pong'}  (36ms)

> python test_client.py open_view
{'Ok': True, 'Error': None, 'Data': "Opened view for map 'Map'."}  (1788ms)
```

**Next, not yet started:** an `execute_csharp`-style escape hatch (mirrors
`execute_python`'s trust model on the Python side, avoids needing a recompile for every
new main-thread-only capability), or hosting an MCP server directly inside the Add-in
via the [official MCP C# SDK](https://github.com/modelcontextprotocol/csharp-sdk) so
Claude could talk to it directly with no separate Python process in between.

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

## Related

- [MCP-ArcGISPro](https://github.com/Geo2004/MCP-ArcGISPro) — the Python bridge this
  complements. Start there for anything that doesn't specifically need this repo.

## License

MIT

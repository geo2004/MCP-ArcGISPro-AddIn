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

🚧 Early planning / scaffolding. Nothing functional yet.

**Planned V1 scope:** a minimal Add-in that can open a map/scene/globe view on command,
proving the `QueuedTask.Run` + IPC mechanism end-to-end, before attempting anything
broader (an `execute_csharp`-style escape hatch, or hosting an MCP server directly
inside the Add-in via the [official MCP C# SDK](https://github.com/modelcontextprotocol/csharp-sdk)).

## Requirements

- ArcGIS Pro 3.x (targeting 3.6.1 — .NET 8)
- Visual Studio 2022 17.13+
- ArcGIS Pro SDK for .NET (Visual Studio → Extensions → Manage Extensions → search
  "ArcGIS Pro SDK" → Install)

## Related

- [MCP-ArcGISPro](https://github.com/Geo2004/MCP-ArcGISPro) — the Python bridge this
  complements. Start there for anything that doesn't specifically need this repo.

## License

MIT

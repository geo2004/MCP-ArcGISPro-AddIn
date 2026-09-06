# Building this Add-In

## Build

1. Install **Visual Studio 2022 (17.13+)** and the **ArcGIS Pro SDK for .NET** matching
   your installed Pro version (see the main README — the Extension Manager's default
   install often doesn't match).
2. Open `MCPArcGISProAddIn.sln` and build.
3. Restart ArcGIS Pro if it was already open — add-ins load at startup, not
   hot-reloaded while running.

## Testing

1. Open ArcGIS Pro, open a project with at least one map.
2. Talk to the MCP server over HTTP as described in the main README's "Trying it
   yourself" section.

If a tool call fails, check `%LOCALAPPDATA%\MCPArcGISProAddIn_error.log` first — the
MCP SDK's own error message to the client is generic and won't tell you what actually
went wrong.

## Known gaps

- No Start/Stop ribbon button — the add-in starts the MCP host automatically on load.
- Single client, no auth — matches this project's trust model (a local, single-user
  desktop tool).
- See the main README for the current tool list and what's planned next.

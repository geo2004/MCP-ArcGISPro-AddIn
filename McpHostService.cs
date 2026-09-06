using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MCPArcGISProAddIn
{
    /// <summary>
    /// Hosts the MCP server directly inside this Add-in's process via Kestrel/ASP.NET
    /// Core, using the official ModelContextProtocol.AspNetCore SDK. Claude Desktop/
    /// Code connects straight to this over HTTP -- no separate Python process, no
    /// custom IPC protocol. Replaces the earlier named-pipe BridgeService.
    ///
    /// Confidence note: AddMcpServer/WithHttpTransport/WithToolsFromAssembly/MapMcp
    /// are confirmed against the SDK's own current docs. Running a WebApplication via
    /// RunAsync() from inside an existing WPF host process (rather than a dedicated
    /// console app's Main) is a documented pattern elsewhere (e.g. Rick Strahl's
    /// Westwind.AspNetCore.HostedWebServer), but this exact combination -- inside an
    /// ArcGIS Pro Add-in specifically -- was compile-tested via `dotnet build`, not
    /// yet run live inside ArcGIS Pro. Watch this file first if the endpoint doesn't
    /// come up cleanly.
    /// </summary>
    internal class McpHostService
    {
        private const int Port = 5057;
        private WebApplication? _app;

        public void Start()
        {
            if (_app != null) return; // already running

            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders(); // ArcGIS Pro owns the console/output, not us

            builder.Services
                .AddMcpServer()
                .WithHttpTransport()
                .WithToolsFromAssembly();

            _app = builder.Build();
            _app.Urls.Add($"http://localhost:{Port}");
            _app.MapMcp();

            _ = _app.RunAsync();
        }

        public void Stop()
        {
            _ = _app?.StopAsync();
            _app = null;
        }
    }
}

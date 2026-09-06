using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading.Tasks;
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

        private static readonly AssemblyDependencyResolver _resolver =
            new AssemblyDependencyResolver(typeof(McpHostService).Assembly.Location);

        private static readonly string? _aspNetCoreSharedDir = FindAspNetCoreSharedDir();

        static McpHostService()
        {
            // We're loaded as a plugin inside ArcGISPro.exe's own already-running
            // process. Its runtime config never declared the Microsoft.AspNetCore.App
            // shared framework, so the CLR can't find those assemblies on its own even
            // though they're installed on the machine -- this unhandled
            // FileNotFoundException ("Microsoft.AspNetCore") is what crashed Pro before
            // Module1's SafeCall wrapper caught it.
            AssemblyLoadContext.Default.Resolving += (context, name) =>
            {
                // NuGet-package-based plugin deps (Microsoft.Extensions.*, ModelContextProtocol*)
                // resolve fine via our own .deps.json.
                var path = _resolver.ResolveAssemblyToPath(name);
                if (path != null) return context.LoadFromAssemblyPath(path);

                // But Microsoft.AspNetCore.App is a FrameworkReference, never restored as a
                // local package with a path in .deps.json -- AssemblyDependencyResolver can't
                // help there. Load it straight from the shared-framework folder on disk.
                if (_aspNetCoreSharedDir != null && name.Name != null)
                {
                    var candidate = Path.Combine(_aspNetCoreSharedDir, name.Name + ".dll");
                    if (File.Exists(candidate)) return context.LoadFromAssemblyPath(candidate);
                }

                return null;
            };
        }

        private static string? FindAspNetCoreSharedDir()
        {
            try
            {
                // typeof(object) resolves to .../shared/Microsoft.NETCore.App/<version>/System.Private.CoreLib.dll
                // -- two levels up is "shared", whose sibling "Microsoft.AspNetCore.App" holds
                // the framework we need. (Off-by-one caught here: GetDirectoryName once only
                // reaches the "Microsoft.NETCore.App" folder, not "shared".)
                var versionDir = Path.GetDirectoryName(typeof(object).Assembly.Location);
                var netCoreAppDir = Path.GetDirectoryName(versionDir);
                var sharedDir = Path.GetDirectoryName(netCoreAppDir);
                if (sharedDir == null) return null;

                var aspNetRoot = Path.Combine(sharedDir, "Microsoft.AspNetCore.App");
                if (!Directory.Exists(aspNetRoot)) return null;

                // Match the ASP.NET Core folder to the CoreCLR version actually running
                // (this machine also has .NET 9 installed side by side -- grabbing the
                // highest version overall picked 9.0.x's Microsoft.AspNetCore.App, whose
                // own System.Runtime dependency then failed to resolve against our net8.0
                // add-in's runtime).
                var runtimeVersion = Path.GetFileName(versionDir) ?? "";
                var majorMinor = string.Join(".", runtimeVersion.Split('.').Take(2)) + ".";

                var candidates = Directory.GetDirectories(aspNetRoot);
                return candidates.Where(d => Path.GetFileName(d).StartsWith(majorMinor))
                                  .OrderByDescending(d => d)
                                  .FirstOrDefault()
                       ?? candidates.OrderByDescending(d => d).FirstOrDefault();
            }
            catch
            {
                return null;
            }
        }

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

            // With logging providers cleared above, an unhandled request exception was
            // otherwise a bare 500 with an empty body and zero trace anywhere -- log it
            // to the same file SafeCall uses, then let it propagate so Kestrel's default
            // behavior (500) is unchanged.
            _app.Use(async (context, next) =>
            {
                try
                {
                    await next();
                }
                catch (Exception ex)
                {
                    LogError($"Unhandled request exception ({context.Request.Path})", ex);
                    throw;
                }
            });

            _app.Urls.Add($"http://localhost:{Port}");
            _app.MapMcp();

            // RunAsync() is fire-and-forget by design (Start() must return immediately
            // so Module1.Initialize() doesn't block ArcGIS Pro's own startup) -- but an
            // UNOBSERVED exception in a discarded Task can crash the whole process on
            // its own, separately from anything try/catch around Start() itself would
            // ever see. Observe it explicitly instead of just discarding it.
            _ = RunAndLogAsync();
        }

        private async Task RunAndLogAsync()
        {
            try
            {
                await _app!.RunAsync();
            }
            catch (Exception ex)
            {
                LogError("RunAsync", ex);
            }
        }

        internal static void LogError(string what, Exception ex)
        {
            try
            {
                var path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "MCPArcGISProAddIn_error.log");
                File.AppendAllText(path,
                    $"[{DateTime.Now:O}] {what} failed:{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
            }
            catch
            {
                // Nothing safe left to do if even logging fails.
            }
        }

        public void Stop()
        {
            _ = _app?.StopAsync();
            _app = null;
        }
    }
}

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ArcGIS.Desktop.Core;
using ArcGIS.Desktop.Framework;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Mapping;

namespace MCPArcGISProAddIn
{
    /// <summary>
    /// The actual ArcGIS Pro operations, independent of how they're invoked. MCP tool
    /// methods (McpTools.cs) call straight into these now -- there used to be a
    /// named-pipe protocol (BridgeService.cs) in between, retired in favor of hosting
    /// MCP directly in this process. Same logic that was already live-tested working
    /// on 2026-09-06, just called directly instead of through a hand-rolled pipe.
    /// </summary>
    internal static class ArcGisOperations
    {
        public static Task<string> PingAsync() => Task.FromResult("pong");

        /// <summary>
        /// Opens a pane for the named map (or the active/first map if mapName is
        /// empty). The one thing this whole Add-In exists for -- arcpy has no
        /// equivalent, at all, on the Python side.
        /// </summary>
        public static async Task<string> OpenViewAsync(string mapName)
        {
            MapProjectItem? mapItem = await QueuedTask.Run(() =>
            {
                var items = Project.Current.GetItems<MapProjectItem>();

                if (!string.IsNullOrEmpty(mapName))
                {
                    return items.FirstOrDefault(m =>
                        m.Name.Equals(mapName, StringComparison.OrdinalIgnoreCase));
                }

                // No name given: prefer whatever's already the active map, else just
                // the first map in the project.
                var activeMapName = MapView.Active?.Map?.Name;
                if (!string.IsNullOrEmpty(activeMapName))
                {
                    var active = items.FirstOrDefault(m =>
                        m.Name.Equals(activeMapName, StringComparison.OrdinalIgnoreCase));
                    if (active != null) return active;
                }
                return items.FirstOrDefault();
            });

            if (mapItem == null)
            {
                throw new InvalidOperationException(
                    string.IsNullOrEmpty(mapName)
                        ? "No maps exist in this project."
                        : $"No map named '{mapName}' found in this project.");
            }

            // OpenMapPaneAsync must run on ArcGIS Pro's UI thread specifically (per
            // the SDK docs) -- QueuedTask.Run marshals onto a *different* thread
            // (safe for CIM/data access, not the same as the UI thread), so this
            // needs its own hop via the WPF dispatcher. Confirmed working live.
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null)
            {
                throw new InvalidOperationException("No WPF dispatcher available (unexpected outside ArcGIS Pro).");
            }

            await dispatcher.InvokeAsync(() => mapItem.OpenMapPaneAsync()).Task.Unwrap();

            return $"Opened view for map '{mapItem.Name}'.";
        }

        /// <summary>
        /// Creates a brand-new ArcGIS Pro project on disk, without a template -- the
        /// same as clicking "Start without a template" on the Home screen. Another
        /// arcpy-can't-do-this: creating/closing whole projects is an application-level
        /// operation, not a geoprocessing one.
        /// </summary>
        public static async Task<string> CreateProjectAsync(string projectName, string folderPath)
        {
            if (string.IsNullOrWhiteSpace(projectName))
                throw new ArgumentException("projectName is required.");

            if (string.IsNullOrWhiteSpace(folderPath))
            {
                folderPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "ArcGIS", "Projects");
            }

            Directory.CreateDirectory(folderPath);

            var settings = new CreateProjectSettings
            {
                Name = projectName,
                LocationPath = folderPath
            };

            // Turns out Project.CreateAsync needs the UI thread too (it touches Pro's
            // own dock panes while setting up the new project) -- calling it directly
            // from a Kestrel request thread failed with "Could not find Project Dock
            // Pane". A normal add-in button handler is already on the UI thread, which
            // is why this doesn't show up in the SDK's own samples; we aren't, so we
            // need the same dispatcher hop as the pane-opening calls.
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null)
            {
                throw new InvalidOperationException("No WPF dispatcher available (unexpected outside ArcGIS Pro).");
            }

            var project = await dispatcher.InvokeAsync(() => Project.CreateAsync(settings)).Task.Unwrap();

            return $"Created project '{projectName}' at {project.URI}.";
        }

        /// <summary>
        /// Inserts a brand-new, blank map into the current project and opens its view --
        /// the CIM-creation half (QueuedTask) and the pane-opening half (WPF dispatcher)
        /// are two different threading requirements chained back to back, confirmed live.
        /// </summary>
        public static async Task<string> InsertMapAsync(string mapName)
        {
            if (Project.Current == null)
                throw new InvalidOperationException("No project is open. Call create_project first.");

            if (string.IsNullOrWhiteSpace(mapName))
                mapName = "Map";

            Map map = await QueuedTask.Run(() =>
                MapFactory.Instance.CreateMap(mapName, basemap: Basemap.None));

            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null)
            {
                throw new InvalidOperationException("No WPF dispatcher available (unexpected outside ArcGIS Pro).");
            }

            await dispatcher.InvokeAsync(() => ProApp.Panes.CreateMapPaneAsync(map)).Task.Unwrap();

            return $"Inserted and opened new map '{map.Name}'.";
        }
    }
}

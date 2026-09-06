using System;
using System.Linq;
using System.Threading.Tasks;
using ArcGIS.Desktop.Core;
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
    }
}

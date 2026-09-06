using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ArcGIS.Core.Geometry;
using ArcGIS.Desktop.Core;
using ArcGIS.Desktop.Framework;
using ArcGIS.Desktop.Framework.Contracts;
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
        /// Structured snapshot of what's actually going on right now -- project open?
        /// which maps exist? which views are open, which is active? Call this before
        /// any state-dependent operation instead of guessing: every Pro restart returns
        /// to the Home screen with nothing open, and a wrong assumption here is exactly
        /// what produced a confusing generic error from add_layer once already.
        /// </summary>
        public static async Task<string> GetStatusAsync()
        {
            if (Project.Current == null)
                return "No project is open.";

            var mapNames = await QueuedTask.Run(() =>
                Project.Current.GetItems<MapProjectItem>().Select(m => m.Name).ToList());

            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null)
                throw new InvalidOperationException("No WPF dispatcher available (unexpected outside ArcGIS Pro).");

            var openViewNames = await dispatcher.InvokeAsync(() =>
                ProApp.Panes.OfType<IMapPane>()
                    .Select(p => p.MapView?.Map?.Name)
                    .Where(n => !string.IsNullOrEmpty(n))
                    .ToList());

            var activeViewName = await dispatcher.InvokeAsync(() => MapView.Active?.Map?.Name);

            return $"Project: '{Project.Current.Name}' ({Project.Current.URI}). " +
                   $"Maps: {(mapNames.Count > 0 ? string.Join(", ", mapNames) : "none")}. " +
                   $"Open views: {(openViewNames.Count > 0 ? string.Join(", ", openViewNames) : "none")}. " +
                   $"Active view: {activeViewName ?? "none"}.";
        }

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
        /// Opens an existing .aprx project on disk. Same family as CreateAsync/SaveAsync
        /// -- expect the dispatcher hop to be needed here too.
        /// </summary>
        public static async Task<string> OpenProjectAsync(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("path is required.");

            if (!File.Exists(path))
                throw new InvalidOperationException($"No .aprx file found at '{path}'.");

            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null)
            {
                throw new InvalidOperationException("No WPF dispatcher available (unexpected outside ArcGIS Pro).");
            }

            var project = await dispatcher.InvokeAsync(() => Project.OpenAsync(path)).Task.Unwrap();

            return $"Opened project '{project.Name}' from {path}.";
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

        /// <summary>Must be called from inside QueuedTask.Run. Named map, else the active map, else the first map in the project.</summary>
        private static Map? ResolveMap(string mapName)
        {
            if (!string.IsNullOrEmpty(mapName))
            {
                var item = Project.Current.GetItems<MapProjectItem>()
                    .FirstOrDefault(m => m.Name.Equals(mapName, StringComparison.OrdinalIgnoreCase));
                return item?.GetMap();
            }

            if (MapView.Active?.Map != null) return MapView.Active.Map;

            return Project.Current.GetItems<MapProjectItem>().FirstOrDefault()?.GetMap();
        }

        /// <summary>Lists every map in the current project -- pure CIM/catalog read, no dispatcher hop expected.</summary>
        public static async Task<string> ListMapsAsync()
        {
            if (Project.Current == null)
                throw new InvalidOperationException("No project is open.");

            var names = await QueuedTask.Run(() =>
                Project.Current.GetItems<MapProjectItem>().Select(m => m.Name).ToList());

            return names.Count > 0 ? string.Join(", ", names) : "No maps in this project.";
        }

        /// <summary>Lists every layer in a map (named, or the active one) -- pure CIM read, no dispatcher hop expected.</summary>
        public static async Task<string> ListLayersAsync(string mapName)
        {
            if (Project.Current == null)
                throw new InvalidOperationException("No project is open.");

            var layerNames = await QueuedTask.Run(() =>
            {
                var map = ResolveMap(mapName);
                return map?.GetLayersAsFlattenedList().Select(l => l.Name).ToList();
            });

            if (layerNames == null)
                throw new InvalidOperationException("No map found to list layers from.");

            return layerNames.Count > 0 ? string.Join(", ", layerNames) : "That map has no layers.";
        }

        /// <summary>Adds a layer from a data path to a map -- pure CIM write, no dispatcher hop expected.</summary>
        public static async Task<string> AddLayerAsync(string dataPath, string mapName)
        {
            if (string.IsNullOrWhiteSpace(dataPath))
                throw new ArgumentException("dataPath is required.");

            if (Project.Current == null)
                throw new InvalidOperationException("No project is open.");

            // Confirmed live: forward slashes make Esri's URI-based geodatabase-path
            // parsing (splitting "...\x.gdb" from "\FeatureClassName" inside it) fail
            // with a misleading "unsupported data type" -- even though the exact same
            // path with backslashes works. Normalize regardless of what the caller passed.
            dataPath = dataPath.Replace('/', '\\');

            var layerName = await QueuedTask.Run(() =>
            {
                var map = ResolveMap(mapName);
                if (map == null) return null;

                var layer = LayerFactory.Instance.CreateLayer(new Uri(dataPath), map);
                return layer?.Name;
            });

            if (layerName == null)
                throw new InvalidOperationException("Could not add the layer (no map found, or the data path is invalid).");

            return $"Added layer '{layerName}'.";
        }

        /// <summary>Removes a named layer from a map -- pure CIM write, no dispatcher hop expected.</summary>
        public static async Task<string> RemoveLayerAsync(string layerName, string mapName)
        {
            if (string.IsNullOrWhiteSpace(layerName))
                throw new ArgumentException("layerName is required.");

            if (Project.Current == null)
                throw new InvalidOperationException("No project is open.");

            var removed = await QueuedTask.Run(() =>
            {
                var map = ResolveMap(mapName);
                var layer = map?.GetLayersAsFlattenedList()
                    .FirstOrDefault(l => l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase));
                if (layer == null) return false;

                map!.RemoveLayer(layer);
                return true;
            });

            if (!removed)
                throw new InvalidOperationException($"No layer named '{layerName}' found to remove.");

            return $"Removed layer '{layerName}'.";
        }

        /// <summary>
        /// Saves the current project. Same family as Project.CreateAsync -- testing
        /// whether project-lifecycle calls consistently need the dispatcher hop.
        /// </summary>
        public static async Task<string> SaveProjectAsync()
        {
            if (Project.Current == null)
                throw new InvalidOperationException("No project is open.");

            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null)
            {
                throw new InvalidOperationException("No WPF dispatcher available (unexpected outside ArcGIS Pro).");
            }

            await dispatcher.InvokeAsync(() => Project.Current.SaveAsync()).Task.Unwrap();

            return "Project saved.";
        }

        // ---- View/pane interactivity: the genuinely new capability over the Python
        // bridge, which has no concept of a live window at all. Every one of these
        // touches a live MapView/Pane rather than pure CIM data, so per this session's
        // pattern they all default to the dispatcher hop -- confirm live, don't assume.

        /// <summary>Must be called from the UI thread. Named map's view, else the active view.</summary>
        private static MapView? ResolveMapView(string mapName)
        {
            if (!string.IsNullOrEmpty(mapName))
            {
                return ProApp.Panes.OfType<IMapPane>()
                    .FirstOrDefault(p => p.MapView?.Map?.Name.Equals(mapName, StringComparison.OrdinalIgnoreCase) == true)
                    ?.MapView;
            }
            return MapView.Active;
        }

        public static async Task<string> ListOpenViewsAsync()
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null)
                throw new InvalidOperationException("No WPF dispatcher available (unexpected outside ArcGIS Pro).");

            var names = await dispatcher.InvokeAsync(() =>
                ProApp.Panes.OfType<IMapPane>()
                    .Select(p => p.MapView?.Map?.Name)
                    .Where(n => !string.IsNullOrEmpty(n))
                    .ToList());

            return names.Count > 0 ? string.Join(", ", names) : "No map/scene views are currently open.";
        }

        public static async Task<string> ActivateViewAsync(string mapName)
        {
            if (string.IsNullOrWhiteSpace(mapName))
                throw new ArgumentException("mapName is required.");

            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null)
                throw new InvalidOperationException("No WPF dispatcher available (unexpected outside ArcGIS Pro).");

            var activated = await dispatcher.InvokeAsync(() =>
            {
                var pane = ProApp.Panes.OfType<IMapPane>()
                    .FirstOrDefault(p => p.MapView?.Map?.Name.Equals(mapName, StringComparison.OrdinalIgnoreCase) == true);
                if (pane == null) return false;
                ((Pane)pane).Activate();
                return true;
            });

            if (!activated)
                throw new InvalidOperationException($"No open view found for map '{mapName}'.");

            return $"Activated view for map '{mapName}'.";
        }

        public static async Task<string> CloseViewAsync(string mapName)
        {
            if (string.IsNullOrWhiteSpace(mapName))
                throw new ArgumentException("mapName is required.");

            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null)
                throw new InvalidOperationException("No WPF dispatcher available (unexpected outside ArcGIS Pro).");

            var closed = await dispatcher.InvokeAsync(() =>
            {
                var pane = ProApp.Panes.OfType<IMapPane>()
                    .FirstOrDefault(p => p.MapView?.Map?.Name.Equals(mapName, StringComparison.OrdinalIgnoreCase) == true);
                if (pane == null) return false;
                ((Pane)pane).Close();
                return true;
            });

            if (!closed)
                throw new InvalidOperationException($"No open view found for map '{mapName}'.");

            return $"Closed view for map '{mapName}'.";
        }

        public static async Task<string> GetViewExtentAsync(string mapName)
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null)
                throw new InvalidOperationException("No WPF dispatcher available (unexpected outside ArcGIS Pro).");

            var extent = await dispatcher.InvokeAsync(() => ResolveMapView(mapName)?.Extent);

            if (extent == null)
                throw new InvalidOperationException("No open view found.");

            return $"XMin={extent.XMin:F2}, YMin={extent.YMin:F2}, XMax={extent.XMax:F2}, YMax={extent.YMax:F2}, SpatialReference={extent.SpatialReference?.Name}";
        }

        public static async Task<string> ZoomToExtentAsync(double xmin, double ymin, double xmax, double ymax, string mapName)
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null)
                throw new InvalidOperationException("No WPF dispatcher available (unexpected outside ArcGIS Pro).");

            bool zoomed = await dispatcher.InvokeAsync(async () =>
            {
                var view = ResolveMapView(mapName);
                if (view == null) return false;
                var envelope = EnvelopeBuilderEx.CreateEnvelope(xmin, ymin, xmax, ymax, view.Map.SpatialReference);
                return await view.ZoomToAsync(envelope);
            }).Task.Unwrap();

            if (!zoomed)
                throw new InvalidOperationException("Could not zoom (no open view found, or the zoom itself failed).");

            return $"Zoomed to extent ({xmin}, {ymin}, {xmax}, {ymax}).";
        }

        public static async Task<string> ZoomInAsync(string mapName)
        {
            var view = await ResolveMapViewOnUiThread(mapName);
            if (view == null)
                throw new InvalidOperationException("No open view found to zoom.");

            // Confirmed live: unlike ZoomToAsync (Task-returning, marshals itself fine
            // from the dispatcher), ZoomInFixed/ZoomOutFixed are synchronous CIM-affinity
            // calls -- calling them from the WPF dispatcher thread throws
            // ArcGIS.Core.CalledOnWrongThreadException. They need QueuedTask specifically,
            // a third distinct threading requirement in this add-in (dispatcher for
            // resolving which view via the UI-owned Panes collection, then QueuedTask for
            // the actual CIM-affinity call on that view).
            await QueuedTask.Run(() => view.ZoomInFixed());

            return "Zoomed in.";
        }

        public static async Task<string> ZoomOutAsync(string mapName)
        {
            var view = await ResolveMapViewOnUiThread(mapName);
            if (view == null)
                throw new InvalidOperationException("No open view found to zoom.");

            await QueuedTask.Run(() => view.ZoomOutFixed());

            return "Zoomed out.";
        }

        /// <summary>Resolves a view via the dispatcher (Panes is UI-owned), for callers that then need QueuedTask for the CIM-affinity call itself.</summary>
        private static async Task<MapView?> ResolveMapViewOnUiThread(string mapName)
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null)
                throw new InvalidOperationException("No WPF dispatcher available (unexpected outside ArcGIS Pro).");

            return await dispatcher.InvokeAsync(() => ResolveMapView(mapName));
        }

        public static async Task<string> SelectByExtentAsync(double xmin, double ymin, double xmax, double ymax, string mapName)
        {
            var view = await ResolveMapViewOnUiThread(mapName);
            if (view == null)
                throw new InvalidOperationException("No open view found to select in.");

            // Same CIM-affinity requirement as ZoomInFixed/ZoomOutFixed -- SelectFeatures
            // is synchronous too, needs QueuedTask rather than the dispatcher.
            var summary = await QueuedTask.Run(() =>
            {
                var envelope = EnvelopeBuilderEx.CreateEnvelope(xmin, ymin, xmax, ymax, view.Map.SpatialReference);
                var result = view.SelectFeatures(envelope, SelectionCombinationMethod.New).ToDictionary();

                return result.Count == 0
                    ? "No features selected."
                    : string.Join(", ", result.Select(kvp => $"{kvp.Key.Name}: {kvp.Value.Count}"));
            });

            return summary;
        }

        /// <summary>
        /// Renders the active (or named) view to a PNG file. Genuinely useful beyond
        /// the "arcpy can't do this" theme: it's the only way this add-in (or anyone
        /// driving it, e.g. Claude) can actually *see* what's on screen -- there's no
        /// way to screenshot the live ArcGIS Pro window from outside the process.
        /// </summary>
        public static async Task<string> ExportViewAsync(string outputPath, string mapName)
        {
            if (string.IsNullOrWhiteSpace(outputPath))
            {
                var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                outputPath = Path.Combine(Path.GetTempPath(), $"arcgispro_view_{timestamp}.png");
            }
            outputPath = outputPath.Replace('/', '\\');

            var view = await ResolveMapViewOnUiThread(mapName);
            if (view == null)
                throw new InvalidOperationException("No open view found to export.");

            // Same CIM-affinity requirement as ZoomInFixed/SelectFeatures -- Export is
            // synchronous too, needs QueuedTask rather than the dispatcher.
            await QueuedTask.Run(() => view.Export(new PNGFormat { OutputFileName = outputPath }));

            return $"Exported view to {outputPath}.";
        }

        /// <summary>Lists bookmarks in the current project's active (or named) map -- pure CIM read.</summary>
        public static async Task<string> ListBookmarksAsync(string mapName)
        {
            if (Project.Current == null)
                throw new InvalidOperationException("No project is open.");

            var names = await QueuedTask.Run(() =>
            {
                var map = ResolveMap(mapName);
                return map?.GetBookmarks().Select(b => b.Name).ToList();
            });

            if (names == null)
                throw new InvalidOperationException("No map found to list bookmarks from.");

            return names.Count > 0 ? string.Join(", ", names) : "No bookmarks in this map.";
        }

        /// <summary>Creates a bookmark from the active (or named) view's current camera position.</summary>
        public static async Task<string> AddBookmarkAsync(string bookmarkName, string mapName)
        {
            if (string.IsNullOrWhiteSpace(bookmarkName))
                throw new ArgumentException("bookmarkName is required.");

            var view = await ResolveMapViewOnUiThread(mapName);
            if (view?.Map == null)
                throw new InvalidOperationException("No open view found to bookmark.");

            // AddBookmark lives on Map, not MapView -- it takes the view as a parameter
            // to snapshot its current camera.
            await QueuedTask.Run(() => view.Map.AddBookmark(view, bookmarkName));

            return $"Added bookmark '{bookmarkName}'.";
        }

        /// <summary>Zooms the active (or named) view to a named bookmark.</summary>
        public static async Task<string> ZoomToBookmarkAsync(string bookmarkName, string mapName)
        {
            if (string.IsNullOrWhiteSpace(bookmarkName))
                throw new ArgumentException("bookmarkName is required.");

            var view = await ResolveMapViewOnUiThread(mapName);
            if (view?.Map == null)
                throw new InvalidOperationException("No open view found.");

            // GetBookmarks() is CIM-affine like Map.AddBookmark -- needs QueuedTask, not
            // the dispatcher, even though ZoomToAsync right after it needs the dispatcher.
            var bookmark = await QueuedTask.Run(() =>
                view.Map.GetBookmarks()
                    .FirstOrDefault(b => b.Name.Equals(bookmarkName, StringComparison.OrdinalIgnoreCase)));

            if (bookmark == null)
                throw new InvalidOperationException($"No bookmark named '{bookmarkName}'.");

            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null)
                throw new InvalidOperationException("No WPF dispatcher available (unexpected outside ArcGIS Pro).");

            await dispatcher.InvokeAsync(() => view.ZoomToAsync(bookmark)).Task.Unwrap();

            return $"Zoomed to bookmark '{bookmarkName}'.";
        }

        /// <summary>Gets the active (or named) view's current camera (position, scale, heading, pitch).</summary>
        public static async Task<string> GetCameraAsync(string mapName)
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null)
                throw new InvalidOperationException("No WPF dispatcher available (unexpected outside ArcGIS Pro).");

            var camera = await dispatcher.InvokeAsync(() => ResolveMapView(mapName)?.Camera);

            if (camera == null)
                throw new InvalidOperationException("No open view found.");

            return $"X={camera.X:F2}, Y={camera.Y:F2}, Z={camera.Z:F2}, Scale={camera.Scale:F0}, " +
                   $"Heading={camera.Heading:F1}, Pitch={camera.Pitch:F1}";
        }
    }
}

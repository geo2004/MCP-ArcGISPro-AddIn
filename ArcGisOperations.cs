using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using ArcGIS.Core.CIM;
using ArcGIS.Core.Geometry;
using ArcGIS.Desktop.Core;
using ArcGIS.Desktop.Editing;
using ArcGIS.Desktop.Editing.Attributes;
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

        /// <summary>
        /// Sets the active (or named) view's camera directly -- the write half of
        /// GetCameraAsync, mainly useful for 3D scenes (heading/pitch are meaningless
        /// for a flat 2D map, but harmless to set anyway).
        /// </summary>
        public static async Task<string> SetCameraAsync(
            double x, double y, double? z, double? scale, double? heading, double? pitch, string mapName)
        {
            var view = await ResolveMapViewOnUiThread(mapName);
            if (view == null)
                throw new InvalidOperationException("No open view found.");

            // Mutate the existing camera's properties rather than construct a new one --
            // Camera's constructor overloads are ambiguous (2D vs 3D shapes with unclear
            // parameter order), but this is the standard Esri ProSnippets pattern for
            // "fly to a location" and sidesteps that entirely.
            var camera = view.Camera;
            camera.X = x;
            camera.Y = y;
            if (z.HasValue) camera.Z = z.Value;
            if (scale.HasValue) camera.Scale = scale.Value;
            if (heading.HasValue) camera.Heading = heading.Value;
            if (pitch.HasValue) camera.Pitch = pitch.Value;

            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null)
                throw new InvalidOperationException("No WPF dispatcher available (unexpected outside ArcGIS Pro).");

            // ZoomToAsync -- like the envelope/bookmark overloads already used elsewhere
            // -- is Async-suffixed and thread-flexible, so the dispatcher is fine here too.
            await dispatcher.InvokeAsync(() => view.ZoomToAsync(camera)).Task.Unwrap();

            return $"Camera set to X={camera.X:F2}, Y={camera.Y:F2}, Z={camera.Z:F2}, " +
                   $"Scale={camera.Scale:F0}, Heading={camera.Heading:F1}, Pitch={camera.Pitch:F1}.";
        }

        /// <summary>Inserts a new 3D scene (local or global) into the current project and opens its view.</summary>
        public static async Task<string> InsertSceneAsync(string sceneName, bool isGlobal)
        {
            if (Project.Current == null)
                throw new InvalidOperationException("No project is open. Call create_project first.");

            if (string.IsNullOrWhiteSpace(sceneName))
                sceneName = isGlobal ? "Global Scene" : "Scene";

            var viewingMode = isGlobal ? MapViewingMode.SceneGlobal : MapViewingMode.SceneLocal;

            // CreateScene's 2nd parameter is a ground-elevation-source Uri, not the
            // viewing mode (that's 3rd) -- null uses the default ground source.
            Map scene = await QueuedTask.Run(() => MapFactory.Instance.CreateScene(sceneName, null, viewingMode));

            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null)
                throw new InvalidOperationException("No WPF dispatcher available (unexpected outside ArcGIS Pro).");

            await dispatcher.InvokeAsync(() => ProApp.Panes.CreateMapPaneAsync(scene)).Task.Unwrap();

            return $"Inserted and opened new {(isGlobal ? "global " : "")}scene '{scene.Name}'.";
        }

        /// <summary>Undoes the last operation on the active (or named) map -- arcpy has no concept of this at all.</summary>
        public static async Task<string> UndoAsync(string mapName)
        {
            var view = await ResolveMapViewOnUiThread(mapName);
            if (view?.Map == null)
                throw new InvalidOperationException("No open view found.");

            if (!view.Map.OperationManager.CanUndo)
                return "Nothing to undo.";

            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null)
                throw new InvalidOperationException("No WPF dispatcher available (unexpected outside ArcGIS Pro).");

            await dispatcher.InvokeAsync(() => view.Map.OperationManager.UndoAsync()).Task.Unwrap();

            return "Undone.";
        }

        /// <summary>Redoes the last undone operation on the active (or named) map.</summary>
        public static async Task<string> RedoAsync(string mapName)
        {
            var view = await ResolveMapViewOnUiThread(mapName);
            if (view?.Map == null)
                throw new InvalidOperationException("No open view found.");

            if (!view.Map.OperationManager.CanRedo)
                return "Nothing to redo.";

            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null)
                throw new InvalidOperationException("No WPF dispatcher available (unexpected outside ArcGIS Pro).");

            await dispatcher.InvokeAsync(() => view.Map.OperationManager.RedoAsync()).Task.Unwrap();

            return "Redone.";
        }

        /// <summary>
        /// Shows a message box in ArcGIS Pro itself -- possible only because a live
        /// window exists. Synchronous but creates a real WPF dialog, so per Esri's own
        /// "handful of methods need the GUI thread" carve-out, it needs the dispatcher
        /// despite not being Async-suffixed.
        /// </summary>
        public static async Task<string> ShowMessageAsync(string message, string caption)
        {
            if (string.IsNullOrWhiteSpace(message))
                throw new ArgumentException("message is required.");

            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null)
                throw new InvalidOperationException("No WPF dispatcher available (unexpected outside ArcGIS Pro).");

            await dispatcher.InvokeAsync(() =>
                ArcGIS.Desktop.Framework.Dialogs.MessageBox.Show(message, string.IsNullOrEmpty(caption) ? "MCP" : caption));

            return "Message shown.";
        }

        /// <summary>Activates a Pro tool/command by its DAML id, e.g. "esri_mapping_exploreTool".</summary>
        public static async Task<string> ActivateToolAsync(string toolId)
        {
            if (string.IsNullOrWhiteSpace(toolId))
                throw new ArgumentException("toolId is required.");

            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null)
                throw new InvalidOperationException("No WPF dispatcher available (unexpected outside ArcGIS Pro).");

            // SetCurrentToolAsync returns a plain Task (not Task<bool>) -- if the id is
            // wrong it throws or no-ops rather than reporting failure via a return value.
            await dispatcher.InvokeAsync(() => FrameworkApplication.SetCurrentToolAsync(toolId)).Task.Unwrap();

            return $"Activated tool '{toolId}'.";
        }

        /// <summary>Gets the currently active Pro tool/command's DAML id.</summary>
        public static Task<string> GetCurrentToolAsync()
        {
            var toolId = FrameworkApplication.CurrentTool;
            return Task.FromResult(string.IsNullOrEmpty(toolId) ? "No tool is active." : toolId);
        }

        // ---- v2: data editing via EditOperation. Genuinely distinct from arcpy's
        // cursors -- these participate in Pro's own undo/redo stack and edit-session
        // model (confirmed live already: add_layer showed up as a real undo/redo entry),
        // not just raw table writes. Point-geometry only for now -- WKT/multi-vertex
        // geometry parsing from a JSON-RPC string argument is a bigger, separate piece,
        // deliberately deferred.

        /// <summary>JSON object string, e.g. {"NAME":"foo","COUNT":3} -> Dictionary for EditOperation attribute payloads.</summary>
        private static Dictionary<string, object> ParseAttributesJson(string attributesJson)
        {
            var dict = new Dictionary<string, object>();
            if (string.IsNullOrWhiteSpace(attributesJson)) return dict;

            using var doc = JsonDocument.Parse(attributesJson);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                dict[prop.Name] = prop.Value.ValueKind switch
                {
                    JsonValueKind.String => prop.Value.GetString()!,
                    JsonValueKind.Number => prop.Value.TryGetInt64(out var l) ? (object)l : prop.Value.GetDouble(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    _ => prop.Value.ToString()
                };
            }
            return dict;
        }

        /// <summary>Must be called from inside QueuedTask.Run.</summary>
        private static FeatureLayer? ResolveFeatureLayer(string mapName, string layerName)
        {
            var map = ResolveMap(mapName);
            return map?.GetLayersAsFlattenedList().OfType<FeatureLayer>()
                .FirstOrDefault(l => l.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>JSON array of [x,y] pairs, e.g. [[100,0],[101,0],[101,1]] -> MapPoint list for polyline/polygon builders.</summary>
        private static List<MapPoint> ParseVerticesJson(string verticesJson, SpatialReference sr)
        {
            if (string.IsNullOrWhiteSpace(verticesJson))
                throw new ArgumentException("verticesJson is required, e.g. [[100,0],[101,0],[101,1]].");

            using var doc = JsonDocument.Parse(verticesJson);
            var points = new List<MapPoint>();
            foreach (var vertex in doc.RootElement.EnumerateArray())
            {
                var coords = vertex.EnumerateArray().ToArray();
                points.Add(MapPointBuilderEx.CreateMapPoint(coords[0].GetDouble(), coords[1].GetDouble(), sr));
            }

            if (points.Count < 2)
                throw new ArgumentException("verticesJson must contain at least 2 points.");

            return points;
        }

        /// <summary>Creates a new point feature. Polygon/polyline creation isn't supported yet.</summary>
        public static async Task<string> CreateFeatureAsync(string layerName, double x, double y, string attributesJson, string mapName)
        {
            if (Project.Current == null)
                throw new InvalidOperationException("No project is open.");

            var attributes = ParseAttributesJson(attributesJson);

            await QueuedTask.Run(() =>
            {
                var layer = ResolveFeatureLayer(mapName, layerName)
                    ?? throw new InvalidOperationException($"No layer named '{layerName}' found.");

                var point = MapPointBuilderEx.CreateMapPoint(x, y, layer.GetSpatialReference());
                attributes["SHAPE"] = point;

                var op = new EditOperation { Name = "Create feature via MCP" };
                op.Create(layer, attributes);
                if (!op.Execute())
                    throw new InvalidOperationException($"Edit failed: {op.ErrorMessage}");
            });

            return $"Created point feature in '{layerName}' at ({x}, {y}).";
        }

        /// <summary>Moves a point feature to a new location.</summary>
        public static async Task<string> UpdateFeatureGeometryAsync(long objectId, double x, double y, string layerName, string mapName)
        {
            if (Project.Current == null)
                throw new InvalidOperationException("No project is open.");

            await QueuedTask.Run(() =>
            {
                var layer = ResolveFeatureLayer(mapName, layerName)
                    ?? throw new InvalidOperationException($"No layer named '{layerName}' found.");

                var point = MapPointBuilderEx.CreateMapPoint(x, y, layer.GetSpatialReference());

                var op = new EditOperation { Name = "Update feature geometry via MCP" };
                op.Modify(layer, objectId, point);
                if (!op.Execute())
                    throw new InvalidOperationException($"Edit failed: {op.ErrorMessage}");
            });

            return $"Moved feature {objectId} in '{layerName}' to ({x}, {y}).";
        }

        /// <summary>Updates one or more attribute values on a feature, by object id.</summary>
        public static async Task<string> UpdateFeatureAttributesAsync(long objectId, string attributesJson, string layerName, string mapName)
        {
            if (Project.Current == null)
                throw new InvalidOperationException("No project is open.");

            var attributes = ParseAttributesJson(attributesJson);
            if (attributes.Count == 0)
                throw new ArgumentException("attributesJson must contain at least one field.");

            await QueuedTask.Run(() =>
            {
                var layer = ResolveFeatureLayer(mapName, layerName)
                    ?? throw new InvalidOperationException($"No layer named '{layerName}' found.");

                var op = new EditOperation { Name = "Update feature attributes via MCP" };
                op.Modify(layer, objectId, attributes);
                if (!op.Execute())
                    throw new InvalidOperationException($"Edit failed: {op.ErrorMessage}");
            });

            return $"Updated feature {objectId} in '{layerName}'.";
        }

        /// <summary>Deletes a feature by object id.</summary>
        public static async Task<string> DeleteFeatureAsync(long objectId, string layerName, string mapName)
        {
            if (Project.Current == null)
                throw new InvalidOperationException("No project is open.");

            await QueuedTask.Run(() =>
            {
                var layer = ResolveFeatureLayer(mapName, layerName)
                    ?? throw new InvalidOperationException($"No layer named '{layerName}' found.");

                var op = new EditOperation { Name = "Delete feature via MCP" };
                op.Delete(layer, objectId);
                if (!op.Execute())
                    throw new InvalidOperationException($"Edit failed: {op.ErrorMessage}");
            });

            return $"Deleted feature {objectId} from '{layerName}'.";
        }

        /// <summary>Saves all unsaved data edits -- distinct from save_project, which saves the .aprx itself.</summary>
        public static async Task<string> SaveEditsAsync()
        {
            if (Project.Current == null)
                throw new InvalidOperationException("No project is open.");

            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null)
                throw new InvalidOperationException("No WPF dispatcher available (unexpected outside ArcGIS Pro).");

            bool saved = await dispatcher.InvokeAsync(() => Project.Current.SaveEditsAsync()).Task.Unwrap();

            return saved ? "Edits saved." : "Nothing to save, or save failed.";
        }

        /// <summary>Discards all unsaved data edits.</summary>
        public static async Task<string> DiscardEditsAsync()
        {
            if (Project.Current == null)
                throw new InvalidOperationException("No project is open.");

            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null)
                throw new InvalidOperationException("No WPF dispatcher available (unexpected outside ArcGIS Pro).");

            bool discarded = await dispatcher.InvokeAsync(() => Project.Current.DiscardEditsAsync()).Task.Unwrap();

            return discarded ? "Edits discarded." : "Nothing to discard, or discard failed.";
        }

        /// <summary>Enables/disables snapping and sets which snap modes are active. Empty snapModes with enabled=true keeps whatever modes were already set.</summary>
        public static async Task<string> SetSnappingAsync(bool enabled, List<string> snapModes)
        {
            await QueuedTask.Run(() =>
            {
                Snapping.IsEnabled = enabled;

                if (snapModes.Count > 0)
                {
                    var modes = snapModes
                        .Select(m => Enum.TryParse<SnapMode>(m, ignoreCase: true, out var mode) ? mode : (SnapMode?)null)
                        .Where(m => m.HasValue)
                        .Select(m => m!.Value)
                        .ToList();
                    Snapping.SetSnapModes(modes);
                }
            });

            return $"Snapping {(enabled ? "enabled" : "disabled")}" +
                   (snapModes.Count > 0 ? $", modes: {string.Join(", ", snapModes)}." : ".");
        }

        /// <summary>Reads back whatever is currently selected in a view -- including selections a human made by clicking, not just ones this add-in made.</summary>
        public static async Task<string> GetSelectedFeaturesAsync(string mapName)
        {
            if (Project.Current == null)
                throw new InvalidOperationException("No project is open.");

            var summary = await QueuedTask.Run(() =>
            {
                var map = ResolveMap(mapName);
                if (map == null) return null;

                var selection = map.GetSelection();
                return selection.Count == 0
                    ? "Nothing is selected."
                    : string.Join("; ", selection.ToDictionary()
                        .Select(kvp => $"{kvp.Key.Name}: [{string.Join(",", kvp.Value)}]"));
            });

            if (summary == null)
                throw new InvalidOperationException("No map found.");

            return summary;
        }

        /// <summary>Opens a live attribute table pane for a layer -- another live-window-only capability, arcpy has no window to open at all.</summary>
        public static async Task<string> OpenTableAsync(string layerName, string mapName)
        {
            if (Project.Current == null)
                throw new InvalidOperationException("No project is open.");

            var layer = await QueuedTask.Run(() => (MapMember?)ResolveFeatureLayer(mapName, layerName));
            if (layer == null)
                throw new InvalidOperationException($"No layer named '{layerName}' found.");

            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null)
                throw new InvalidOperationException("No WPF dispatcher available (unexpected outside ArcGIS Pro).");

            await dispatcher.InvokeAsync(() => FrameworkApplication.Panes.OpenTablePane(layer, TableViewMode.eAllRecords));

            return $"Opened attribute table for '{layerName}'.";
        }

        /// <summary>Closes the open attribute table pane for a layer.</summary>
        public static async Task<string> CloseTableAsync(string layerName)
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null)
                throw new InvalidOperationException("No WPF dispatcher available (unexpected outside ArcGIS Pro).");

            var closed = await dispatcher.InvokeAsync(() =>
            {
                var pane = FrameworkApplication.Panes.OfType<ITablePane>()
                    .FirstOrDefault(p => p.MapMember?.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase) == true);
                if (pane == null) return false;
                ((Pane)pane).Close();
                return true;
            });

            if (!closed)
                throw new InvalidOperationException($"No open attribute table found for '{layerName}'.");

            return $"Closed attribute table for '{layerName}'.";
        }

        /// <summary>Lists every currently open attribute table pane.</summary>
        public static async Task<string> ListOpenTablesAsync()
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null)
                throw new InvalidOperationException("No WPF dispatcher available (unexpected outside ArcGIS Pro).");

            var names = await dispatcher.InvokeAsync(() =>
                FrameworkApplication.Panes.OfType<ITablePane>()
                    .Select(p => p.MapMember?.Name)
                    .Where(n => !string.IsNullOrEmpty(n))
                    .ToList());

            return names.Count > 0 ? string.Join(", ", names) : "No attribute tables are currently open.";
        }

        // ---- v3 batch 1: full geometry support (create_feature/update_feature_geometry
        // were point-only) and a point-based identify that doesn't disturb selection.

        public static async Task<string> CreatePolylineFeatureAsync(string layerName, string verticesJson, string attributesJson, string mapName)
        {
            if (Project.Current == null)
                throw new InvalidOperationException("No project is open.");

            var attributes = ParseAttributesJson(attributesJson);

            await QueuedTask.Run(() =>
            {
                var layer = ResolveFeatureLayer(mapName, layerName)
                    ?? throw new InvalidOperationException($"No layer named '{layerName}' found.");

                var sr = layer.GetSpatialReference();
                var polyline = PolylineBuilderEx.CreatePolyline(ParseVerticesJson(verticesJson, sr), sr);
                attributes["SHAPE"] = polyline;

                var op = new EditOperation { Name = "Create polyline feature via MCP" };
                op.Create(layer, attributes);
                if (!op.Execute())
                    throw new InvalidOperationException($"Edit failed: {op.ErrorMessage}");
            });

            return $"Created polyline feature in '{layerName}'.";
        }

        public static async Task<string> CreatePolygonFeatureAsync(string layerName, string verticesJson, string attributesJson, string mapName)
        {
            if (Project.Current == null)
                throw new InvalidOperationException("No project is open.");

            var attributes = ParseAttributesJson(attributesJson);

            await QueuedTask.Run(() =>
            {
                var layer = ResolveFeatureLayer(mapName, layerName)
                    ?? throw new InvalidOperationException($"No layer named '{layerName}' found.");

                var sr = layer.GetSpatialReference();
                var polygon = PolygonBuilderEx.CreatePolygon(ParseVerticesJson(verticesJson, sr), sr);
                attributes["SHAPE"] = polygon;

                var op = new EditOperation { Name = "Create polygon feature via MCP" };
                op.Create(layer, attributes);
                if (!op.Execute())
                    throw new InvalidOperationException($"Edit failed: {op.ErrorMessage}");
            });

            return $"Created polygon feature in '{layerName}'.";
        }

        public static async Task<string> UpdatePolylineGeometryAsync(long objectId, string verticesJson, string layerName, string mapName)
        {
            if (Project.Current == null)
                throw new InvalidOperationException("No project is open.");

            await QueuedTask.Run(() =>
            {
                var layer = ResolveFeatureLayer(mapName, layerName)
                    ?? throw new InvalidOperationException($"No layer named '{layerName}' found.");

                var sr = layer.GetSpatialReference();
                var polyline = PolylineBuilderEx.CreatePolyline(ParseVerticesJson(verticesJson, sr), sr);

                var op = new EditOperation { Name = "Update polyline geometry via MCP" };
                op.Modify(layer, objectId, polyline);
                if (!op.Execute())
                    throw new InvalidOperationException($"Edit failed: {op.ErrorMessage}");
            });

            return $"Updated polyline geometry for feature {objectId} in '{layerName}'.";
        }

        public static async Task<string> UpdatePolygonGeometryAsync(long objectId, string verticesJson, string layerName, string mapName)
        {
            if (Project.Current == null)
                throw new InvalidOperationException("No project is open.");

            await QueuedTask.Run(() =>
            {
                var layer = ResolveFeatureLayer(mapName, layerName)
                    ?? throw new InvalidOperationException($"No layer named '{layerName}' found.");

                var sr = layer.GetSpatialReference();
                var polygon = PolygonBuilderEx.CreatePolygon(ParseVerticesJson(verticesJson, sr), sr);

                var op = new EditOperation { Name = "Update polygon geometry via MCP" };
                op.Modify(layer, objectId, polygon);
                if (!op.Execute())
                    throw new InvalidOperationException($"Edit failed: {op.ErrorMessage}");
            });

            return $"Updated polygon geometry for feature {objectId} in '{layerName}'.";
        }

        /// <summary>Read-only probe at a point -- unlike select_by_extent, doesn't change the current selection.</summary>
        public static async Task<string> IdentifyAtPointAsync(double x, double y, double tolerance, string mapName)
        {
            var view = await ResolveMapViewOnUiThread(mapName);
            if (view == null)
                throw new InvalidOperationException("No open view found.");

            var summary = await QueuedTask.Run(() =>
            {
                var sr = view.Map.SpatialReference;
                var envelope = EnvelopeBuilderEx.CreateEnvelope(x - tolerance, y - tolerance, x + tolerance, y + tolerance, sr);
                var result = view.GetFeatures(envelope, true, false).ToDictionary();

                return result.Count == 0
                    ? "Nothing found at that location."
                    : string.Join("; ", result.Select(kvp => $"{kvp.Key.Name}: [{string.Join(",", kvp.Value)}]"));
            });

            return summary;
        }

        // ---- v3 batches 2-3: the rest of EditOperation's manual-edit toolbox. Several
        // of these signatures aren't fully confirmed in Esri's own docs -- written as
        // the best-grounded guess from the confirmed patterns (Layer + oid(s) + geometry,
        // matching Clip/Split/Explode), correction left to the compiler, which has been
        // reliable and cheap all session compared to guessing wrong at runtime.

        /// <summary>Moves whatever is currently selected by an offset.</summary>
        public static async Task<string> MoveFeaturesAsync(double dx, double dy, string mapName)
        {
            var view = await ResolveMapViewOnUiThread(mapName);
            if (view?.Map == null)
                throw new InvalidOperationException("No open view found.");

            await QueuedTask.Run(() =>
            {
                var selection = view.Map.GetSelection();
                if (selection.Count == 0)
                    throw new InvalidOperationException("Nothing is selected. Select features first.");

                var op = new EditOperation { Name = "Move features via MCP" };
                op.Move(selection, dx, dy);
                if (!op.Execute())
                    throw new InvalidOperationException($"Edit failed: {op.ErrorMessage}");
            });

            return $"Moved selected features by ({dx}, {dy}).";
        }

        /// <summary>Rotates whatever is currently selected around a pivot point, in degrees.</summary>
        public static async Task<string> RotateFeaturesAsync(double originX, double originY, double angleDegrees, string mapName)
        {
            var view = await ResolveMapViewOnUiThread(mapName);
            if (view?.Map == null)
                throw new InvalidOperationException("No open view found.");

            await QueuedTask.Run(() =>
            {
                var selection = view.Map.GetSelection();
                if (selection.Count == 0)
                    throw new InvalidOperationException("Nothing is selected. Select features first.");

                var origin = MapPointBuilderEx.CreateMapPoint(originX, originY, view.Map.SpatialReference);
                var op = new EditOperation { Name = "Rotate features via MCP" };
                op.Rotate(selection, origin, angleDegrees * Math.PI / 180);
                if (!op.Execute())
                    throw new InvalidOperationException($"Edit failed: {op.ErrorMessage}");
            });

            return $"Rotated selected features by {angleDegrees} degrees around ({originX}, {originY}).";
        }

        /// <summary>Scales whatever is currently selected around a pivot point.</summary>
        public static async Task<string> ScaleFeaturesAsync(double originX, double originY, double scaleX, double scaleY, string mapName)
        {
            var view = await ResolveMapViewOnUiThread(mapName);
            if (view?.Map == null)
                throw new InvalidOperationException("No open view found.");

            await QueuedTask.Run(() =>
            {
                var selection = view.Map.GetSelection();
                if (selection.Count == 0)
                    throw new InvalidOperationException("Nothing is selected. Select features first.");

                var origin = MapPointBuilderEx.CreateMapPoint(originX, originY, view.Map.SpatialReference);
                var op = new EditOperation { Name = "Scale features via MCP" };
                op.Scale(selection, origin, scaleX, scaleY);
                if (!op.Execute())
                    throw new InvalidOperationException($"Edit failed: {op.ErrorMessage}");
            });

            return $"Scaled selected features by ({scaleX}, {scaleY}) around ({originX}, {originY}).";
        }

        /// <summary>Merges every currently-selected feature in a layer into one.</summary>
        public static async Task<string> MergeFeaturesAsync(string layerName, string mapName)
        {
            if (Project.Current == null)
                throw new InvalidOperationException("No project is open.");

            await QueuedTask.Run(() =>
            {
                var layer = ResolveFeatureLayer(mapName, layerName)
                    ?? throw new InvalidOperationException($"No layer named '{layerName}' found.");

                var oids = layer.GetSelection().GetObjectIDs();
                if (oids.Count < 2)
                    throw new InvalidOperationException("Select at least 2 features in this layer to merge.");

                var op = new EditOperation { Name = "Merge features via MCP" };
                op.Merge(layer, oids);
                if (!op.Execute())
                    throw new InvalidOperationException($"Edit failed: {op.ErrorMessage}");
            });

            return $"Merged selected features in '{layerName}'.";
        }

        /// <summary>Splits a feature along a cutting line.</summary>
        public static async Task<string> SplitFeatureAsync(long objectId, string splitLineVerticesJson, string layerName, string mapName)
        {
            if (Project.Current == null)
                throw new InvalidOperationException("No project is open.");

            await QueuedTask.Run(() =>
            {
                var layer = ResolveFeatureLayer(mapName, layerName)
                    ?? throw new InvalidOperationException($"No layer named '{layerName}' found.");

                var sr = layer.GetSpatialReference();
                var splitLine = PolylineBuilderEx.CreatePolyline(ParseVerticesJson(splitLineVerticesJson, sr), sr);

                var op = new EditOperation { Name = "Split feature via MCP" };
                op.Split(layer, objectId, splitLine);
                if (!op.Execute())
                    throw new InvalidOperationException($"Edit failed: {op.ErrorMessage}");
            });

            return $"Split feature {objectId} in '{layerName}'.";
        }

        /// <summary>Breaks a multipart feature into one feature per part.</summary>
        public static async Task<string> ExplodeFeatureAsync(long objectId, string layerName, string mapName)
        {
            if (Project.Current == null)
                throw new InvalidOperationException("No project is open.");

            await QueuedTask.Run(() =>
            {
                var layer = ResolveFeatureLayer(mapName, layerName)
                    ?? throw new InvalidOperationException($"No layer named '{layerName}' found.");

                var op = new EditOperation { Name = "Explode feature via MCP" };
                op.Explode(layer, new List<long> { objectId }, true);
                if (!op.Execute())
                    throw new InvalidOperationException($"Edit failed: {op.ErrorMessage}");
            });

            return $"Exploded feature {objectId} in '{layerName}' into single-part features.";
        }

        /// <summary>Clips a feature against a boundary polygon.</summary>
        public static async Task<string> ClipFeatureAsync(long objectId, string clipPolygonVerticesJson, string layerName, string mapName)
        {
            if (Project.Current == null)
                throw new InvalidOperationException("No project is open.");

            await QueuedTask.Run(() =>
            {
                var layer = ResolveFeatureLayer(mapName, layerName)
                    ?? throw new InvalidOperationException($"No layer named '{layerName}' found.");

                var sr = layer.GetSpatialReference();
                var clipPolygon = PolygonBuilderEx.CreatePolygon(ParseVerticesJson(clipPolygonVerticesJson, sr), sr);

                var op = new EditOperation { Name = "Clip feature via MCP" };
                op.Clip(layer, objectId, clipPolygon, ClipMode.PreserveArea);
                if (!op.Execute())
                    throw new InvalidOperationException($"Edit failed: {op.ErrorMessage}");
            });

            return $"Clipped feature {objectId} in '{layerName}'.";
        }

        /// <summary>Reshapes part of a feature's geometry using a cutting line.</summary>
        public static async Task<string> ReshapeFeatureAsync(long objectId, string reshapeLineVerticesJson, string layerName, string mapName)
        {
            if (Project.Current == null)
                throw new InvalidOperationException("No project is open.");

            await QueuedTask.Run(() =>
            {
                var layer = ResolveFeatureLayer(mapName, layerName)
                    ?? throw new InvalidOperationException($"No layer named '{layerName}' found.");

                var sr = layer.GetSpatialReference();
                var reshapeLine = PolylineBuilderEx.CreatePolyline(ParseVerticesJson(reshapeLineVerticesJson, sr), sr);

                var op = new EditOperation { Name = "Reshape feature via MCP" };
                op.Reshape(layer, objectId, reshapeLine);
                if (!op.Execute())
                    throw new InvalidOperationException($"Edit failed: {op.ErrorMessage}");
            });

            return $"Reshaped feature {objectId} in '{layerName}'.";
        }

        /// <summary>Resolves geometry intersections among currently-selected features (e.g. shared vertices where lines cross) -- requires a Standard/Advanced license.</summary>
        public static async Task<string> PlanarizeFeaturesAsync(string layerName, string mapName)
        {
            if (Project.Current == null)
                throw new InvalidOperationException("No project is open.");

            await QueuedTask.Run(() =>
            {
                var layer = ResolveFeatureLayer(mapName, layerName)
                    ?? throw new InvalidOperationException($"No layer named '{layerName}' found.");

                var oids = layer.GetSelection().GetObjectIDs();
                if (oids.Count == 0)
                    throw new InvalidOperationException("Nothing is selected in this layer.");

                var op = new EditOperation { Name = "Planarize features via MCP" };
                op.Planarize(layer, oids);
                if (!op.Execute())
                    throw new InvalidOperationException($"Edit failed: {op.ErrorMessage}");
            });

            return $"Planarized selected features in '{layerName}'.";
        }

        /// <summary>Changes a feature's subtype classification.</summary>
        public static async Task<string> ChangeSubtypeAsync(long objectId, int newSubtypeCode, string layerName, string mapName)
        {
            if (Project.Current == null)
                throw new InvalidOperationException("No project is open.");

            await QueuedTask.Run(() =>
            {
                var layer = ResolveFeatureLayer(mapName, layerName)
                    ?? throw new InvalidOperationException($"No layer named '{layerName}' found.");

                // ChangeSubtype lives on Inspector, not EditOperation directly -- load
                // the feature, change its subtype there, then Modify(inspector).
                var inspector = new Inspector();
                inspector.Load(layer, objectId);
                inspector.ChangeSubtype(newSubtypeCode, true);

                var op = new EditOperation { Name = "Change subtype via MCP" };
                op.Modify(inspector);
                if (!op.Execute())
                    throw new InvalidOperationException($"Edit failed: {op.ErrorMessage}");
            });

            return $"Changed subtype for feature {objectId} in '{layerName}' to {newSubtypeCode}.";
        }

        /// <summary>Attaches a file (photo, PDF, etc.) to an existing feature.</summary>
        public static async Task<string> AddAttachmentAsync(long objectId, string filePath, string layerName, string mapName)
        {
            if (Project.Current == null)
                throw new InvalidOperationException("No project is open.");

            filePath = filePath.Replace('/', '\\');
            if (!File.Exists(filePath))
                throw new InvalidOperationException($"No file found at '{filePath}'.");

            await QueuedTask.Run(() =>
            {
                var layer = ResolveFeatureLayer(mapName, layerName)
                    ?? throw new InvalidOperationException($"No layer named '{layerName}' found.");

                var op = new EditOperation { Name = "Add attachment via MCP" };
                op.AddAttachment(layer, objectId, filePath);
                if (!op.Execute())
                    throw new InvalidOperationException($"Edit failed: {op.ErrorMessage}");
            });

            return $"Attached '{filePath}' to feature {objectId} in '{layerName}'.";
        }
    }
}

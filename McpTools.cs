using System;
using System.ComponentModel;
using System.Threading.Tasks;
using ModelContextProtocol.Server;

namespace MCPArcGISProAddIn
{
    /// <summary>
    /// MCP tool definitions, discovered via WithToolsFromAssembly() in McpHostService.
    /// Kept deliberately thin -- each method just calls into ArcGisOperations, which
    /// has the real logic and doesn't know anything about MCP.
    /// </summary>
    [McpServerToolType]
    public static class McpTools
    {
        /// <summary>
        /// The MCP SDK's own tool-invocation handler swallows exceptions into a generic
        /// "An error occurred invoking 'x'" -- useless for debugging. Every tool routes
        /// through here so the real exception lands in the same error log SafeCall uses,
        /// before letting the SDK's generic message still reach the client.
        /// </summary>
        private static async Task<string> RunLogged(Func<Task<string>> operation, string toolName)
        {
            try
            {
                return await operation();
            }
            catch (Exception ex)
            {
                McpHostService.LogError($"Tool '{toolName}' failed", ex);
                throw;
            }
        }

        [McpServerTool]
        [Description("Test the connection to ArcGIS Pro. Call this first to confirm the bridge is up.")]
        public static Task<string> Ping() => RunLogged(ArcGisOperations.PingAsync, nameof(Ping));

        [McpServerTool]
        [Description("Get the current state: is a project open, which one, what maps exist, which views " +
                      "are open, and which view is active. Call this before any state-dependent operation " +
                      "-- every ArcGIS Pro restart returns to the Home screen with nothing open.")]
        public static Task<string> GetStatus() => RunLogged(ArcGisOperations.GetStatusAsync, nameof(GetStatus));

        [McpServerTool]
        [Description("Open a map/scene/globe view in ArcGIS Pro -- something arcpy alone cannot do. " +
                      "Opens the active or first map in the project if mapName is left empty.")]
        public static Task<string> OpenView(
            [Description("Exact map name as shown in the Catalog pane, or empty for the active/first map.")]
            string mapName = "")
            => RunLogged(() => ArcGisOperations.OpenViewAsync(mapName), nameof(OpenView));

        [McpServerTool]
        [Description("Open an existing ArcGIS Pro project (.aprx) from disk.")]
        public static Task<string> OpenProject(
            [Description("Full path to the .aprx file.")]
            string path)
            => RunLogged(() => ArcGisOperations.OpenProjectAsync(path), nameof(OpenProject));

        [McpServerTool]
        [Description("Create a brand-new ArcGIS Pro project without a template, the same as " +
                      "'Start without a template' on the Home screen.")]
        public static Task<string> CreateProject(
            [Description("Name for the new project.")]
            string projectName,
            [Description("Folder to create the project in. Empty for the default Documents\\ArcGIS\\Projects folder.")]
            string folderPath = "")
            => RunLogged(() => ArcGisOperations.CreateProjectAsync(projectName, folderPath), nameof(CreateProject));

        [McpServerTool]
        [Description("Insert a new blank map into the currently open project and open its view.")]
        public static Task<string> InsertMap(
            [Description("Name for the new map. Defaults to 'Map' if left empty.")]
            string mapName = "")
            => RunLogged(() => ArcGisOperations.InsertMapAsync(mapName), nameof(InsertMap));

        [McpServerTool]
        [Description("List every map in the currently open project.")]
        public static Task<string> ListMaps() => RunLogged(ArcGisOperations.ListMapsAsync, nameof(ListMaps));

        [McpServerTool]
        [Description("List every layer in a map.")]
        public static Task<string> ListLayers(
            [Description("Exact map name, or empty for the active map.")]
            string mapName = "")
            => RunLogged(() => ArcGisOperations.ListLayersAsync(mapName), nameof(ListLayers));

        [McpServerTool]
        [Description("Add a layer to a map from a data path (shapefile, feature class, raster, etc.).")]
        public static Task<string> AddLayer(
            [Description("Full path to the data source, e.g. a .shp file or a geodatabase feature class.")]
            string dataPath,
            [Description("Exact map name to add the layer to, or empty for the active map.")]
            string mapName = "")
            => RunLogged(() => ArcGisOperations.AddLayerAsync(dataPath, mapName), nameof(AddLayer));

        [McpServerTool]
        [Description("Remove a named layer from a map.")]
        public static Task<string> RemoveLayer(
            [Description("Exact layer name as shown in the Contents pane.")]
            string layerName,
            [Description("Exact map name the layer belongs to, or empty for the active map.")]
            string mapName = "")
            => RunLogged(() => ArcGisOperations.RemoveLayerAsync(layerName, mapName), nameof(RemoveLayer));

        [McpServerTool]
        [Description("Save the currently open project.")]
        public static Task<string> SaveProject() => RunLogged(ArcGisOperations.SaveProjectAsync, nameof(SaveProject));

        // ---- View/pane interactivity -- genuinely new vs. the Python bridge, which has
        // no concept of a live window at all.

        [McpServerTool]
        [Description("List every currently open map/scene view.")]
        public static Task<string> ListOpenViews() => RunLogged(ArcGisOperations.ListOpenViewsAsync, nameof(ListOpenViews));

        [McpServerTool]
        [Description("Activate (bring to front) the open view for a map.")]
        public static Task<string> ActivateView(
            [Description("Exact map name of an already-open view.")]
            string mapName)
            => RunLogged(() => ArcGisOperations.ActivateViewAsync(mapName), nameof(ActivateView));

        [McpServerTool]
        [Description("Close the open view for a map.")]
        public static Task<string> CloseView(
            [Description("Exact map name of an already-open view.")]
            string mapName)
            => RunLogged(() => ArcGisOperations.CloseViewAsync(mapName), nameof(CloseView));

        [McpServerTool]
        [Description("Get the current extent (bounding box) of a view.")]
        public static Task<string> GetViewExtent(
            [Description("Exact map name, or empty for the active view.")]
            string mapName = "")
            => RunLogged(() => ArcGisOperations.GetViewExtentAsync(mapName), nameof(GetViewExtent));

        [McpServerTool]
        [Description("Zoom a view to a given extent, in the map's own spatial reference.")]
        public static Task<string> ZoomToExtent(
            double xmin, double ymin, double xmax, double ymax,
            [Description("Exact map name, or empty for the active view.")]
            string mapName = "")
            => RunLogged(() => ArcGisOperations.ZoomToExtentAsync(xmin, ymin, xmax, ymax, mapName), nameof(ZoomToExtent));

        [McpServerTool]
        [Description("Zoom a view in by one fixed step.")]
        public static Task<string> ZoomIn(
            [Description("Exact map name, or empty for the active view.")]
            string mapName = "")
            => RunLogged(() => ArcGisOperations.ZoomInAsync(mapName), nameof(ZoomIn));

        [McpServerTool]
        [Description("Zoom a view out by one fixed step.")]
        public static Task<string> ZoomOut(
            [Description("Exact map name, or empty for the active view.")]
            string mapName = "")
            => RunLogged(() => ArcGisOperations.ZoomOutAsync(mapName), nameof(ZoomOut));

        [McpServerTool]
        [Description("Select features intersecting a given extent in a view, across all its layers.")]
        public static Task<string> SelectByExtent(
            double xmin, double ymin, double xmax, double ymax,
            [Description("Exact map name, or empty for the active view.")]
            string mapName = "")
            => RunLogged(() => ArcGisOperations.SelectByExtentAsync(xmin, ymin, xmax, ymax, mapName), nameof(SelectByExtent));

        [McpServerTool]
        [Description("Render a view to a PNG file -- the only way to actually see what's on screen.")]
        public static Task<string> ExportView(
            [Description("Full output path for the PNG. Empty for a timestamped temp file.")]
            string outputPath = "",
            [Description("Exact map name, or empty for the active view.")]
            string mapName = "")
            => RunLogged(() => ArcGisOperations.ExportViewAsync(outputPath, mapName), nameof(ExportView));

        [McpServerTool]
        [Description("List every bookmark in a map.")]
        public static Task<string> ListBookmarks(
            [Description("Exact map name, or empty for the active map.")]
            string mapName = "")
            => RunLogged(() => ArcGisOperations.ListBookmarksAsync(mapName), nameof(ListBookmarks));

        [McpServerTool]
        [Description("Create a bookmark from a view's current camera position.")]
        public static Task<string> AddBookmark(
            [Description("Name for the new bookmark.")]
            string bookmarkName,
            [Description("Exact map name, or empty for the active view.")]
            string mapName = "")
            => RunLogged(() => ArcGisOperations.AddBookmarkAsync(bookmarkName, mapName), nameof(AddBookmark));

        [McpServerTool]
        [Description("Zoom a view to a named bookmark.")]
        public static Task<string> ZoomToBookmark(
            [Description("Exact bookmark name.")]
            string bookmarkName,
            [Description("Exact map name, or empty for the active view.")]
            string mapName = "")
            => RunLogged(() => ArcGisOperations.ZoomToBookmarkAsync(bookmarkName, mapName), nameof(ZoomToBookmark));

        [McpServerTool]
        [Description("Get a view's current camera: position, scale, heading, and pitch.")]
        public static Task<string> GetCamera(
            [Description("Exact map name, or empty for the active view.")]
            string mapName = "")
            => RunLogged(() => ArcGisOperations.GetCameraAsync(mapName), nameof(GetCamera));
    }
}

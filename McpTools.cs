using System;
using System.Collections.Generic;
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

        [McpServerTool]
        [Description("Set a view's camera directly -- the write half of get_camera. Mainly useful for 3D scenes.")]
        public static Task<string> SetCamera(
            double x, double y,
            double? z = null, double? scale = null, double? heading = null, double? pitch = null,
            [Description("Exact map name, or empty for the active view.")]
            string mapName = "")
            => RunLogged(() => ArcGisOperations.SetCameraAsync(x, y, z, scale, heading, pitch, mapName), nameof(SetCamera));

        [McpServerTool]
        [Description("Insert a new 3D scene into the currently open project and open its view.")]
        public static Task<string> InsertScene(
            [Description("Name for the new scene. Defaults to 'Scene'/'Global Scene' if left empty.")]
            string sceneName = "",
            [Description("True for a global (whole-earth) scene, false for a local scene.")]
            bool isGlobal = false)
            => RunLogged(() => ArcGisOperations.InsertSceneAsync(sceneName, isGlobal), nameof(InsertScene));

        [McpServerTool]
        [Description("Undo the last operation on a map. arcpy has no concept of this at all.")]
        public static Task<string> Undo(
            [Description("Exact map name, or empty for the active view.")]
            string mapName = "")
            => RunLogged(() => ArcGisOperations.UndoAsync(mapName), nameof(Undo));

        [McpServerTool]
        [Description("Redo the last undone operation on a map.")]
        public static Task<string> Redo(
            [Description("Exact map name, or empty for the active view.")]
            string mapName = "")
            => RunLogged(() => ArcGisOperations.RedoAsync(mapName), nameof(Redo));

        [McpServerTool]
        [Description("Show a message box in ArcGIS Pro itself -- only possible because a live window exists.")]
        public static Task<string> ShowMessage(
            string message,
            [Description("Dialog title. Defaults to 'MCP' if left empty.")]
            string caption = "")
            => RunLogged(() => ArcGisOperations.ShowMessageAsync(message, caption), nameof(ShowMessage));

        [McpServerTool]
        [Description("Activate a Pro tool/command by its DAML id, e.g. 'esri_mapping_exploreTool'.")]
        public static Task<string> ActivateTool(string toolId)
            => RunLogged(() => ArcGisOperations.ActivateToolAsync(toolId), nameof(ActivateTool));

        [McpServerTool]
        [Description("Get the currently active Pro tool/command's DAML id.")]
        public static Task<string> GetCurrentTool() => RunLogged(ArcGisOperations.GetCurrentToolAsync, nameof(GetCurrentTool));

        // ---- v2: data editing via EditOperation -- genuinely distinct from arcpy's
        // cursors since these participate in Pro's own undo/redo stack. Point geometry
        // only for now.

        [McpServerTool]
        [Description("Create a new point feature in a layer with the given attributes.")]
        public static Task<string> CreateFeature(
            [Description("Exact layer name to create the feature in.")]
            string layerName,
            double x, double y,
            [Description("JSON object of field values, e.g. {\"NAME\":\"foo\",\"COUNT\":3}. Empty for no attributes.")]
            string attributesJson = "",
            [Description("Exact map name, or empty for the active view.")]
            string mapName = "")
            => RunLogged(() => ArcGisOperations.CreateFeatureAsync(layerName, x, y, attributesJson, mapName), nameof(CreateFeature));

        [McpServerTool]
        [Description("Move a point feature to a new location.")]
        public static Task<string> UpdateFeatureGeometry(
            [Description("Object ID of the feature to move.")]
            long objectId,
            double x, double y,
            [Description("Exact layer name the feature belongs to.")]
            string layerName,
            [Description("Exact map name, or empty for the active view.")]
            string mapName = "")
            => RunLogged(() => ArcGisOperations.UpdateFeatureGeometryAsync(objectId, x, y, layerName, mapName), nameof(UpdateFeatureGeometry));

        [McpServerTool]
        [Description("Update one or more attribute values on a feature. Get the object id from get_selected_features.")]
        public static Task<string> UpdateFeatureAttributes(
            [Description("Object ID of the feature to update.")]
            long objectId,
            [Description("JSON object of field values to change, e.g. {\"NAME\":\"fixed\"}.")]
            string attributesJson,
            [Description("Exact layer name the feature belongs to.")]
            string layerName,
            [Description("Exact map name, or empty for the active view.")]
            string mapName = "")
            => RunLogged(() => ArcGisOperations.UpdateFeatureAttributesAsync(objectId, attributesJson, layerName, mapName), nameof(UpdateFeatureAttributes));

        [McpServerTool]
        [Description("Delete a feature by object id.")]
        public static Task<string> DeleteFeature(
            long objectId,
            [Description("Exact layer name the feature belongs to.")]
            string layerName,
            [Description("Exact map name, or empty for the active view.")]
            string mapName = "")
            => RunLogged(() => ArcGisOperations.DeleteFeatureAsync(objectId, layerName, mapName), nameof(DeleteFeature));

        [McpServerTool]
        [Description("Save all unsaved data edits. Distinct from save_project, which saves the .aprx itself.")]
        public static Task<string> SaveEdits() => RunLogged(ArcGisOperations.SaveEditsAsync, nameof(SaveEdits));

        [McpServerTool]
        [Description("Discard all unsaved data edits.")]
        public static Task<string> DiscardEdits() => RunLogged(ArcGisOperations.DiscardEditsAsync, nameof(DiscardEdits));

        [McpServerTool]
        [Description("Enable/disable snapping and set which snap modes are active (e.g. Vertex, Edge, Endpoint). arcpy has no interactive cursor, so no concept of this at all.")]
        public static Task<string> SetSnapping(
            bool enabled,
            [Description("Snap mode names, e.g. [\"Vertex\", \"Edge\"]. Leave empty to just toggle enabled without changing modes.")]
            List<string>? snapModes = null)
            => RunLogged(() => ArcGisOperations.SetSnappingAsync(enabled, snapModes ?? new List<string>()), nameof(SetSnapping));

        [McpServerTool]
        [Description("Read back whatever is currently selected in a view, including selections a human made by clicking.")]
        public static Task<string> GetSelectedFeatures(
            [Description("Exact map name, or empty for the active view.")]
            string mapName = "")
            => RunLogged(() => ArcGisOperations.GetSelectedFeaturesAsync(mapName), nameof(GetSelectedFeatures));

        [McpServerTool]
        [Description("Open a live attribute table pane for a layer.")]
        public static Task<string> OpenTable(
            [Description("Exact layer name.")]
            string layerName,
            [Description("Exact map name, or empty for the active view.")]
            string mapName = "")
            => RunLogged(() => ArcGisOperations.OpenTableAsync(layerName, mapName), nameof(OpenTable));

        [McpServerTool]
        [Description("Close the open attribute table pane for a layer.")]
        public static Task<string> CloseTable(
            [Description("Exact layer name.")]
            string layerName)
            => RunLogged(() => ArcGisOperations.CloseTableAsync(layerName), nameof(CloseTable));

        [McpServerTool]
        [Description("List every currently open attribute table pane.")]
        public static Task<string> ListOpenTables() => RunLogged(ArcGisOperations.ListOpenTablesAsync, nameof(ListOpenTables));
    }
}

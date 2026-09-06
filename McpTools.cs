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
        [McpServerTool]
        [Description("Test the connection to ArcGIS Pro. Call this first to confirm the bridge is up.")]
        public static Task<string> Ping() => ArcGisOperations.PingAsync();

        [McpServerTool]
        [Description("Open a map/scene/globe view in ArcGIS Pro -- something arcpy alone cannot do. " +
                      "Opens the active or first map in the project if mapName is left empty.")]
        public static Task<string> OpenView(
            [Description("Exact map name as shown in the Catalog pane, or empty for the active/first map.")]
            string mapName = "")
            => ArcGisOperations.OpenViewAsync(mapName);

        [McpServerTool]
        [Description("Create a brand-new ArcGIS Pro project without a template, the same as " +
                      "'Start without a template' on the Home screen.")]
        public static Task<string> CreateProject(
            [Description("Name for the new project.")]
            string projectName,
            [Description("Folder to create the project in. Empty for the default Documents\\ArcGIS\\Projects folder.")]
            string folderPath = "")
            => ArcGisOperations.CreateProjectAsync(projectName, folderPath);

        [McpServerTool]
        [Description("Insert a new blank map into the currently open project and open its view.")]
        public static Task<string> InsertMap(
            [Description("Name for the new map. Defaults to 'Map' if left empty.")]
            string mapName = "")
            => ArcGisOperations.InsertMapAsync(mapName);
    }
}

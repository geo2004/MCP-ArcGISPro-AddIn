namespace MCPArcGISProAddIn
{
    /// <summary>
    /// Request sent over the named pipe. Deliberately tiny for V1 -- one operation.
    /// </summary>
    public class BridgeRequest
    {
        /// <summary>Operation name. V1 supports only "open_view" and "ping".</summary>
        public string Op { get; set; } = "";

        /// <summary>For open_view: the map name to open (Contents-pane / Catalog name).
        /// Empty/null means "the project's active map, or its first map".</summary>
        public string MapName { get; set; } = "";
    }

    /// <summary>Response sent back over the named pipe.</summary>
    public class BridgeResponse
    {
        public bool Ok { get; set; }
        public string? Error { get; set; }
        public string? Data { get; set; }

        public static BridgeResponse Success(string? data = null) =>
            new BridgeResponse { Ok = true, Data = data };

        public static BridgeResponse Failure(string error) =>
            new BridgeResponse { Ok = false, Error = error };
    }
}

using ArcGIS.Desktop.Framework;
using ArcGIS.Desktop.Framework.Contracts;

namespace MCPArcGISProAddIn
{
    /// <summary>
    /// Add-in module. autoLoad="true" in Config.daml, so this starts the MCP host
    /// as soon as ArcGIS Pro loads the add-in -- no manual step. A Start/Stop ribbon
    /// button (like MCP-ArcGISPro's MCP_Bridge.pyt toolbox has) can come later via
    /// VS's Add-In Designer once this is confirmed working.
    /// </summary>
    internal class Module1 : Module
    {
        private static Module1? _this;
        private readonly McpHostService _bridge = new McpHostService();

        /// <summary>Singleton accessor -- id must match Config.daml's insertModule id.</summary>
        public static Module1 Current =>
            _this ??= (Module1)FrameworkApplication.FindModule("MCPArcGISProAddIn_Module");

        protected override bool Initialize()
        {
            _bridge.Start();
            return base.Initialize();
        }

        protected override void Uninitialize()
        {
            _bridge.Stop();
            base.Uninitialize();
        }

        /// <summary>Called by Pro when the user tries to close the app.</summary>
        protected override bool CanUnload()
        {
            _bridge.Stop();
            return true;
        }
    }
}

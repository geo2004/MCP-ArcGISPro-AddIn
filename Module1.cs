using System;
using System.IO;
using ArcGIS.Desktop.Framework;
using ArcGIS.Desktop.Framework.Contracts;

namespace MCPArcGISProAddIn
{
    /// <summary>
    /// Add-in module. autoLoad="true" in Config.daml, so this starts the MCP host
    /// as soon as ArcGIS Pro loads the add-in -- no manual step. A Start/Stop ribbon
    /// button (like MCP-ArcGISPro's MCP_Bridge.pyt toolbox has) can come later via
    /// VS's Add-In Designer once this is confirmed working.
    ///
    /// IMPORTANT: Initialize()/Uninitialize()/CanUnload() run as part of ArcGIS Pro's
    /// own startup/shutdown sequence. An unhandled exception here crashed the entire
    /// host process on first live test (0xe0434352 -- a .NET unhandled-exception
    /// signature, not a native crash; most likely an assembly version conflict
    /// between the MCP SDK's Microsoft.Extensions.* dependencies and the versions
    /// ArcGIS Pro's own process already has loaded). Every call into _bridge is now
    /// wrapped so a failure to start/stop the MCP host can NEVER take ArcGIS Pro down
    /// with it -- worst case, the MCP capability just doesn't come up, logged to a
    /// file instead of crashing.
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
            SafeCall(_bridge.Start, "Start");
            return base.Initialize();
        }

        protected override void Uninitialize()
        {
            SafeCall(_bridge.Stop, "Stop (Uninitialize)");
            base.Uninitialize();
        }

        /// <summary>Called by Pro when the user tries to close the app.</summary>
        protected override bool CanUnload()
        {
            SafeCall(_bridge.Stop, "Stop (CanUnload)");
            return true;
        }

        private static void SafeCall(Action action, string what)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                LogStartupError(what, ex);
            }
        }

        private static void LogStartupError(string what, Exception ex)
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
                // If even logging fails, there's nothing safe left to do -- give up quietly
                // rather than risk another exception during Pro's own startup/shutdown.
            }
        }
    }
}

using System;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ArcGIS.Desktop.Core;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Mapping;

namespace MCPArcGISProAddIn
{
    /// <summary>
    /// Named-pipe listener + dispatcher for the handful of operations that genuinely
    /// need this Add-In (i.e. can't be done from the Python bridge). Runs on its own
    /// background thread, started from Module1.Initialize/Uninitialize.
    ///
    /// Confidence notes (this whole file is written but not yet compile-tested --
    /// the SDK isn't installed on this machine yet):
    ///  - The named-pipe server loop below is plain .NET (System.IO.Pipes), not
    ///    ArcGIS-specific -- high confidence.
    ///  - QueuedTask.Run usage is confirmed against a real, working ArcGIS Pro
    ///    Add-In sample (nicogis/MCP-Server-ArcGIS-Pro-AddIn) that does the same
    ///    "IPC handler awaits QueuedTask.Run" pattern for Map/Layer access.
    ///  - OpenMapPaneAsync's UI-thread requirement, and the Dispatcher-hop needed to
    ///    satisfy it from this background listener thread, is my own synthesis from
    ///    the ArcGIS Pro SDK docs + standard WPF Dispatcher.InvokeAsync usage -- not
    ///    seen combined in one working example. This is the one piece to watch
    ///    closely on first live test.
    /// </summary>
    internal class BridgeService
    {
        private const string PipeName = "MCPArcGISProAddInPipe";
        private CancellationTokenSource? _cts;
        private Task? _listenLoop;

        public void Start()
        {
            if (_listenLoop != null) return; // already running
            _cts = new CancellationTokenSource();
            _listenLoop = Task.Run(() => ListenLoopAsync(_cts.Token));
        }

        public void Stop()
        {
            _cts?.Cancel();
            _listenLoop = null;
        }

        private async Task ListenLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(
                        PipeName, PipeDirection.InOut, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

                    await server.WaitForConnectionAsync(token);

                    string requestJson = await ReadMessageAsync(server, token);
                    BridgeResponse response;
                    try
                    {
                        var request = JsonSerializer.Deserialize<BridgeRequest>(requestJson)
                                      ?? new BridgeRequest();
                        response = await DispatchAsync(request);
                    }
                    catch (Exception ex)
                    {
                        response = BridgeResponse.Failure($"{ex.GetType().Name}: {ex.Message}");
                    }

                    string responseJson = JsonSerializer.Serialize(response);
                    await WriteMessageAsync(server, responseJson, token);
                }
                catch (OperationCanceledException)
                {
                    break; // Stop() was called
                }
                catch (Exception)
                {
                    // One bad connection shouldn't kill the whole listener. V1 has no
                    // logging yet -- add it here once this is actually running.
                }
            }
        }

        private async Task<BridgeResponse> DispatchAsync(BridgeRequest request)
        {
            switch (request.Op)
            {
                case "ping":
                    return BridgeResponse.Success("pong");

                case "open_view":
                    return await OpenViewAsync(request.MapName);

                default:
                    return BridgeResponse.Failure($"Unknown op '{request.Op}'. Supported: ping, open_view");
            }
        }

        /// <summary>
        /// Opens a pane for the named map (or the active/first map if mapName is
        /// empty). This is the one thing this whole Add-In exists for -- arcpy has
        /// no equivalent, at all, on the Python side.
        /// </summary>
        private async Task<BridgeResponse> OpenViewAsync(string mapName)
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
                return BridgeResponse.Failure(
                    string.IsNullOrEmpty(mapName)
                        ? "No maps exist in this project."
                        : $"No map named '{mapName}' found in this project.");
            }

            // OpenMapPaneAsync must run on ArcGIS Pro's UI thread specifically (per
            // the SDK docs) -- QueuedTask.Run marshals onto a *different* thread
            // (safe for CIM/data access, not the same as the UI thread), so this
            // needs its own hop via the WPF dispatcher. Unverified live -- watch
            // this first.
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null)
            {
                return BridgeResponse.Failure("No WPF dispatcher available (unexpected outside ArcGIS Pro).");
            }

            await dispatcher.InvokeAsync(() => mapItem.OpenMapPaneAsync()).Task.Unwrap();

            return BridgeResponse.Success($"Opened view for map '{mapItem.Name}'.");
        }

        // --- length-prefixed message framing (plain .NET, not ArcGIS-specific) ---

        private static async Task<string> ReadMessageAsync(NamedPipeServerStream stream, CancellationToken token)
        {
            var lengthBuf = new byte[4];
            await stream.ReadExactlyAsync(lengthBuf, 0, 4, token);
            int length = BitConverter.ToInt32(lengthBuf, 0);
            var buf = new byte[length];
            await stream.ReadExactlyAsync(buf, 0, length, token);
            return Encoding.UTF8.GetString(buf);
        }

        private static async Task WriteMessageAsync(NamedPipeServerStream stream, string message, CancellationToken token)
        {
            var bytes = Encoding.UTF8.GetBytes(message);
            var lengthBuf = BitConverter.GetBytes(bytes.Length);
            await stream.WriteAsync(lengthBuf, 0, 4, token);
            await stream.WriteAsync(bytes, 0, bytes.Length, token);
            await stream.FlushAsync(token);
        }
    }
}

"""Manual test client for the MCP-ArcGISPro-AddIn's named pipe bridge.

Pure stdlib -- Windows named pipes are reachable via a plain open() in binary
mode, no pywin32 needed. Not yet run against a real server (the SDK isn't
installed on this machine yet); the framing here matches BridgeService.cs's
ReadMessageAsync/WriteMessageAsync exactly (4-byte little-endian length prefix,
then UTF-8 JSON), so it should just work once the Add-In is actually running --
but treat the first real run as the actual test of this file too.

Usage:
    python test_client.py ping
    python test_client.py open_view
    python test_client.py open_view "Map"

Requires the Add-In to already be loaded inside a running ArcGIS Pro session
with a project open (Config.daml's autoLoad="true" starts the listener
automatically once the add-in is installed).
"""
import json
import struct
import sys
import time

PIPE_PATH = r"\\.\pipe\MCPArcGISProAddInPipe"


def send(op: str, map_name: str = "") -> dict:
    with open(PIPE_PATH, "r+b", buffering=0) as pipe:
        request = json.dumps({"Op": op, "MapName": map_name}).encode("utf-8")
        pipe.write(struct.pack("<i", len(request)))
        pipe.write(request)

        length = struct.unpack("<i", pipe.read(4))[0]
        response = pipe.read(length)
        return json.loads(response.decode("utf-8"))


if __name__ == "__main__":
    op = sys.argv[1] if len(sys.argv) > 1 else "ping"
    map_name = sys.argv[2] if len(sys.argv) > 2 else ""

    t0 = time.time()
    try:
        result = send(op, map_name)
    except FileNotFoundError:
        print("Could not connect -- is ArcGIS Pro running with the add-in loaded "
              "and a project open?")
        sys.exit(1)
    print(f"{result}  ({(time.time() - t0) * 1000:.0f}ms)")

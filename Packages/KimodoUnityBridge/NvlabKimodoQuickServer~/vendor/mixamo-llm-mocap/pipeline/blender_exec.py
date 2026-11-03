"""Run a Python file inside the live Blender session, over the official
Blender MCP add-on's bridge socket (localhost:9876).

This is the direct path an agent (or a human) can use when the MCP
client layer is not available — the add-on's protocol is null-delimited
JSON: {"type": "execute", "code": "<python>", "strict_json": bool}.
The executed code should set `result = {...}` for a structured reply;
stdout/stderr are captured into the response.

Usage:
  python pipeline\\blender_exec.py <file.py> [timeout_seconds]

Blender must be open with the MCP add-on's server running (it
autostarts if "Allow Online Access" is enabled in Preferences >
System > Network). A long apply pass blocks Blender's UI until it
finishes — that is normal.
"""

from __future__ import annotations

import json
import socket
import sys

HOST, PORT = "localhost", 9876


def execute_file(path: str, timeout: float = 120.0) -> dict:
    code = open(path, encoding="utf-8").read()
    s = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    s.settimeout(timeout)
    s.connect((HOST, PORT))
    s.sendall((json.dumps({"type": "execute", "code": code, "strict_json": False}) + "\0").encode())
    buf = bytearray()
    while b"\0" not in buf:
        chunk = s.recv(65536)
        if not chunk:
            break
        buf.extend(chunk)
    s.close()
    if b"\0" not in buf:
        raise SystemExit("no complete response from Blender (is the add-on server running?)")
    return json.loads(bytes(buf[: buf.index(b"\0")]).decode())


if __name__ == "__main__":
    if len(sys.argv) < 2:
        raise SystemExit(__doc__)
    t = float(sys.argv[2]) if len(sys.argv) > 2 else 120.0
    res = execute_file(sys.argv[1], t)
    print(json.dumps(res, indent=1))
    # Propagate Blender-side failures as a nonzero exit so shell chains
    # (`&&`) cannot mistake an in-Blender traceback for success.
    if res.get("status") != "ok":
        sys.exit(1)

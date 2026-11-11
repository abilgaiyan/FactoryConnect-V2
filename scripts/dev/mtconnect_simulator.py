#!/usr/bin/env python3
"""Loopback-only, deterministic MTConnect sample source for FC2 development.

No SQL access, no factory connection, no runtime authority or commissioning side effects.
"""
import argparse
from datetime import datetime, timedelta, timezone
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import urlsplit, parse_qs
from xml.sax.saxutils import escape

DEVICE_UUID = "fc2-local-sim-0001"
INSTANCE_ID = 2026111101
DATA_ITEM = "execution"
VALUES = ("READY", "ACTIVE", "ACTIVE", "STOPPED", "READY", "ACTIVE", "STOPPED")
MAX_RETAINED = 10000


def frame(first_requested: int, count: int, available: int, start: datetime) -> bytes:
    if first_requested < 1 or count < 1 or count > 1000:
        raise ValueError("from must be >= 1 and count must be 1..1000")
    # Retained source window is fixed at sequence 1 for this bounded local demo.
    last = max(1, available)
    if first_requested > last + 1:
        raise ValueError("requested sequence is ahead of the retained source")
    end = min(available + 1, first_requested + count)
    items = []
    for seq in range(first_requested, end):
        timestamp = (start + timedelta(seconds=seq - 1)).isoformat(timespec="milliseconds").replace("+00:00", "Z")
        value = VALUES[(seq - 1) % len(VALUES)]
        items.append(f'<Execution dataItemId="{DATA_ITEM}" sequence="{seq}" timestamp="{timestamp}">{escape(value)}</Execution>')
    next_seq = end if items else first_requested
    return (f'<?xml version="1.0" encoding="UTF-8"?>'
            f'<MTConnectStreams xmlns="urn:mtconnect.org:MTConnectStreams:2.0">'
            f'<Header instanceId="{INSTANCE_ID}" firstSequence="1" lastSequence="{last}" nextSequence="{next_seq}"/>'
            f'<Streams><DeviceStream name="FC2-LOCAL-SIM" uuid="{DEVICE_UUID}">'
            f'<ComponentStream component="Controller" name="controller" componentId="controller">'
            f'<Events>{"".join(items)}</Events>'
            f'</ComponentStream></DeviceStream></Streams></MTConnectStreams>').encode("utf-8")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--port", type=int, default=5055)
    parser.add_argument("--observations", type=int, default=120, help="Fixed retained observations, 1..10000")
    args = parser.parse_args()
    if not (1024 <= args.port <= 65535 and 1 <= args.observations <= MAX_RETAINED):
        parser.error("port must be 1024..65535; observations must be 1..10000")
    started = datetime(2026, 11, 11, 0, 0, 0, tzinfo=timezone.utc)  # Stable provenance across restarts

    class Handler(BaseHTTPRequestHandler):
        def do_GET(self):
            uri = urlsplit(self.path)
            if uri.path == "/health":
                data = b'{"status":"ready","kind":"simulated-mtconnect"}'
                content_type = "application/json"
            elif uri.path == "/device/sample":
                try:
                    params = parse_qs(uri.query, keep_blank_values=True, strict_parsing=True)
                    if set(params) != {"from", "count"} or any(len(v) != 1 for v in params.values()):
                        raise ValueError("expected from and count")
                    first, count = int(params["from"][0]), int(params["count"][0])
                    data = frame(first, count, args.observations, started)
                except (ValueError, OverflowError):
                    self.send_error(400, "Invalid or out-of-range MTConnect sample request")
                    return
                content_type = "application/xml"
            else:
                self.send_error(404, "Not found")
                return
            self.send_response(200)
            self.send_header("Content-Type", content_type + "; charset=utf-8")
            self.send_header("Content-Length", str(len(data)))
            self.send_header("Cache-Control", "no-store")
            self.end_headers()
            self.wfile.write(data)

    server = ThreadingHTTPServer(("127.0.0.1", args.port), Handler)
    print(f"FC2 simulated source: http://127.0.0.1:{args.port}/device/", flush=True)
    print(f"UUID={DEVICE_UUID} instanceId={INSTANCE_ID} retained=1..{args.observations}", flush=True)
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        server.server_close()


if __name__ == "__main__":
    main()

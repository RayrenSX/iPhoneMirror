#!/usr/bin/env python3
"""Serve HLS files while failing the first manifest requests for recovery tests."""

from __future__ import annotations

import argparse
import functools
import http.server
import pathlib
import threading


class RecoveryHandler(http.server.SimpleHTTPRequestHandler):
    failures_remaining = 0
    failures_lock = threading.Lock()

    def do_GET(self) -> None:  # noqa: N802 - required by BaseHTTPRequestHandler
        if self.path.split("?", 1)[0].lower().endswith((".m3u8", ".m3u")):
            with self.failures_lock:
                if self.failures_remaining > 0:
                    type(self).failures_remaining -= 1
                    self.send_error(503, "intentional transient HLS failure")
                    return
        super().do_GET()

    def log_message(self, format: str, *args: object) -> None:
        return


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", type=pathlib.Path, required=True)
    parser.add_argument("--port", type=int, default=18093)
    parser.add_argument("--fail-manifests", type=int, default=1)
    args = parser.parse_args()

    root = args.root.resolve(strict=True)
    RecoveryHandler.failures_remaining = max(0, args.fail_manifests)
    handler = functools.partial(RecoveryHandler, directory=str(root))
    server = http.server.ThreadingHTTPServer(("127.0.0.1", args.port), handler)
    server.serve_forever()


if __name__ == "__main__":
    main()

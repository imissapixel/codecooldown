#!/usr/bin/env python3
"""One bounded JSON request on stdin, one sanitized JSON response on stdout."""
from __future__ import annotations

import json
import signal
import sys

from code_usage_monitor import VERSION
from code_usage_monitor.contracts import UsageError, source_id, source_revision, utcnow, validate_profile
from code_usage_monitor.discovery import discover
from code_usage_monitor.subscriptions import claude, codex


def handle(request) -> dict:
    p = None
    try:
        if not isinstance(request, dict) or request.get("version") != 1:
            raise UsageError("unsupported_response", "Unsupported helper protocol version.")
        operation = request.get("operation")
        if operation == "discover":
            return discover()
        if operation == "diagnostics":
            return {"version": 1, "adapter_version": VERSION, "python": f"{sys.version_info.major}.{sys.version_info.minor}",
                    "status": "ready", "fetched_at": utcnow()}
        p = validate_profile(request.get("profile"))
        if operation == "validate":
            # Include adapter version so an upgrade can recover a cached auth error.
            return {"version": 1, "profile_id": p["id"], "source_id": source_id(p),
                    "source_revision": source_revision(p), "status": "ready"}
        if operation != "fetch":
            raise UsageError("unsupported_response", "Unsupported helper operation.")
        reading = claude(p) if p["provider"] == "claude" else codex(p)
        return {**reading, "source_revision": source_revision(p)}
    except UsageError as e:
        return {"version": 1, "adapter_version": VERSION, "profile_id": p["id"] if p else None,
                "provider": p["provider"] if p else None, "source_id": source_id(p) if p else None,
                "source_revision": source_revision(p) if p else None,
                "status": e.status, "fetched_at": utcnow(), "measurements": [], "notices": [],
                "error": e.message, "retry_after_seconds": e.retry_after}
    except Exception:
        # Never serialize exception text: library errors can include credentials or raw responses.
        return {"version": 1, "adapter_version": VERSION, "profile_id": p["id"] if p else None,
                "status": "unsupported_response", "fetched_at": utcnow(), "measurements": [], "notices": [],
                "error": "The adapter could not normalize this response. Check the mapping and adapter version."}


def main():
    signal.signal(signal.SIGALRM, lambda *_: (_ for _ in ()).throw(UsageError("network_error", "The helper exceeded its work deadline.")))
    signal.alarm(110)
    try:
        raw = sys.stdin.buffer.read(65537)
        if len(raw) > 65536:
            raise ValueError()
        response = handle(json.loads(raw))
    except Exception:
        response = {"version": 1, "status": "unsupported_response", "measurements": [],
                    "error": "Send one valid protocol-v1 JSON request, at most 64 KiB."}
    sys.stdout.write(json.dumps(response, allow_nan=False, separators=(",", ":")) + "\n")


if __name__ == "__main__":
    main()

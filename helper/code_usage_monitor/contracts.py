from __future__ import annotations

import hashlib
import math
import re
from datetime import datetime, timezone
from pathlib import Path

from . import VERSION
PROVIDERS = {"claude", "codex"}
STATUSES = {"ready", "login_required", "token_expired", "permission_denied", "setup_required",
            "rate_limited", "network_error", "unsupported_response", "wsl_unavailable"}
MAX_BYTES = 2 * 1024 * 1024


class UsageError(Exception):
    def __init__(self, status: str, message: str, retry_after: int | None = None):
        super().__init__(message)
        self.status = status
        self.message = message
        self.retry_after = retry_after


def utcnow() -> str:
    return datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")


def timestamp(value) -> str | None:
    try:
        if isinstance(value, (int, float)) and not isinstance(value, bool):
            if not math.isfinite(value) or value <= 0:
                return None
            dt = datetime.fromtimestamp(value, timezone.utc)
        elif isinstance(value, str):
            dt = datetime.fromisoformat(value.replace("Z", "+00:00"))
            if dt.tzinfo is None:
                return None
        else:
            return None
        return dt.astimezone(timezone.utc).isoformat().replace("+00:00", "Z")
    except (ValueError, OverflowError, OSError):
        return None


def number(value) -> float | None:
    if isinstance(value, (float, int)) and not isinstance(value, bool) and math.isfinite(value) and value >= 0:
        return float(value)
    return None


def digest(value: str) -> str:
    return hashlib.sha256(value.encode()).hexdigest()[:24]


def text(value, max_length=512) -> str:
    if not isinstance(value, str) or len(value) > max_length or any(ord(c) < 32 for c in value):
        raise UsageError("setup_required", "A profile field is invalid. Check profile settings.")
    return value


def validate_profile(value) -> dict:
    if not isinstance(value, dict):
        raise UsageError("setup_required", "A profile object is required.")
    p = {key: text(value.get(key, "")) for key in ("id", "provider", "home", "auth_path")}
    if p["provider"] not in PROVIDERS or not re.fullmatch(r"[A-Za-z0-9_-]{1,64}", p["id"]):
        raise UsageError("setup_required", "Choose a supported provider and a valid profile ID.")
    p["home"] = p["home"] or str(Path.home())
    for key in ("home", "auth_path"):
        if p[key] and not p[key].startswith("/"):
            raise UsageError("setup_required", "Linux paths must be absolute.")
    return p


def source_id(p: dict) -> str:
    return p["provider"] + ":" + digest(str(Path(p["auth_path"]).resolve()))


def source_revision(p: dict) -> str:
    try:
        info = Path(p["auth_path"]).stat()
        return digest(f"{VERSION}:{info.st_dev}:{info.st_ino}:{info.st_mtime_ns}:{info.st_size}")
    except OSError:
        return "missing"


def measurement(kind: str, label: str, value, unit: str, scope: str, provenance: str,
                *, start=None, end=None, data_at=None, reset=None, duration=None,
                attribution="credential", currency=None) -> dict:
    return {"kind": kind, "label": label, "value": number(value), "unit": unit,
            "currency": currency, "period_start": timestamp(start), "period_end": timestamp(end),
            "fetched_at": utcnow(), "data_at": timestamp(data_at), "reset_at": timestamp(reset),
            "window_seconds": number(duration), "scope": scope, "provenance": provenance,
            "attribution": attribution}


def result(p: dict, measurements: list, *, notices=None, plan=None) -> dict:
    return {"version": 1, "adapter_version": VERSION, "profile_id": p["id"],
            "source_id": source_id(p), "provider": p["provider"], "status": "ready",
            "fetched_at": utcnow(), "measurements": measurements, "notices": notices or [],
            "plan": plan, "error": None, "retry_after_seconds": None}

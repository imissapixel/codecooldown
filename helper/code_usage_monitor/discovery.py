from __future__ import annotations

import os
import re
from pathlib import Path

MAX_PROFILES = 50
SUFFIX = re.compile(r"[A-Za-z0-9_-]{1,40}")
PROVIDERS = (("claude", "Claude", ".credentials.json"), ("codex", "Codex", "auth.json"))


def _profile(provider: str, label: str, suffix: str, auth: Path, home: Path) -> dict:
    name = f"{provider}-{suffix}" if suffix else provider
    return {"id": name, "display_name": f"{label} ({suffix})" if suffix else label, "command": name,
            "provider": provider, "home": str(home), "auth_path": str(auth), "enabled": True, "refresh_seconds": 300}


def discover() -> dict:
    # Existence checks only: credential contents are never read here.
    home = Path.home()
    defaults = {"claude": Path(os.environ.get("CLAUDE_CONFIG_DIR") or home / ".claude"),
                "codex": Path(os.environ.get("CODEX_HOME") or home / ".codex")}
    profiles, seen = [], set()
    for provider, label, filename in PROVIDERS:
        auth = defaults[provider] / filename
        profiles.append(_profile(provider, label, "", auth, home))
        seen.add(os.path.normpath(auth))
    for provider, label, filename in PROVIDERS:
        try:
            entries = sorted(home.glob(f".{provider}-*"))
        except OSError:
            entries = []
        for entry in entries:
            suffix = entry.name[len(provider) + 2:]
            auth = entry / filename
            if not SUFFIX.fullmatch(suffix) or os.path.normpath(auth) in seen or not auth.is_file():
                continue
            seen.add(os.path.normpath(auth))
            profiles.append(_profile(provider, label, suffix, auth, home))
    return {"version": 1, "profiles": profiles[:MAX_PROFILES], "capabilities": {"python": True},
            "notices": ["Discovery checks known credential locations only; it never reads credential contents or executes shell wrappers."]}

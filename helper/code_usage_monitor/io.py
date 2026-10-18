from __future__ import annotations

import email.utils
import json
import urllib.error
import urllib.request
from datetime import datetime, timezone
from pathlib import Path

from .contracts import MAX_BYTES, UsageError

CLAUDE_RENEWAL_ENDPOINT = "https://platform.claude.com/v1/oauth/token"
# Public Claude Code OAuth application identifier, shared across installations.
CLAUDE_CLIENT_ID = "9d1c250a-e61b-44d9-88ed-5944d1962f5e"  # Public application ID; gitleaks:allow


def read_json(path: str) -> dict:
    try:
        with Path(path).open("rb") as f:
            raw = f.read(MAX_BYTES + 1)
        if len(raw) > MAX_BYTES:
            raise UsageError("unsupported_response", "The credential file exceeds the supported size.")
        data = json.loads(raw)
        if not isinstance(data, dict):
            raise ValueError()
        return data
    except FileNotFoundError:
        raise UsageError("login_required", "Credential file missing. Sign in using this profile's CLI, then refresh.") from None
    except PermissionError:
        raise UsageError("permission_denied", "Cannot read this profile's credential file. Check Linux file access.") from None
    except (ValueError, OSError, RecursionError):
        raise UsageError("unsupported_response", "The credential file is not a supported JSON object.") from None


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


def http_json(url: str, headers: dict) -> dict:
    if url not in {"https://api.anthropic.com/api/oauth/usage", "https://chatgpt.com/backend-api/wham/usage"}:
        raise UsageError("setup_required", "Usage endpoint is not supported.")
    request = urllib.request.Request(url, headers=headers, method="GET")
    return _request_json(request)


def refresh_claude_token(token: str, client_id: str, scopes: list | None) -> dict:
    body = {"grant_type": "refresh_token", "refresh_token": token, "client_id": client_id}
    if scopes:
        body["scope"] = " ".join(scopes)
    request = urllib.request.Request(CLAUDE_RENEWAL_ENDPOINT, data=json.dumps(body).encode(), method="POST",
                                     headers={"Content-Type": "application/json", "Accept": "application/json",
                                              "User-Agent": "codecooldown/0.2.0"})
    return _request_json(request, renewal=True)


def _request_json(request: urllib.request.Request, *, renewal=False) -> dict:
    try:
        with urllib.request.build_opener(NoRedirect()).open(request, timeout=18) as response:
            raw = response.read(MAX_BYTES + 1)
        if len(raw) > MAX_BYTES:
            raise ValueError()
        value = json.loads(raw)
        if not isinstance(value, dict):
            raise ValueError()
        return value
    except urllib.error.HTTPError as e:
        if renewal and e.code in (400, 401, 403):
            if e.code == 400:
                try:
                    if json.loads(e.read(4097)).get("error") != "invalid_grant":
                        raise ValueError()
                except (ValueError, OSError, AttributeError, RecursionError):
                    raise UsageError("unsupported_response", "Claude rejected the token renewal request.") from None
            raise UsageError("login_required", "Claude could not renew this login. Sign in using this profile's CLI.") from None
        if e.code in (401, 403):
            category, expired = "", False
            try:
                error = json.loads(e.read(4097)).get("error", {})
                if isinstance(error, dict):
                    category = error.get("type", "")
                    message = str(error.get("message", "")).lower()
                    expired = "expired" in message and "token" in message
            except (ValueError, OSError, AttributeError, RecursionError):
                pass
            if expired:
                raise UsageError("token_expired", "The saved access token expired; your CLI may still be signed in. Use that CLI to renew credentials, then refresh usage.") from None
            if e.code == 403 and category == "permission_error":
                raise UsageError("permission_denied", "This account was denied usage access. CLI inference access can still be valid.") from None
            raise UsageError("login_required", "Usage authorization was rejected. Check this profile's CLI authentication status, then refresh usage.") from None
        if e.code == 429:
            seconds = 60
            retry = e.headers.get("Retry-After", "")
            try:
                seconds = int(retry) if retry.isdigit() else int((email.utils.parsedate_to_datetime(retry) - datetime.now(timezone.utc)).total_seconds())
            except (TypeError, ValueError, OverflowError):
                pass
            raise UsageError("rate_limited", "Usage requests are rate limited. Wait before refreshing.", min(86400, max(1, seconds))) from None
        if 500 <= e.code < 600:
            raise UsageError("network_error", "The usage service is temporarily unavailable.") from None
        raise UsageError("unsupported_response", "The usage endpoint rejected this read-only request.") from None
    except (ValueError, RecursionError):
        raise UsageError("unsupported_response", "The usage service returned an unsupported response.") from None
    except (OSError, urllib.error.URLError):
        raise UsageError("network_error", "Cannot reach the usage service. Check connectivity.") from None

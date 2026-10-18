"""Renew Claude credentials inside WSL without launching a CLI or making model requests."""
from __future__ import annotations

from contextlib import contextmanager
import json
import os
from pathlib import Path
import stat
import tempfile
import threading
import time

from .contracts import UsageError, number
from .io import CLAUDE_CLIENT_ID, read_json, refresh_claude_token

LOCK_WAIT_SECONDS = 10
LOCK_STALE_SECONDS = 60


def _token(value) -> bool:
    return isinstance(value, str) and 0 < len(value) <= 16384 and not any(c.isspace() or ord(c) < 32 for c in value)


def claude_oauth(auth: dict) -> dict:
    oauth = auth.get("claudeAiOauth")
    if not isinstance(oauth, dict) or not _token(oauth.get("accessToken")):
        raise UsageError("login_required", "No OAuth token at this Claude source. Sign in using this profile's CLI.")
    return oauth


def expired(oauth: dict) -> bool:
    expiry = number(oauth.get("expiresAt"))
    return expiry is not None and expiry <= time.time() * 1000


@contextmanager
def _refresh_lock(directory: Path):
    # Match Claude Code's current and legacy proper-lockfile directories. Inode
    # checks prevent releasing a replacement lock if another process takes over.
    locks = [directory / ".oauth_refresh.lock", directory.with_name(directory.name + ".lock")]
    held = []
    stopped = threading.Event()
    compromised = threading.Event()

    def owns(path, inode):
        try:
            return path.lstat().st_ino == inode
        except OSError:
            return False

    def heartbeat():
        while not stopped.wait(5):
            for path, inode in held:
                try:
                    if not owns(path, inode):
                        compromised.set()
                        return
                    os.utime(path, follow_symlinks=False)
                except OSError:
                    compromised.set()
                    return

    def check():
        if compromised.is_set() or any(not owns(path, inode) for path, inode in held):
            raise UsageError("network_error", "Claude credential renewal lost its lock. Retry shortly.")

    worker = None
    try:
        deadline = time.monotonic() + LOCK_WAIT_SECONDS
        for path in locks:
            while True:
                try:
                    path.mkdir(mode=0o700)
                    held.append((path, path.lstat().st_ino))
                    break
                except FileExistsError:
                    try:
                        info = path.lstat()
                        if stat.S_ISDIR(info.st_mode) and time.time() - info.st_mtime > LOCK_STALE_SECONDS:
                            if path.lstat() == info:
                                path.rmdir()
                                continue
                    except FileNotFoundError:
                        continue
                    if time.monotonic() >= deadline:
                        raise UsageError("network_error", "Another process is renewing Claude credentials. Retry shortly.")
                    time.sleep(0.1)
        worker = threading.Thread(target=heartbeat, daemon=True)
        worker.start()
        yield check
    finally:
        stopped.set()
        if worker is not None:
            worker.join()
        for path, inode in reversed(held):
            if owns(path, inode):
                try:
                    path.rmdir()
                except OSError:
                    pass


def renew_claude(path: str, rejected_token: str) -> dict:
    temporary = None
    try:
        target = Path(path).resolve(strict=True)
        with _refresh_lock(target.parent) as check_lock:
            auth = read_json(str(target))
            oauth = claude_oauth(auth)
            # A CLI or another helper may have renewed this account while we waited.
            if oauth["accessToken"] != rejected_token and not expired(oauth):
                return oauth
            refresh = oauth.get("refreshToken")
            if not _token(refresh):
                raise UsageError("login_required", "This Claude login has no refresh token. Sign in using this profile's CLI.")
            client = oauth.get("clientId") or CLAUDE_CLIENT_ID
            scopes = oauth.get("scopes")
            if not _token(client) or scopes is not None and (not isinstance(scopes, list) or not all(_token(s) for s in scopes)):
                raise UsageError("unsupported_response", "The Claude OAuth metadata is invalid.")
            # Verify that we can create a private replacement before rotating a token.
            with tempfile.NamedTemporaryFile(mode="w", encoding="utf-8", dir=target.parent,
                                             prefix=".codecooldown-", delete=False) as stream:
                temporary = Path(stream.name)
                check_lock()
                response = refresh_claude_token(refresh, client, scopes)
                access = response.get("access_token")
                rotated = response.get("refresh_token", refresh)
                seconds = number(response.get("expires_in"))
                if not _token(access) or not _token(rotated) or seconds is None or not 0 < seconds <= 31536000:
                    raise UsageError("unsupported_response", "Claude returned invalid renewed credentials.")
                updated = {**oauth, "accessToken": access, "refreshToken": rotated,
                           "expiresAt": int((time.time() + seconds) * 1000)}
                if "scope" in response:
                    scope = response["scope"]
                    if not isinstance(scope, str) or len(scope) > 16384 or not all(_token(s) for s in scope.split()):
                        raise UsageError("unsupported_response", "Claude returned invalid renewed scopes.")
                    updated["scopes"] = scope.split()
                if "refresh_token_expires_in" in response:
                    refresh_seconds = number(response["refresh_token_expires_in"])
                    if refresh_seconds is None or not 0 < refresh_seconds <= 315360000:
                        raise UsageError("unsupported_response", "Claude returned an invalid refresh token lifetime.")
                    updated["refreshTokenExpiresAt"] = int((time.time() + refresh_seconds) * 1000)
                # Preserve unrelated metadata, and do not overwrite an external login.
                latest = read_json(str(target))
                if latest.get("claudeAiOauth") != oauth:
                    current = claude_oauth(latest)
                    if current["accessToken"] != rejected_token and not expired(current):
                        return current
                    raise UsageError("network_error", "Claude credentials changed during renewal. Retry shortly.")
                latest["claudeAiOauth"] = updated
                json.dump(latest, stream, ensure_ascii=False, allow_nan=False)
                stream.flush()
                os.fsync(stream.fileno())
                check_lock()
                os.replace(temporary, target)
                temporary = None
                return updated
    except PermissionError:
        raise UsageError("permission_denied", "Cannot save renewed Claude credentials. Check Linux file access.") from None
    except OSError:
        raise UsageError("network_error", "Cannot safely renew Claude credentials. Check the credential file and retry.") from None
    finally:
        if temporary is not None:
            temporary.unlink(missing_ok=True)

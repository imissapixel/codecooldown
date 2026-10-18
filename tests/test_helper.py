import contextlib
import hashlib
import io
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
import urllib.error
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "helper"))
from monitor import handle
from code_usage_monitor import subscriptions
from code_usage_monitor.contracts import UsageError, source_id, validate_profile
from code_usage_monitor.discovery import discover
from code_usage_monitor.io import http_json

CANARY = "sk-secret-canary-NEVER-EXPORT-12345"


def profile(provider="claude", **kwargs):
    return validate_profile({"id": "test", "provider": provider, "home": "/home/test", "auth_path": "/home/test/auth.json", **kwargs})


class SubscriptionTests(unittest.TestCase):
    def test_claude_missing_and_null_windows_are_not_zero(self):
        r = subscriptions.parse_claude(profile(), {"five_hour": {"utilization": 0, "resets_at": None},
                "seven_day": None, "extra_usage": {"utilization": None, "is_enabled": False}})
        self.assertEqual(len(r["measurements"]), 1)
        self.assertEqual(r["measurements"][0]["value"], 0)
        self.assertIsNone(r["measurements"][0]["reset_at"])

    def test_expired_access_token_without_refresh_token_requires_login(self):
        with tempfile.TemporaryDirectory() as d:
            p = Path(d) / "auth.json"
            p.write_text(json.dumps({"claudeAiOauth": {"accessToken": CANARY, "expiresAt": 1}}))
            before = p.read_bytes()
            with patch.object(subscriptions, "http_json") as http:
                r = handle({"version": 1, "operation": "fetch", "profile": profile(auth_path=str(p))})
            self.assertEqual(r["status"], "login_required"); http.assert_not_called()
            self.assertIn("no refresh token", r["error"])
            self.assertEqual(p.read_bytes(), before)
            self.assertNotIn(CANARY, json.dumps(r))

    def test_claude_independent_credentials_and_failure(self):
        with tempfile.TemporaryDirectory() as d:
            paths = [Path(d) / "base auth.json", Path(d) / "up auth.json"]
            paths[0].write_text(json.dumps({"claudeAiOauth": {"accessToken": "base-token"}}))
            paths[1].write_text(json.dumps({"claudeAiOauth": {"accessToken": "other-token"}}))
            before = [p.read_bytes() for p in paths]
            seen = []
            def http(url, headers):
                seen.append(headers["Authorization"])
                if seen[-1] == "Bearer base-token":
                    raise UsageError("login_required", "Sign in.")
                return {"seven_day": {"utilization": 40}}
            with patch.object(subscriptions, "http_json", http):
                a, b = [handle({"version": 1, "operation": "fetch", "profile": profile(auth_path=str(p))}) for p in paths]
            self.assertEqual((a["status"], b["status"]), ("login_required", "ready"))
            self.assertNotEqual(a["source_id"], b["source_id"])
            self.assertEqual(seen, ["Bearer base-token", "Bearer other-token"])
            self.assertEqual(before, [p.read_bytes() for p in paths])

    def test_canonical_alias_dedup(self):
        with tempfile.TemporaryDirectory() as d:
            auth = Path(d) / "canonical auth.json"; auth.write_text("{}")
            link = Path(d) / "generated auth.json"; link.symlink_to(auth)
            self.assertEqual(source_id(profile("codex", auth_path=str(auth))), source_id(profile("codex", auth_path=str(link))))

    def test_codex_reported_duration_credits_unknown_fields(self):
        r = subscriptions.parse_codex(profile("codex"), {"plan_type": "pro", "email": CANARY,
            "rate_limit": {"primary_window": {"used_percent": 35, "limit_window_seconds": 604800, "reset_at": 1900000000}},
            "credits": {"balance": "2.5"}})
        self.assertEqual(r["measurements"][0]["window_seconds"], 604800)
        self.assertEqual(r["measurements"][1]["value"], 2.5)
        self.assertNotIn(CANARY, json.dumps(r))

    def test_invalid_percentage_and_empty_schema(self):
        for bad in [-1, 101, True, "20", float("nan")]:
            with self.assertRaises(UsageError):
                subscriptions.parse_codex(profile("codex"), {"rate_limit": {"primary_window": {"used_percent": bad}}})
        with self.assertRaises(UsageError): subscriptions.parse_claude(profile(), {})

    def test_raw_response_keys_never_exported(self):
        r = subscriptions.parse_claude(profile(), {CANARY: {"utilization": 12}})
        self.assertNotIn(CANARY, json.dumps(r))

    def test_unknown_provider_never_reads_auth(self):
        with patch.object(subscriptions, "read_json", side_effect=AssertionError("must not read auth")):
            r = handle({"version": 1, "operation": "fetch", "profile": {"id": "test", "provider": "other", "auth_path": "/home/test/auth.json"}})
        self.assertEqual(r["status"], "setup_required")
        self.assertEqual(r["measurements"], [])


class DiscoveryTests(unittest.TestCase):
    def discover_in(self, home, **env):
        clean = {k: v for k, v in os.environ.items() if k not in {"CLAUDE_CONFIG_DIR", "CODEX_HOME"}}
        with patch.dict(os.environ, {**clean, **env}, clear=True), patch.object(Path, "home", return_value=Path(home)):
            return discover()

    def write(self, path):
        path.parent.mkdir(parents=True, exist_ok=True); path.write_text(json.dumps({"token": CANARY}))

    def test_defaults_present_without_credentials(self):
        with tempfile.TemporaryDirectory() as d:
            r = self.discover_in(d)
            self.assertEqual(r["version"], 1)
            self.assertEqual(r["capabilities"], {"python": True})
            self.assertEqual(r["profiles"], [
                {"id": "claude", "display_name": "Claude", "command": "claude", "provider": "claude", "home": d,
                 "auth_path": str(Path(d) / ".claude/.credentials.json"), "enabled": True, "refresh_seconds": 300},
                {"id": "codex", "display_name": "Codex", "command": "codex", "provider": "codex", "home": d,
                 "auth_path": str(Path(d) / ".codex/auth.json"), "enabled": True, "refresh_seconds": 300}])
            for p in r["profiles"]: validate_profile(p)

    def test_extra_accounts_detected_sorted_and_invalid_ignored(self):
        with tempfile.TemporaryDirectory() as d:
            home = Path(d)
            for path in [home / ".claude-work/.credentials.json", home / ".claude-alpha/.credentials.json",
                         home / ".codex-team/auth.json", home / ".claude-bad name/.credentials.json",
                         home / (".claude-" + "x" * 41) / ".credentials.json", home / ".codex-wrong/.credentials.json"]:
                self.write(path)
            (home / ".claude-empty").mkdir()
            r = self.discover_in(d)
            self.assertEqual([p["id"] for p in r["profiles"]], ["claude", "codex", "claude-alpha", "claude-work", "codex-team"])
            work, team = r["profiles"][3], r["profiles"][4]
            self.assertEqual((work["display_name"], work["command"], work["provider"], work["auth_path"]),
                             ("Claude (work)", "claude-work", "claude", str(home / ".claude-work/.credentials.json")))
            self.assertEqual((team["display_name"], team["command"], team["provider"], team["auth_path"]),
                             ("Codex (team)", "codex-team", "codex", str(home / ".codex-team/auth.json")))
            self.assertNotIn(CANARY, json.dumps(r))

    def test_env_overrides_and_no_duplicates(self):
        with tempfile.TemporaryDirectory() as d:
            home = Path(d)
            self.write(home / ".claude-work/.credentials.json")
            r = self.discover_in(d, CLAUDE_CONFIG_DIR=str(home / ".claude-work"), CODEX_HOME=str(home / "codex-home"))
            self.assertEqual([(p["id"], p["auth_path"]) for p in r["profiles"]],
                             [("claude", str(home / ".claude-work/.credentials.json")), ("codex", str(home / "codex-home/auth.json"))])

    def test_profile_limit(self):
        with tempfile.TemporaryDirectory() as d:
            for n in range(60): self.write(Path(d) / f".claude-a{n:02d}/.credentials.json")
            self.assertEqual(len(self.discover_in(d)["profiles"]), 50)


class BoundaryTests(unittest.TestCase):
    def test_protocol_and_secret_errors(self):
        for request in [{}, [], {"version": 2}, {"version": 1, "operation": "exec", "profile": profile()}]:
            self.assertEqual(handle(request)["status"], "unsupported_response")
        with patch("monitor.claude", side_effect=RuntimeError(CANARY)):
            r = handle({"version": 1, "operation": "fetch", "profile": profile()})
        self.assertNotIn(CANARY, json.dumps(r))
        self.assertEqual(r["status"], "unsupported_response")

    def test_command_and_filter_injection_rejected(self):
        for kwargs in [dict(id="a;touch /tmp/pwn"), dict(id=""), dict(provider="claude --x"), dict(auth_path="relative/auth.json"), dict(home="~/x"), dict(auth_path="/a\n/b")]:
            with self.assertRaises(UsageError): profile(**kwargs)
        # Paths containing spaces and shell metacharacters are data, never shell commands.
        p = profile(auth_path="/home/test/a file $(echo nope).json")
        self.assertIn("$(echo nope)", p["auth_path"])

    def test_unknown_network_destination_rejected(self):
        with self.assertRaises(UsageError): http_json("https://evil.example/", {"Authorization": CANARY})

    def test_oversized_stdin_no_secret_output(self):
        output = subprocess.run([sys.executable, "helper/monitor.py"], input=(CANARY * 5000).encode(), capture_output=True, timeout=5)
        self.assertEqual(output.returncode, 0)
        self.assertEqual(json.loads(output.stdout)["status"], "unsupported_response")
        self.assertNotIn(CANARY.encode(), output.stdout + output.stderr)

    def test_http_rate_limit_retry_after_and_auth_state(self):
        for code, status in [(429, "rate_limited"), (401, "login_required"), (503, "network_error")]:
            error = urllib.error.HTTPError("https://api.anthropic.com/api/oauth/usage", code, CANARY,
                                           {"Retry-After": "123"}, io.BytesIO(CANARY.encode()))
            with patch("urllib.request.OpenerDirector.open", side_effect=error):
                with self.assertRaises(UsageError) as caught:
                    http_json("https://api.anthropic.com/api/oauth/usage", {})
            self.assertEqual(caught.exception.status, status)
            self.assertNotIn(CANARY, str(caught.exception))
            if code == 429: self.assertEqual(caught.exception.retry_after, 123)

    def test_http_expired_token_and_usage_permission_are_distinct(self):
        for code, kind, message, status in [
            (401, "authentication_error", "OAuth token has expired " + CANARY, "token_expired"),
            (403, "permission_error", CANARY, "permission_denied")]:
            body = json.dumps({"error": {"type": kind, "message": message}}).encode()
            error = urllib.error.HTTPError("https://api.anthropic.com/api/oauth/usage", code, "failure", {}, io.BytesIO(body))
            with patch("urllib.request.OpenerDirector.open", side_effect=error):
                with self.assertRaises(UsageError) as caught: http_json("https://api.anthropic.com/api/oauth/usage", {})
            self.assertEqual(caught.exception.status, status)
            self.assertNotIn(CANARY, str(caught.exception))

    def test_credential_revision_changes_without_exporting_tokens(self):
        with tempfile.TemporaryDirectory() as d:
            p = Path(d) / "auth.json"; p.write_text(json.dumps({"token": CANARY}))
            request = {"version": 1, "operation": "validate", "profile": profile(auth_path=str(p))}
            before = handle(request)
            p.write_text(json.dumps({"token": CANARY + "-changed"}))
            after = handle(request)
            self.assertNotEqual(before["source_revision"], after["source_revision"])
            self.assertEqual(before["source_id"], after["source_id"])
            self.assertNotIn(CANARY, json.dumps([before, after]))

    def test_adapter_upgrade_retries_previously_expired_credentials(self):
        with tempfile.TemporaryDirectory() as d:
            p = Path(d) / "auth.json"; p.write_text(json.dumps({"token": CANARY}))
            request = {"version": 1, "operation": "validate", "profile": profile(auth_path=str(p))}
            with patch("code_usage_monitor.contracts.VERSION", "0.1.0"):
                before = handle(request)
            after = handle(request)
            self.assertNotEqual(before["source_revision"], after["source_revision"])
            self.assertEqual(before["source_id"], after["source_id"])


if __name__ == "__main__":
    unittest.main()

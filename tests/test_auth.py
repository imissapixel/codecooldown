import concurrent.futures
import io
import json
import os
from pathlib import Path
import sys
import tempfile
import threading
import time
import unittest
import urllib.error
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "helper"))
from monitor import handle
from code_usage_monitor import auth, subscriptions
from code_usage_monitor.contracts import MAX_BYTES, UsageError
from code_usage_monitor.io import CLAUDE_CLIENT_ID, CLAUDE_RENEWAL_ENDPOINT, refresh_claude_token

OLD = "secret-old-access-canary"
REFRESH = "secret-refresh-canary"
NEW = "secret-new-access-canary"
ROTATED = "secret-rotated-refresh-canary"


class RenewalTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.directory = Path(self.temp.name) / "account"
        self.directory.mkdir()
        self.path = self.directory / ".credentials.json"
        self.initial = {"unrelated": {"keep": True}, "claudeAiOauth": {
            "accessToken": OLD, "refreshToken": REFRESH, "expiresAt": 1,
            "scopes": ["user:profile", "user:inference"], "subscriptionType": "max", "rateLimitTier": "tier"}}
        self.path.write_text(json.dumps(self.initial))
        self.request = {"version": 1, "operation": "fetch", "profile": {
            "id": "test", "provider": "claude", "home": self.temp.name, "auth_path": str(self.path)}}

    def reply(self, **overrides):
        return {"access_token": NEW, "refresh_token": ROTATED, "expires_in": 28800, **overrides}

    def check_no_secrets(self, result):
        for token in [OLD, REFRESH, NEW, ROTATED]:
            self.assertNotIn(token, json.dumps(result))

    def check_clean(self):
        self.assertEqual(sorted(p.name for p in self.directory.iterdir()), [".credentials.json"])
        self.assertFalse(self.directory.with_name("account.lock").exists())

    def test_expired_token_renews_and_fetches_with_private_atomic_persistence(self):
        def refresh(token, client, scopes):
            self.assertEqual((token, client, scopes), (REFRESH, CLAUDE_CLIENT_ID, self.initial["claudeAiOauth"]["scopes"]))
            self.assertTrue((self.directory / ".oauth_refresh.lock").is_dir())
            self.assertTrue(self.directory.with_name("account.lock").is_dir())
            return self.reply(scope="user:profile user:inference", refresh_token_expires_in=86400)
        with patch.object(auth, "refresh_claude_token", side_effect=refresh) as renew, \
             patch.object(subscriptions, "http_json", return_value={"seven_day": {"utilization": 40}}) as usage:
            result = handle(self.request)
        self.assertEqual(result["status"], "ready")
        validated = handle({**self.request, "operation": "validate"})
        self.assertEqual(result["source_revision"], validated["source_revision"])
        renew.assert_called_once()
        self.assertEqual(usage.call_args.args[1]["Authorization"], "Bearer " + NEW)
        saved = json.loads(self.path.read_text())
        self.assertEqual(saved["unrelated"], self.initial["unrelated"])
        self.assertEqual(saved["claudeAiOauth"]["subscriptionType"], "max")
        self.assertEqual(saved["claudeAiOauth"]["refreshToken"], ROTATED)
        self.assertGreater(saved["claudeAiOauth"]["expiresAt"], time.time() * 1000 + 28700000)
        self.assertGreater(saved["claudeAiOauth"]["refreshTokenExpiresAt"], time.time() * 1000)
        self.assertEqual(self.path.stat().st_mode & 0o777, 0o600)
        self.check_no_secrets(result)
        self.check_clean()

    def test_valid_token_does_not_renew_or_write_credentials(self):
        self.initial["claudeAiOauth"]["expiresAt"] = int((time.time() + 3600) * 1000)
        self.path.write_text(json.dumps(self.initial))
        before = self.path.read_bytes()
        with patch.object(auth, "refresh_claude_token") as renew, \
             patch.object(subscriptions, "http_json", return_value={"five_hour": {"utilization": 0}}):
            result = handle(self.request)
        self.assertEqual(result["status"], "ready")
        renew.assert_not_called()
        self.assertEqual(self.path.read_bytes(), before)
        self.check_clean()

    def test_early_401_renews_and_retries_usage_once(self):
        self.initial["claudeAiOauth"].pop("expiresAt")
        self.path.write_text(json.dumps(self.initial))
        for status in ["login_required", "token_expired"]:
            with self.subTest(status=status):
                self.path.write_text(json.dumps(self.initial))
                with patch.object(auth, "refresh_claude_token", return_value=self.reply()) as renew, \
                     patch.object(subscriptions, "http_json", side_effect=[UsageError(status, "Rejected"),
                          {"five_hour": {"utilization": 10}}]) as usage:
                    result = handle(self.request)
                self.assertEqual(result["status"], "ready")
                renew.assert_called_once()
                self.assertEqual(usage.call_count, 2)
                self.assertEqual(usage.call_args.args[1]["Authorization"], "Bearer " + NEW)

    def test_no_refresh_loop_when_new_token_is_rejected(self):
        with patch.object(auth, "refresh_claude_token", return_value=self.reply()) as renew, \
             patch.object(subscriptions, "http_json", side_effect=UsageError("login_required", "Rejected")) as usage:
            result = handle(self.request)
        self.assertEqual(result["status"], "login_required")
        self.assertEqual((renew.call_count, usage.call_count), (1, 1))
        self.assertEqual(result["source_revision"], handle({**self.request, "operation": "validate"})["source_revision"])
        self.check_no_secrets(result)

    def test_unwritable_directory_does_not_rotate_a_refresh_token(self):
        before = self.path.read_bytes()
        with patch.object(auth.tempfile, "NamedTemporaryFile", side_effect=PermissionError(REFRESH)), \
             patch.object(auth, "refresh_claude_token") as renew:
            result = handle(self.request)
        self.assertEqual(result["status"], "permission_denied")
        renew.assert_not_called()
        self.assertEqual(self.path.read_bytes(), before)
        self.check_no_secrets(result)
        self.check_clean()

    def test_lost_lock_does_not_overwrite_credentials_or_remove_foreign_lock(self):
        foreign = self.directory / ".oauth_refresh.lock"
        moved = self.directory / "old-lock"
        before = self.path.read_bytes()
        def refresh(*args):
            foreign.rename(moved)
            foreign.mkdir()
            return self.reply()
        with patch.object(auth, "refresh_claude_token", side_effect=refresh):
            result = handle(self.request)
        self.assertEqual(result["status"], "network_error")
        self.assertEqual(self.path.read_bytes(), before)
        self.assertTrue(foreign.is_dir())
        foreign.rmdir(); moved.rmdir()
        self.check_clean()

    def test_usage_permission_rate_and_network_failures_do_not_rotate_valid_tokens(self):
        self.initial["claudeAiOauth"]["expiresAt"] = int((time.time() + 3600) * 1000)
        self.path.write_text(json.dumps(self.initial))
        for status in ["permission_denied", "rate_limited", "network_error", "unsupported_response"]:
            with self.subTest(status=status), patch.object(auth, "refresh_claude_token") as renew, \
                 patch.object(subscriptions, "http_json", side_effect=UsageError(status, "Failed")):
                self.assertEqual(handle(self.request)["status"], status)
                renew.assert_not_called()

    def test_failed_or_malformed_renewal_preserves_original_and_hides_secrets(self):
        failures = [UsageError("login_required", "Sign in again"), UsageError("network_error", "Try later"),
                    UsageError("rate_limited", "Wait", 123)]
        malformed = [self.reply(access_token=""), self.reply(refresh_token=None), self.reply(expires_in=0),
                     self.reply(expires_in=True), self.reply(expires_in=float("inf")), self.reply(scope=[]),
                     self.reply(refresh_token_expires_in=-1)]
        before = self.path.read_bytes()
        for value in failures + malformed:
            with self.subTest(value_type=type(value).__name__):
                args = {"side_effect": value} if isinstance(value, Exception) else {"return_value": value}
                with patch.object(auth, "refresh_claude_token", **args), patch.object(subscriptions, "http_json") as usage:
                    result = handle(self.request)
                self.assertEqual(result["status"], value.status if isinstance(value, UsageError) else "unsupported_response")
                if isinstance(value, UsageError):
                    self.assertEqual(result["retry_after_seconds"], value.retry_after)
                self.assertEqual(self.path.read_bytes(), before)
                usage.assert_not_called()
                self.check_no_secrets(result)
                self.check_clean()

    def test_missing_rotated_refresh_token_preserves_existing_one(self):
        response = self.reply(); response.pop("refresh_token")
        with patch.object(auth, "refresh_claude_token", return_value=response):
            result = auth.renew_claude(str(self.path), OLD)
        self.assertEqual(result["refreshToken"], REFRESH)

    def test_custom_client_and_missing_scopes_are_supported(self):
        self.initial["claudeAiOauth"]["clientId"] = "another-public-client"
        self.initial["claudeAiOauth"].pop("scopes")
        self.path.write_text(json.dumps(self.initial))
        with patch.object(auth, "refresh_claude_token", return_value=self.reply()) as renew:
            result = auth.renew_claude(str(self.path), OLD)
        renew.assert_called_once_with(REFRESH, "another-public-client", None)
        self.assertEqual(result["clientId"], "another-public-client")
        self.check_clean()

    def test_renewal_only_updates_the_selected_account_with_a_nondefault_home(self):
        other = self.directory.parent / "another account $(literal)"
        other.mkdir()
        other_path = other / "credentials.json"
        other_path.write_text(json.dumps({"claudeAiOauth": {"accessToken": "other-access", "refreshToken": "other-refresh", "expiresAt": 1}}))
        original = self.path.read_bytes()
        with patch.object(auth, "refresh_claude_token", return_value=self.reply()) as renew:
            result = auth.renew_claude(str(other_path), "other-access")
        renew.assert_called_once_with("other-refresh", CLAUDE_CLIENT_ID, None)
        self.assertEqual(result["accessToken"], NEW)
        self.assertEqual(self.path.read_bytes(), original)
        self.assertFalse(other.with_name(other.name + ".lock").exists())
        self.assertFalse((other / ".oauth_refresh.lock").exists())

    def test_symlink_and_concurrent_helpers_rotate_only_once(self):
        link = self.directory / "alias.json"; link.symlink_to(self.path)
        started = threading.Event()
        release = threading.Event()
        def refresh(*args):
            started.set()
            self.assertTrue(release.wait(3))
            return self.reply()
        with patch.object(auth, "refresh_claude_token", side_effect=refresh) as renew, \
             concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:
            first = pool.submit(auth.renew_claude, str(self.path), OLD)
            self.assertTrue(started.wait(3))
            second = pool.submit(auth.renew_claude, str(link), OLD)
            release.set()
            self.assertEqual(first.result(timeout=5)["accessToken"], NEW)
            self.assertEqual(second.result(timeout=5)["accessToken"], NEW)
        renew.assert_called_once()
        self.assertTrue(link.is_symlink())

    def test_external_cli_renewal_is_reused_without_rotation(self):
        current = {**self.initial["claudeAiOauth"], "accessToken": NEW, "expiresAt": int((time.time() + 3600) * 1000)}
        self.path.write_text(json.dumps({**self.initial, "claudeAiOauth": current}))
        with patch.object(auth, "refresh_claude_token") as renew:
            result = auth.renew_claude(str(self.path), OLD)
        self.assertEqual(result, current)
        renew.assert_not_called()

    def test_external_login_during_renewal_is_not_overwritten(self):
        def refresh(*args):
            self.initial["claudeAiOauth"]["accessToken"] = "external-login"
            self.initial["claudeAiOauth"]["expiresAt"] = int((time.time() + 3600) * 1000)
            self.path.write_text(json.dumps(self.initial))
            return self.reply()
        with patch.object(auth, "refresh_claude_token", side_effect=refresh):
            result = auth.renew_claude(str(self.path), OLD)
        self.assertEqual(result["accessToken"], "external-login")
        self.assertEqual(json.loads(self.path.read_text()), self.initial)
        self.check_clean()

    def test_atomic_replace_failure_preserves_original(self):
        before = self.path.read_bytes()
        with patch.object(auth, "refresh_claude_token", return_value=self.reply()), \
             patch.object(auth.os, "replace", side_effect=PermissionError(NEW)):
            result = handle(self.request)
        self.assertEqual(result["status"], "permission_denied")
        self.assertEqual(self.path.read_bytes(), before)
        self.check_no_secrets(result)
        self.check_clean()

    def test_live_cli_lock_is_respected_and_wait_is_bounded(self):
        for path in [self.directory / ".oauth_refresh.lock", self.directory.with_name("account.lock")]:
            with self.subTest(lock=path.name):
                path.mkdir()
                try:
                    with patch.object(auth, "LOCK_WAIT_SECONDS", 0.02), patch.object(auth, "refresh_claude_token") as renew:
                        result = handle(self.request)
                    self.assertEqual(result["status"], "network_error")
                    self.assertTrue(path.is_dir())
                    renew.assert_not_called()
                finally:
                    path.rmdir()
        self.check_clean()

    def test_stale_cli_lock_can_be_recovered(self):
        path = self.directory / ".oauth_refresh.lock"; path.mkdir()
        os.utime(path, (time.time() - 70, time.time() - 70))
        with patch.object(auth, "refresh_claude_token", return_value=self.reply()):
            self.assertEqual(auth.renew_claude(str(self.path), OLD)["accessToken"], NEW)
        self.check_clean()


class RenewalHttpTests(unittest.TestCase):
    def test_fixed_post_endpoint_client_and_scopes(self):
        response = {"access_token": NEW, "expires_in": 28800}
        with patch("urllib.request.OpenerDirector.open", return_value=io.BytesIO(json.dumps(response).encode())) as opened:
            self.assertEqual(refresh_claude_token(REFRESH, CLAUDE_CLIENT_ID, ["user:profile"]), response)
        request = opened.call_args.args[0]
        self.assertEqual((request.full_url, request.method), (CLAUDE_RENEWAL_ENDPOINT, "POST"))
        self.assertEqual(json.loads(request.data), {"grant_type": "refresh_token", "refresh_token": REFRESH,
                                                  "client_id": CLAUDE_CLIENT_ID, "scope": "user:profile"})

    def test_refresh_errors_are_sanitized_and_rate_limits_survive(self):
        for code, body, status in [(400, {"error": "invalid_grant", "error_description": REFRESH}, "login_required"),
                                   (400, {"error": "invalid_scope"}, "unsupported_response"),
                                   (401, {}, "login_required"), (403, {}, "login_required"),
                                   (429, {}, "rate_limited"), (503, {}, "network_error"),
                                   (302, {}, "unsupported_response")]:
            error = urllib.error.HTTPError(CLAUDE_RENEWAL_ENDPOINT, code, REFRESH, {"Retry-After": "123"},
                                           io.BytesIO(json.dumps(body).encode()))
            with self.subTest(code=code), patch("urllib.request.OpenerDirector.open", side_effect=error):
                with self.assertRaises(UsageError) as caught:
                    refresh_claude_token(REFRESH, CLAUDE_CLIENT_ID, None)
            self.assertEqual(caught.exception.status, status)
            self.assertNotIn(REFRESH, str(caught.exception))
            if code == 429:
                self.assertEqual(caught.exception.retry_after, 123)

    def test_refresh_response_is_bounded(self):
        with patch("urllib.request.OpenerDirector.open", return_value=io.BytesIO(b"x" * (MAX_BYTES + 1))):
            with self.assertRaises(UsageError) as caught:
                refresh_claude_token(REFRESH, CLAUDE_CLIENT_ID, None)
        self.assertEqual(caught.exception.status, "unsupported_response")

    def test_real_redirect_never_forwards_refresh_token(self):
        from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
        class Redirect(BaseHTTPRequestHandler):
            reached = []
            def do_POST(self):
                self.reached.append(self.path)
                self.send_response(302)
                self.send_header("Location", "/stolen")
                self.end_headers()
            def do_GET(self):
                self.reached.append(self.path)
                self.send_response(200)
                self.end_headers()
            def log_message(self, *args):
                pass
        server = ThreadingHTTPServer(("127.0.0.1", 0), Redirect)
        thread = threading.Thread(target=server.serve_forever, daemon=True); thread.start()
        try:
            with patch("code_usage_monitor.io.CLAUDE_RENEWAL_ENDPOINT", f"http://127.0.0.1:{server.server_port}/token"):
                with self.assertRaises(UsageError):
                    refresh_claude_token(REFRESH, CLAUDE_CLIENT_ID, None)
            self.assertEqual(Redirect.reached, ["/token"])
        finally:
            server.shutdown(); server.server_close(); thread.join()

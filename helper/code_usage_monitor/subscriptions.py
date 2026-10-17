from __future__ import annotations

import time

from .contracts import UsageError, measurement, number, result, text, timestamp
from .io import http_json, read_json


def parse_claude(p: dict, data: dict) -> dict:
    scope = "OAuth account at configured Claude credential source"
    values = []
    labels = {"five_hour": "5-hour allowance", "seven_day": "7-day allowance",
              "seven_day_opus": "7-day Opus allowance", "seven_day_sonnet": "7-day Sonnet allowance"}
    for key, bucket in data.items():
        if key == "extra_usage":
            continue
        if not isinstance(bucket, dict) or "utilization" not in bucket:
            continue
        used = number(bucket.get("utilization"))
        if used is None or used > 100:
            raise UsageError("unsupported_response", "Claude returned an invalid allowance percentage.")
        label = labels.get(key, "Additional reported allowance")
        duration = {"five_hour": 18000, "seven_day": 604800}.get(key)
        values.append(measurement("allowance_used", label, used, "percent", scope,
                                  "Anthropic OAuth usage", reset=bucket.get("resets_at"), duration=duration))
    # Only the documented extra_usage shape is understood. Unknown spending shapes stay unavailable.
    extra = data.get("extra_usage")
    if isinstance(extra, dict) and extra.get("is_enabled") is True:
        if number(extra.get("utilization")) is not None:
            values.append(measurement("extra_allowance_used", "Extra usage allowance", extra["utilization"],
                                      "percent", scope, "Anthropic OAuth usage"))
    if not values:
        raise UsageError("unsupported_response", "Claude returned no recognized allowance windows.")
    return result(p, values, notices=["Shared local history is not used to estimate either Claude account."])


def claude(p: dict) -> dict:
    auth = read_json(p["auth_path"])
    oauth = auth.get("claudeAiOauth", {})
    token = oauth.get("accessToken") if isinstance(oauth, dict) else None
    if not isinstance(token, str) or not token.strip():
        raise UsageError("login_required", "No OAuth token at this Claude source. Sign in using this profile's CLI.")
    expires_at = number(oauth.get("expiresAt"))
    if expires_at is not None and expires_at <= time.time() * 1000:
        raise UsageError("token_expired", "The saved access token expired; your Claude CLI login may still be valid. Use that CLI to renew it, then refresh usage.")
    return parse_claude(p, http_json("https://api.anthropic.com/api/oauth/usage", {
        "Authorization": "Bearer " + token, "anthropic-beta": "oauth-2025-04-20",
        "User-Agent": "codecooldown/0.1.0", "Accept": "application/json"}))


def parse_codex(p: dict, data: dict) -> dict:
    scope = "Authenticated ChatGPT account"
    values = []
    groups = [("rate_limit", ""), ("code_review_rate_limit", "Code review ")]
    for group, prefix in groups:
        limits = data.get(group)
        if not isinstance(limits, dict):
            continue
        for name in ("primary_window", "secondary_window"):
            window = limits.get(name)
            if not isinstance(window, dict):
                continue
            used = number(window.get("used_percent"))
            if used is None or used > 100:
                raise UsageError("unsupported_response", "Codex returned an invalid allowance percentage.")
            seconds = number(window.get("limit_window_seconds"))
            label = prefix + (f"{seconds / 3600:g}-hour allowance" if seconds else name.replace("_", " "))
            values.append(measurement("allowance_used", label, used, "percent", scope, "ChatGPT wham usage",
                                      reset=window.get("reset_at"), duration=seconds))
    credits = data.get("credits")
    if isinstance(credits, dict):
        balance = credits.get("balance")
        if isinstance(balance, str):
            try:
                balance = float(balance)
            except ValueError:
                balance = None
        if number(balance) is not None:
            values.append(measurement("credits", "Credits balance", balance, "credits", scope, "ChatGPT wham usage"))
    if not values:
        raise UsageError("unsupported_response", "Codex returned no recognized usage windows or credit balance.")
    plan = data.get("plan_type")
    known_plans = {"free", "plus", "pro", "team", "business", "enterprise", "edu", "go"}
    return result(p, values, plan=plan if plan in known_plans else None)


def codex(p: dict) -> dict:
    auth = read_json(p["auth_path"])
    tokens = auth.get("tokens", {})
    token = tokens.get("access_token") if isinstance(tokens, dict) else None
    if not isinstance(token, str) or not token.strip():
        raise UsageError("login_required", "No ChatGPT OAuth token at this Codex source. Sign in through Codex, then refresh.")
    headers = {"Authorization": "Bearer " + token, "User-Agent": "codecooldown/0.1.0", "Accept": "application/json"}
    account = tokens.get("account_id")
    if isinstance(account, str) and account and len(account) <= 256 and not any(ord(c) < 32 for c in account):
        headers["ChatGPT-Account-Id"] = account
    return parse_codex(p, http_json("https://chatgpt.com/backend-api/wham/usage", headers))

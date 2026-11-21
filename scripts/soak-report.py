#!/usr/bin/env python3
"""Audit a passive soak run recorded by scripts/soak-sample.sh (or any Natter /health jsonl).

Answers the questions a 24h soak acceptance actually needs:

  * did the sampler keep up (coverage, gaps, unreachable windows)?
  * did the host restart under us (startedAt changes)?
  * was it healthy the whole time (status / plugins / Tier 1 providers)?
  * did Discord/DingTalk stay connected, and how many reconnects happened?
  * are disconnects attributed (OBS-019) or silent? Attribution reads lastDisconnectError /
    lastDisconnectCode first; only payloads without those fields fall back to the legacy
    "lastDisconnectAt set + lastError null" heuristic, so a fixed host is never over-counted.
  * did anything fail to send (sendFailures / lastSendError)?

Usage:
  python3 scripts/soak-report.py artifacts/ops/.../soak-2026-11-17.jsonl
  python3 scripts/soak-report.py soak/*.jsonl --interval 300 --min-coverage 95
  python3 scripts/soak-report.py soak.jsonl --json > soak-report.json
  python3 scripts/soak-report.py --self-test          # synthetic fixtures, no input files

Exit codes: 0 = PASS, 1 = FAIL, 2 = usage/unreadable input.
"""

from __future__ import annotations

import argparse
import datetime as dt
import json
import sys
from dataclasses import dataclass, field
from pathlib import Path

ISO = "%Y-%m-%dT%H:%M:%S"


def parse_ts(value: str | None) -> dt.datetime | None:
    if not value:
        return None
    text = value.strip()
    # Normalise .NET 7-digit fractional seconds and 'Z'.
    if text.endswith("Z"):
        text = text[:-1] + "+00:00"
    # Trim nanoseconds to microseconds for fromisoformat.
    if "." in text:
        head, _, tail = text.partition(".")
        frac = ""
        for ch in tail:
            if ch.isdigit():
                frac += ch
            else:
                break
        rest = tail[len(frac):]
        text = f"{head}.{(frac + '000000')[:6]}{rest}"
    try:
        parsed = dt.datetime.fromisoformat(text)
    except ValueError:
        return None
    if parsed.tzinfo is None:
        parsed = parsed.replace(tzinfo=dt.timezone.utc)
    return parsed


@dataclass
class Sample:
    at: dt.datetime
    raw: dict
    source: str = "unknown"


@dataclass
class SoakAudit:
    files: list[str] = field(default_factory=list)
    headers: list[dict] = field(default_factory=list)
    stops: list[dict] = field(default_factory=list)
    samples: list[Sample] = field(default_factory=list)
    errors: list[dict] = field(default_factory=list)
    unparsable_lines: int = 0


def load(paths: list[Path]) -> SoakAudit:
    audit = SoakAudit()
    for path in paths:
        audit.files.append(str(path))
        for line in path.read_text(encoding="utf-8", errors="replace").splitlines():
            line = line.strip()
            if not line:
                continue
            try:
                row = json.loads(line)
            except json.JSONDecodeError:
                audit.unparsable_lines += 1
                continue
            if "soakStart" in row or "soak_start" in row:
                audit.headers.append(row)
                continue
            if "soakStop" in row:
                audit.stops.append(row)
                continue
            at = parse_ts(row.get("sampledAt"))
            if at is None:
                at = parse_ts(row.get("sampledWallClock"))
            if at is None:
                audit.unparsable_lines += 1
                continue
            if row.get("error"):
                audit.errors.append(row)
                continue
            audit.samples.append(Sample(at=at, raw=row, source=row.get("source", "unknown")))
    audit.samples.sort(key=lambda s: s.at)
    return audit


def upstream_view(sample: Sample, prefix: str) -> dict | None:
    for upstream in sample.raw.get("upstreams", []) or []:
        if str(upstream.get("source", "")).startswith(prefix):
            return upstream
    return None


def summarise(audit: SoakAudit, interval: int, max_gap: int) -> dict:
    samples = audit.samples
    if not samples:
        return {
            "files": audit.files,
            "samples": 0,
            "errors": len(audit.errors),
            "unparsableLines": audit.unparsable_lines,
            "result": "FAIL",
            "reasons": ["no usable /health samples in the input"],
        }

    first, last = samples[0].at, samples[-1].at
    window = (last - first).total_seconds()
    expected = int(window // interval) + 1 if interval > 0 else len(samples)
    coverage = round(100.0 * len(samples) / expected, 1) if expected else 100.0

    gaps: list[dict] = []
    for prev, cur in zip(samples, samples[1:]):
        delta = (cur.at - prev.at).total_seconds()
        if delta > interval * 1.5:
            gaps.append(
                {
                    "from": prev.at.isoformat(),
                    "to": cur.at.isoformat(),
                    "seconds": int(delta),
                    "missingSamples": max(0, int(round(delta / interval)) - 1),
                    "exceedsTolerance": delta > max_gap,
                }
            )

    restarts: list[dict] = []
    uptime_regressions = 0
    prev_started = None
    prev_uptime = None
    for sample in samples:
        started = sample.raw.get("startedAt")
        uptime = sample.raw.get("uptimeSeconds")
        if started and prev_started and started != prev_started:
            restarts.append({"at": sample.at.isoformat(), "newStartedAt": started, "previousStartedAt": prev_started})
        if started:
            prev_started = started
        if isinstance(uptime, int) and isinstance(prev_uptime, int) and uptime < prev_uptime:
            uptime_regressions += 1
        if isinstance(uptime, int):
            prev_uptime = uptime

    status_counts: dict[str, int] = {}
    for sample in samples:
        key = str(sample.raw.get("status", "unknown"))
        status_counts[key] = status_counts.get(key, 0) + 1

    plugin_failures = [s.raw.get("plugins", {}) for s in samples if (s.raw.get("plugins", {}) or {}).get("failed", 0) > 0]
    tier1_unavailable = [
        s.at.isoformat()
        for s in samples
        if any(p.get("supportTier") == 1 and p.get("available") is not True for p in s.raw.get("providers", []) or [])
    ]

    upstreams: dict[str, dict] = {}
    for prefix in ("discord:", "dingtalk:"):
        entries = [(s, upstream_view(s, prefix)) for s in samples]
        entries = [(s, u) for s, u in entries if u]
        if not entries:
            continue
        reconnects = [int(u.get("reconnectCount") or 0) for _, u in entries]
        # A counter can reset to 0 when the host restarts; count the events, not just the delta,
        # otherwise a restart makes the total look negative.
        reconnect_events = 0
        for prev_count, cur_count in zip(reconnects, reconnects[1:]):
            if cur_count >= prev_count:
                reconnect_events += cur_count - prev_count
            else:
                reconnect_events += cur_count
        sends = [int(u.get("sendFailures") or 0) for _, u in entries]
        errors = sorted({str(u.get("lastError")) for _, u in entries if u.get("lastError")})

        # Disconnect attribution is payload-version aware (OBS-019 fix):
        #   * payload has lastDisconnectError -> authoritative. Empty means "really no reason".
        #   * payload lacks the field (older host / older capture) -> legacy: infer from lastError.
        # `lastError` alone must never label a disconnect "silent" on a fixed host, because it is
        # cleared on Ready by design.
        # `lastDisconnectAt` is sticky: every later sample repeats the same timestamp. Counting those
        # samples would multiply one disconnect by the number of samples, so group by disconnect
        # event (timestamp) and attribute each event once.
        payload_new = 0
        payload_legacy = 0
        events: dict[str, dict] = {}

        for _, upstream in entries:
            has_reason_field = "lastDisconnectError" in upstream
            if has_reason_field:
                payload_new += 1
            else:
                payload_legacy += 1

            disconnect_at = upstream.get("lastDisconnectAt")
            if not disconnect_at:
                continue
            event = events.setdefault(
                str(disconnect_at),
                {"has_field": has_reason_field, "error": "", "code": "", "last_error": None},
            )
            # Later samples of the same event can only add information, never remove it.
            event["has_field"] = event["has_field"] or has_reason_field
            if not event["error"]:
                event["error"] = str(upstream.get("lastDisconnectError") or "").strip()
            if not event["code"]:
                event["code"] = str(upstream.get("lastDisconnectCode") or "").strip()
            if not event["last_error"]:
                event["last_error"] = upstream.get("lastError")

        silent_new = 0
        silent_legacy = 0
        attributed_new = 0
        attributed_legacy = 0
        reason_counts: dict[tuple[str, str], int] = {}

        for event in events.values():
            if event["has_field"]:
                if event["error"]:
                    attributed_new += 1
                    reason_counts[(event["code"] or "unknown", event["error"])] = (
                        reason_counts.get((event["code"] or "unknown", event["error"]), 0) + 1
                    )
                else:
                    silent_new += 1
            else:
                if event["last_error"]:
                    attributed_legacy += 1
                else:
                    silent_legacy += 1

        disconnect_reasons = [
            {"code": code, "error": error, "samples": count}
            for (code, error), count in sorted(reason_counts.items(), key=lambda item: -item[1])[:5]
        ]

        upstreams[prefix.rstrip(":")] = {
            "samples": len(entries),
            "disconnectedSamples": sum(1 for _, u in entries if u.get("connected") is not True),
            "reconnectCountStart": reconnects[0],
            "reconnectCountEnd": reconnects[-1],
            "reconnectEvents": reconnect_events,
            "reconnects": reconnect_events,
            "sendFailuresMax": max(sends) if sends else 0,
            "lastErrors": errors,
            # Kept for compatibility: total of silent disconnects in both payload versions.
            "samplesWithDisconnectButNoReason": silent_new + silent_legacy,
            "distinctDisconnects": len(events),
            "silentDisconnects": {
                "newPayload": silent_new,
                "legacyPayload": silent_legacy,
            },
            "disconnectsWithReason": {
                "newPayload": attributed_new,
                "legacyPayload": attributed_legacy,
            },
            "payloadVersions": {"new": payload_new, "legacy": payload_legacy},
            "disconnectReasons": disconnect_reasons,
            "lastEventAtFinal": entries[-1][1].get("lastEventAt"),
        }

    conversations = [s.raw.get("conversations") for s in samples if isinstance(s.raw.get("conversations"), int)]

    reasons: list[str] = []
    if coverage < 95.0:
        reasons.append(f"coverage {coverage}% < 95%")
    over = [g for g in gaps if g["exceedsTolerance"]]
    if over:
        reasons.append(
            f"{len(over)} gap(s) exceed the {max_gap}s tolerance "
            f"(largest {max(over, key=lambda g: g['seconds'])['seconds']}s)"
        )
    if restarts:
        reasons.append(f"host restarted {len(restarts)} time(s) during the run")
    if plugin_failures:
        reasons.append(f"{len(plugin_failures)} sample(s) reported failed plugins")
    if tier1_unavailable:
        reasons.append(f"Tier 1 provider unavailable in {len(tier1_unavailable)} sample(s)")
    for name, data in upstreams.items():
        if data["disconnectedSamples"]:
            reasons.append(f"{name} disconnected in {data['disconnectedSamples']} sample(s)")
        if data["sendFailuresMax"]:
            reasons.append(f"{name} sendFailures reached {data['sendFailuresMax']}")
        silent = data["silentDisconnects"]
        if silent["newPayload"]:
            reasons.append(
                f"{name} still has {silent['newPayload']} silent disconnect(s) on the new payload "
                "(lastDisconnectError empty) — the OBS-019 fix should have attributed them"
            )

    result = "PASS" if not reasons else "FAIL"

    return {
        "files": audit.files,
        "soakHeaders": audit.headers,
        "soakStops": audit.stops,
        "samples": len(samples),
        "errorSamples": len(audit.errors),
        "unparsableLines": audit.unparsable_lines,
        "firstSample": first.isoformat(),
        "lastSample": last.isoformat(),
        "windowSeconds": int(window),
        "windowHours": round(window / 3600.0, 2),
        "intervalSeconds": interval,
        "expectedSamples": expected,
        "coveragePercent": coverage,
        "gaps": gaps,
        "restarts": restarts,
        "uptimeRegressions": uptime_regressions,
        "statusCounts": status_counts,
        "upstreams": upstreams,
        "conversationsStart": conversations[0] if conversations else None,
        "conversationsEnd": conversations[-1] if conversations else None,
        "tier1ProviderUnavailableSamples": len(tier1_unavailable),
        "result": result,
        "reasons": reasons,
    }


def print_human(summary: dict) -> None:
    print(f"soak files      : {len(summary.get('files', []))}")
    for name in summary.get("files", []):
        print(f"                  {name}")
    if summary.get("samples", 0) == 0:
        print(f"samples         : 0  (errors={summary.get('errorSamples')}, unparsable={summary.get('unparsableLines')})")
        print(f"result          : {summary['result']}")
        for reason in summary.get("reasons", []):
            print(f"  - {reason}")
        return

    print(f"samples         : {summary['samples']}  (errors={summary['errorSamples']}, unparsable={summary['unparsableLines']})")
    print(f"window          : {summary['firstSample']} -> {summary['lastSample']}  ({summary['windowHours']}h)")
    print(f"interval        : {summary['intervalSeconds']}s   expected {summary['expectedSamples']} samples")
    print(f"coverage        : {summary['coveragePercent']}%")
    print(f"status counts   : {summary['statusCounts']}")
    print(f"restarts        : {len(summary['restarts'])}   uptime regressions: {summary['uptimeRegressions']}")
    if summary["restarts"]:
        for restart in summary["restarts"]:
            print(f"  - restart at {restart['at']} (startedAt {restart['previousStartedAt']} -> {restart['newStartedAt']})")
    if summary["gaps"]:
        print(f"gaps            : {len(summary['gaps'])} (>1.5x interval)")
        for gap in summary["gaps"]:
            flag = "!" if gap["exceedsTolerance"] else " "
            print(f" {flag} {gap['from']} -> {gap['to']}  {gap['seconds']}s  missing~{gap['missingSamples']}")
    for name, data in summary.get("upstreams", {}).items():
        silent = data.get("silentDisconnects", {})
        with_reason = data.get("disconnectsWithReason", {})
        versions = data.get("payloadVersions", {})
        print(
            f"upstream {name:<8}: reconnects={data['reconnects']} (end={data['reconnectCountEnd']}) "
            f"disconnected={data['disconnectedSamples']} sendFailuresMax={data['sendFailuresMax']}"
        )
        print(
            f"                   disconnects: attributed={with_reason.get('newPayload', 0)} "
            f"silent={silent.get('newPayload', 0)} (new payload) | "
            f"legacy: attributed={with_reason.get('legacyPayload', 0)} silent={silent.get('legacyPayload', 0)}"
        )
        print(f"                   payload versions: new={versions.get('new', 0)} legacy={versions.get('legacy', 0)}")
        for reason in data.get("disconnectReasons", []):
            print(f"                   reason[{reason['samples']}]: code={reason['code']} error={reason['error']}")
        if data["lastErrors"]:
            for err in data["lastErrors"]:
                print(f"                   lastError: {err}")
    if summary.get("conversationsStart") is not None:
        print(f"conversations   : {summary['conversationsStart']} -> {summary['conversationsEnd']}")
    print(f"result          : {summary['result']}")
    for reason in summary.get("reasons", []):
        print(f"  - {reason}")


def _fixture_rows(
    *,
    payload: str,
    disconnect: bool,
    reason: str | None,
    last_error: str | None,
    samples: int = 5,
) -> list[dict]:
    """Build one synthetic soak run: `payload` is "new" (reason fields) or "legacy" (no fields)."""
    start = dt.datetime(2026, 1, 1, tzinfo=dt.timezone.utc)
    rows: list[dict] = [
        {
            "soakStart": start.isoformat(),
            "intervalSeconds": 300,
            "durationSeconds": 86400,
            "port": 5099,
        }
    ]
    for index in range(samples):
        at = start + dt.timedelta(seconds=300 * index)
        upstream: dict = {
            "source": "discord:discord:1",
            "connected": True,
            "reconnectCount": 1 if (disconnect and index >= 2) else 0,
            "lastDisconnectAt": (start + dt.timedelta(seconds=600)).isoformat() if disconnect else None,
            "lastError": last_error,
            "sendFailures": 0,
            "lastSendError": None,
        }
        if payload == "new":
            upstream["lastDisconnectError"] = reason
            upstream["lastDisconnectCode"] = "ws_closed" if reason else None
        rows.append(
            {
                "status": "healthy",
                "uptimeSeconds": 300 * index,
                "startedAt": start.isoformat(),
                "sampledAt": at.isoformat(),
                "upstreams": [upstream],
                "providers": [
                    {"id": "natter.feature.ai.deepseek#deepseek", "available": True, "supportTier": 1}
                ],
                "plugins": {"activated": 4, "failed": 0, "failures": []},
                "conversations": 0,
            }
        )
    return rows


def self_test() -> int:
    """Synthetic fixtures for both payload versions; no network, no real host, no secrets."""
    import tempfile

    failures = 0

    def check(name: str, ok: bool, detail: str = "") -> None:
        nonlocal failures
        if ok:
            print(f"  ok   {name}")
        else:
            print(f"  FAIL {name} {detail}")
            failures += 1

    def audit(rows: list[dict]) -> dict:
        with tempfile.NamedTemporaryFile("w", suffix=".jsonl", delete=False) as handle:
            handle.write("\n".join(json.dumps(row) for row in rows) + "\n")
            path = Path(handle.name)
        try:
            return summarise(load([path]), 300, 900)
        finally:
            path.unlink(missing_ok=True)

    print("== soak-report self-test ==")

    # 1. New payload with a reason: attributed, not silent, run is healthy.
    summary = audit(_fixture_rows(payload="new", disconnect=True, reason="gateway closed (1006)", last_error=None))
    discord = summary["upstreams"]["discord"]
    check("new payload with reason is attributed", discord["disconnectsWithReason"]["newPayload"] == 1,
          discord["disconnectsWithReason"])
    check("new payload with reason is not silent", discord["silentDisconnects"]["newPayload"] == 0,
          discord["silentDisconnects"])
    check("attributed disconnect does not fail the run", summary["result"] == "PASS", summary["reasons"])
    check("reason is reported with its code",
          any(r["code"] == "ws_closed" and "1006" in r["error"] for r in discord["disconnectReasons"]),
          discord["disconnectReasons"])

    # 2. New payload, empty reason field: this is a real defect and must fail.
    summary = audit(_fixture_rows(payload="new", disconnect=True, reason=None, last_error=None))
    discord = summary["upstreams"]["discord"]
    check("new payload without reason is silent", discord["silentDisconnects"]["newPayload"] == 1,
          discord["silentDisconnects"])
    check("silent disconnect on new payload fails the run", summary["result"] == "FAIL", summary["result"])
    check("verdict explains the silent disconnect",
          any("silent disconnect" in reason for reason in summary["reasons"]), summary["reasons"])

    # 3. Legacy payload (no reason fields) with a cleared lastError: counted as legacy, not as new.
    summary = audit(_fixture_rows(payload="legacy", disconnect=True, reason=None, last_error=None))
    discord = summary["upstreams"]["discord"]
    check("legacy payload is detected", discord["payloadVersions"]["legacy"] == 5, discord["payloadVersions"])
    check("legacy silent disconnect is counted separately",
          discord["silentDisconnects"] == {"newPayload": 0, "legacyPayload": 1}, discord["silentDisconnects"])
    check("legacy-only silence does not fail the run", summary["result"] == "PASS", summary["reasons"])

    # 4. Legacy payload where lastError still carries the cause: legacy attribution.
    summary = audit(_fixture_rows(payload="legacy", disconnect=True, reason=None, last_error="Server missed last heartbeat"))
    discord = summary["upstreams"]["discord"]
    check("legacy payload attributes via lastError", discord["disconnectsWithReason"]["legacyPayload"] == 1,
          discord["disconnectsWithReason"])
    check("legacy attributed disconnect is not silent", discord["silentDisconnects"]["legacyPayload"] == 0,
          discord["silentDisconnects"])

    # 5. lastError cleared on Ready must not create a silent disconnect on a fixed host.
    summary = audit(_fixture_rows(payload="new", disconnect=False, reason=None, last_error=None))
    discord = summary["upstreams"]["discord"]
    check("no disconnect at all is quiet",
          discord["silentDisconnects"] == {"newPayload": 0, "legacyPayload": 0} and summary["result"] == "PASS",
          discord["silentDisconnects"])

    if failures:
        print(f"self-test FAILED ({failures} case(s))", file=sys.stderr)
        return 1
    print("self-test passed (5 scenarios)")
    return 0


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description="Audit a Natter passive soak jsonl.")
    parser.add_argument("files", nargs="*", type=Path, help="soak jsonl file(s)")
    parser.add_argument("--interval", type=int, default=300, help="expected sampling interval in seconds (default 300)")
    parser.add_argument("--max-gap", type=int, default=900, help="largest tolerated gap in seconds (default 900)")
    parser.add_argument("--min-coverage", type=float, default=95.0, help="minimum coverage percent (default 95)")
    parser.add_argument("--json", action="store_true", help="emit the report as JSON")
    parser.add_argument("--self-test", action="store_true", help="run synthetic fixtures and exit")
    args = parser.parse_args(argv)

    if args.self_test:
        return self_test()
    if not args.files:
        parser.error("at least one soak jsonl file is required (or use --self-test)")

    missing = [str(path) for path in args.files if not path.is_file()]
    if missing:
        print(f"missing input: {', '.join(missing)}", file=sys.stderr)
        return 2

    audit = load(args.files)
    summary = summarise(audit, args.interval, args.max_gap)
    # Coverage threshold is a policy knob; fold it into the verdict.
    if summary.get("samples", 0) and summary["coveragePercent"] < args.min_coverage:
        if summary["result"] == "PASS":
            summary["result"] = "FAIL"
        message = f"coverage {summary['coveragePercent']}% < required {args.min_coverage}%"
        if message not in summary["reasons"]:
            summary["reasons"].append(message)

    if args.json:
        print(json.dumps(summary, indent=2, ensure_ascii=False))
    else:
        print_human(summary)
    return 0 if summary["result"] == "PASS" else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))

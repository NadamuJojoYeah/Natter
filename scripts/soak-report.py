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

Modes:
  --mode wallclock   (default) one uninterrupted window. Any gap, restart or missing coverage
                     fails it. Use this on a machine with stable power.
  --mode cumulative  "effective runtime" across days, restarts and power cuts. Each segment is
                     bounded by startedAt changes; only the first interval*1.5 of every sample gap
                     counts as effective runtime; every gap must be attributed. Extra guards:
                     a minimum segment length and a worst-segment RSS drift limit, so a run cannot
                     be assembled out of many short fragments.

Usage:
  python3 scripts/soak-report.py artifacts/ops/.../soak-2026-11-17.jsonl
  python3 scripts/soak-report.py soak/*.jsonl --interval 120 --min-coverage 95
  python3 scripts/soak-report.py soak.jsonl --json > soak-report.json
  python3 scripts/soak-report.py soak-*.jsonl --mode cumulative --target-hours 24
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


def print_cumulative(summary: dict) -> None:
    print(f"soak files      : {len(summary.get('files', []))}")
    for name in summary.get("files", []):
        print(f"                  {name}")
    print(f"mode            : cumulative (effective runtime; gaps attributed, not fatal by themselves)")
    effective = summary.get("effectiveHours", 0.0)
    target = summary.get("targetHours", 24)
    marker = "ok" if effective >= target else "SHORT"
    print(f"effective       : {effective}h / target {target}h   [{marker}]  ({summary.get('effectiveSeconds', 0)}s)")
    print(f"window          : {summary.get('sampleCount', summary.get('samples', 0))} samples, "
          f"{round(summary.get('windowSeconds', 0) / 3600.0, 2)}h wall clock, interval "
          f"{summary.get('intervalSeconds', '?')}s, tolerance {summary.get('toleranceSeconds', '?')}s")
    print(f"segments        : {summary.get('segmentCount', 0)}   longest {summary.get('longestSegmentHours', 0)}h"
          f" (required {summary.get('minSegmentHours', 6)}h)   restarts {summary.get('restartCount', 0)}")
    attribution = summary.get("gapAttribution", {})
    gaps = summary.get("gaps", [])
    print(f"gaps            : {len(gaps)}   host-down={attribution.get('host-down', 0)}"
          f" host-restart={attribution.get('host-restart', 0)}"
          f" suspend-or-stall={attribution.get('suspend-or-stall', 0)}"
          f" unknown={attribution.get('unknown', 0)}")
    for gap in gaps:
        flag = "!" if gap["attribution"] == "unknown" else " "
        detail = gap.get("detail", {})
        note = detail.get("note", "")
        print(f" {flag} {gap['from']} -> {gap['to']}  {gap['seconds']}s  {gap['attribution']}"
              f"{('  (' + note + ')') if note else ''}")
    print(f"unexplained     : {summary.get('unexplainedPercent', 0)}%"
          f" (allowed {summary.get('maxUnexplainedPercent', 2)}%),"
          f" {summary.get('unexplainedSeconds', 0)}s of excess gap time")
    worst = summary.get("worstRssDriftPercent")
    if worst is None:
        print(f"worst RSS drift : insufficient-data (allowed {summary.get('maxRssDriftPercent', 20)}%)")
    else:
        print(f"worst RSS drift : {worst}% (allowed {summary.get('maxRssDriftPercent', 20)}%)")
    attribution_block = summary.get("disconnectAttribution", {})
    print(f"disconnects     : attributed={attribution_block.get('attributed', 0)}"
          f" silent-new={attribution_block.get('silentNewPayload', 0)}"
          f" silent-legacy={attribution_block.get('silentLegacyPayload', 0)}"
          f" rate={attribution_block.get('ratePercent', 100)}%")
    print(f"sendFailures    : max {summary.get('sendFailuresMax', 0)}")
    print(f"host crashes    : {summary.get('hostCrashes', 0)}  (core dumps are outside the sample data;"
          f" verify with `coredumpctl list natter`)")
    for segment in summary.get("segments", []):
        drift = segment.get("rssDriftPercent")
        drift_text = "n/a" if drift is None else f"{drift}%"
        print(f"  segment {segment['index']}: {segment['startAt']} -> {segment['endAt']}"
              f"  effective={segment['effectiveHours']}h  samples={segment['sampleCount']}"
              f"  rssDrift={drift_text}")
    print(f"result          : {summary.get('result')}")
    for reason in summary.get("verdictReasons", []):
        print(f"  - {reason}")
    for note in summary.get("notes", []):
        print(f"  note: {note}")


def _parse_boot(upstream_raw: dict) -> str | None:
    boot = upstream_raw.get("bootId")
    return str(boot) if isinstance(boot, str) and boot else None


def _classify_gap(previous: "Sample", current: "Sample", tolerance: float) -> tuple[str, dict]:
    """Attribute a gap using only what the samples themselves carry.

    host-down          bootId changed  -> the machine rebooted (power cut, kernel update, ...)
    host-restart       bootId same, startedAt changed -> the service was restarted
    suspend-or-stall   bootId same, startedAt same -> the machine slept, or the sampler stalled
    unknown            not enough information (older payloads without bootId and without startedAt)
    """
    prev_boot, cur_boot = _parse_boot(previous.raw), _parse_boot(current.raw)
    prev_started, cur_started = previous.raw.get("startedAt"), current.raw.get("startedAt")
    detail: dict = {}

    if prev_boot and cur_boot:
        if prev_boot != cur_boot:
            detail["bootIdChanged"] = True
            return "host-down", detail
        if prev_started != cur_started:
            detail["startedAtChanged"] = True
            return "host-restart", detail
        return "suspend-or-stall", detail

    # Legacy payload: no bootId on one side. Fall back to startedAt, and mark the missing evidence.
    detail["bootIdMissing"] = True
    if prev_started and cur_started:
        if prev_started != cur_started:
            detail["startedAtChanged"] = True
            detail["note"] = "bootId missing; attributed from startedAt"
            return "host-restart", detail
        detail["note"] = "bootId missing; startedAt unchanged, cannot distinguish suspend from power loss"
        return "suspend-or-stall", detail
    detail["note"] = "bootId missing and startedAt unavailable"
    return "unknown", detail


def summarise_cumulative(
    audit: SoakAudit,
    interval: int,
    target_hours: float,
    min_segment_hours: float,
    max_unexplained_percent: float,
    max_rss_drift_percent: float,
) -> dict:
    """Effective-runtime report: segments, gap attribution, RSS drift, verdict."""
    samples = audit.samples
    tolerance = interval * 1.5
    if not samples:
        return {
            "mode": "cumulative",
            "result": "FAIL",
            "effectiveSeconds": 0,
            "effectiveHours": 0.0,
            "segments": [],
            "gapAttribution": {},
            "unexplainedPercent": 0.0,
            "worstRssDriftPercent": None,
            "verdictReasons": ["no usable /health samples in the input"],
        }

    # --- segments: a new segment starts whenever startedAt changes -------------------------
    raw_segments: list[dict] = []
    for sample in samples:
        started = sample.raw.get("startedAt")
        if not raw_segments or raw_segments[-1]["startedAt"] != started:
            raw_segments.append({"startedAt": started, "samples": []})
        raw_segments[-1]["samples"].append(sample)

    segments: list[dict] = []
    for index, segment in enumerate(raw_segments):
        members = segment["samples"]
        effective = 0.0
        for previous, current in zip(members, members[1:]):
            delta = (current.at - previous.at).total_seconds()
            effective += min(delta, tolerance)
        rss_samples = [
            m for m in members
            if isinstance(m.raw.get("rssBytes"), (int, float)) and m.raw.get("rssBytes") > 0
        ]
        drift = None
        if len(rss_samples) >= 2:
            first, last = float(rss_samples[0].raw["rssBytes"]), float(rss_samples[-1].raw["rssBytes"])
            drift = round((last - first) / first * 100.0, 2)
        segments.append(
            {
                "index": index,
                "startedAt": segment["startedAt"],
                "startAt": members[0].at.isoformat(),
                "endAt": members[-1].at.isoformat(),
                "sampleCount": len(members),
                "effectiveSeconds": int(round(effective)),
                "effectiveHours": round(effective / 3600.0, 2),
                "durationHours": round((members[-1].at - members[0].at).total_seconds() / 3600.0, 2),
                "bootIds": sorted({_parse_boot(m.raw) for m in members if _parse_boot(m.raw)}),
                "rssDriftPercent": drift,
                "rssFirst": int(rss_samples[0].raw["rssBytes"]) if rss_samples else None,
                "rssLast": int(rss_samples[-1].raw["rssBytes"]) if rss_samples else None,
            }
        )

    # --- gaps between consecutive samples, regardless of segment boundaries ---------------
    gaps: list[dict] = []
    attribution_counts = {"host-down": 0, "host-restart": 0, "suspend-or-stall": 0, "unknown": 0}
    unexplained_seconds = 0.0
    for previous, current in zip(samples, samples[1:]):
        delta = (current.at - previous.at).total_seconds()
        if delta <= tolerance:
            continue
        attribution, detail = _classify_gap(previous, current, tolerance)
        excess = delta - tolerance
        attribution_counts[attribution] = attribution_counts.get(attribution, 0) + 1
        if attribution == "unknown":
            unexplained_seconds += excess
        gaps.append(
            {
                "from": previous.at.isoformat(),
                "to": current.at.isoformat(),
                "seconds": int(delta),
                "toleranceSeconds": int(tolerance),
                "excessSeconds": int(excess),
                "attribution": attribution,
                "detail": detail,
            }
        )

    window_seconds = (samples[-1].at - samples[0].at).total_seconds()
    unexplained_percent = round(unexplained_seconds / window_seconds * 100.0, 2) if window_seconds > 0 else 0.0

    effective_seconds = sum(segment["effectiveSeconds"] for segment in segments)
    effective_hours = round(effective_seconds / 3600.0, 2)
    longest_hours = max((segment["effectiveHours"] for segment in segments), default=0.0)

    drifts = [segment["rssDriftPercent"] for segment in segments if segment["rssDriftPercent"] is not None]
    worst_drift = max(drifts) if drifts else None

    # Reuse the wallclock summary for sendFailures / disconnect attribution / provider health.
    base = summarise(audit, interval, max(int(tolerance), 900))
    send_failures_max = max(
        (data.get("sendFailuresMax", 0) for data in base.get("upstreams", {}).values()), default=0
    )
    silent_new = sum(
        data.get("silentDisconnects", {}).get("newPayload", 0) for data in base.get("upstreams", {}).values()
    )
    silent_legacy = sum(
        data.get("silentDisconnects", {}).get("legacyPayload", 0) for data in base.get("upstreams", {}).values()
    )
    attributed = sum(
        data.get("disconnectsWithReason", {}).get("newPayload", 0)
        + data.get("disconnectsWithReason", {}).get("legacyPayload", 0)
        for data in base.get("upstreams", {}).values()
    )
    total_disconnect_events = attributed + silent_new + silent_legacy
    attribution_rate = (
        round(attributed / total_disconnect_events * 100.0, 2) if total_disconnect_events else 100.0
    )

    restart_count = len(segments) - 1
    restarts_with_gap = attribution_counts["host-restart"] + attribution_counts["host-down"]
    restarts_without_gap = max(0, restart_count - restarts_with_gap)

    verdict_reasons: list[str] = []
    notes: list[str] = []

    if effective_hours < target_hours:
        verdict_reasons.append(
            f"effective runtime {effective_hours}h < target {target_hours}h"
        )
    if longest_hours < min_segment_hours:
        verdict_reasons.append(
            f"longest continuous segment {longest_hours}h < required minimum {min_segment_hours}h"
        )
    unexplained_gaps = attribution_counts.get("unknown", 0)
    if unexplained_gaps:
        verdict_reasons.append(f"{unexplained_gaps} gap(s) could not be attributed (unknown)")
    if unexplained_percent > max_unexplained_percent:
        verdict_reasons.append(
            f"unexplained gap time {unexplained_percent}% > allowed {max_unexplained_percent}%"
        )
    if send_failures_max > 0:
        verdict_reasons.append(f"sendFailures reached {send_failures_max}")
    if silent_new > 0:
        verdict_reasons.append(
            f"{silent_new} disconnect(s) without reason on the new payload (OBS-019 regression)"
        )
    if worst_drift is None:
        notes.append("RSS drift: insufficient-data (need two samples with rssBytes in one segment)")
    elif worst_drift > max_rss_drift_percent:
        verdict_reasons.append(
            f"worst-segment RSS drift {worst_drift}% > allowed {max_rss_drift_percent}%"
        )
    if silent_legacy:
        notes.append(
            f"{silent_legacy} legacy disconnect(s) have no reason field (pre-OBS-019 payload) — "
            "counted for information, not as a failure"
        )
    if restarts_without_gap:
        notes.append(
            f"{restarts_without_gap} restart(s) happened between samples (no gap to attribute); "
            "a host crash would look like this, so check `coredumpctl list natter` for core dumps"
        )
    if any(segment["rssDriftPercent"] is None for segment in segments):
        notes.append("RSS drift: some segments had <2 rssBytes samples and are excluded from the worst-case")

    return {
        "mode": "cumulative",
        "result": "PASS" if not verdict_reasons else "FAIL",
        "effectiveSeconds": effective_seconds,
        "effectiveHours": effective_hours,
        "targetHours": target_hours,
        "minSegmentHours": min_segment_hours,
        "maxUnexplainedPercent": max_unexplained_percent,
        "maxRssDriftPercent": max_rss_drift_percent,
        "toleranceSeconds": int(tolerance),
        "windowSeconds": int(window_seconds),
        "segments": segments,
        "segmentCount": len(segments),
        "longestSegmentHours": longest_hours,
        "restartCount": restart_count,
        "restartsWithGap": restarts_with_gap,
        "restartsWithoutGap": restarts_without_gap,
        "gapAttribution": attribution_counts,
        "gaps": gaps,
        "unexplainedSeconds": int(unexplained_seconds),
        "unexplainedPercent": unexplained_percent,
        "worstRssDriftPercent": worst_drift,
        "sendFailuresMax": send_failures_max,
        "disconnectAttribution": {
            "attributed": attributed,
            "silentNewPayload": silent_new,
            "silentLegacyPayload": silent_legacy,
            "ratePercent": attribution_rate,
        },
        "hostCrashes": 0,
        "verdictReasons": verdict_reasons,
        "notes": notes,
    }


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


def _cumulative_fixture(
    segment_specs: list[dict],
    interval: int = 3600,
    extra_gap: int = 0,
    breaker=None,
) -> list[dict]:
    """Build a multi-segment soak run for cumulative-mode tests.

    Each spec: {samples, bootId?, rss?, skip_after?}
      rss        int or list of ints (per sample index) -> row-level rssBytes
      skip_after {sampleIndex: extraSeconds} -> inject a gap after that sample (power cut/restart)
    `breaker(seg_index, sample_index, row)` may delete fields to emulate legacy payloads.
    """
    start = dt.datetime(2026, 2, 1, tzinfo=dt.timezone.utc)
    moment = start
    rows: list[dict] = [
        {"soakStart": start.isoformat(), "intervalSeconds": interval, "durationSeconds": 86400, "port": 5099}
    ]
    uptime = 0
    for seg_index, spec in enumerate(segment_specs):
        if seg_index > 0 and extra_gap:
            moment = moment + dt.timedelta(seconds=extra_gap)
        skip_after = spec.get("skip_after") or {}
        rss_spec = spec.get("rss")
        for index in range(int(spec["samples"])):
            if isinstance(rss_spec, list):
                rss = rss_spec[min(index, len(rss_spec) - 1)]
            else:
                rss = rss_spec
            row: dict = {
                "status": "healthy",
                "uptimeSeconds": uptime,
                "startedAt": spec.get("startedAt", f"2026-02-01T{seg_index:02d}:00:00Z"),
                "sampledAt": moment.isoformat(),
                "upstreams": [
                    {
                        "source": "discord:discord:1",
                        "connected": True,
                        "reconnectCount": 0,
                        "lastDisconnectAt": None,
                        "lastError": None,
                        "sendFailures": 0,
                        "sendFailuresTotal": 0,
                    }
                ],
                "providers": [
                    {"id": "natter.feature.ai.deepseek#deepseek", "available": True, "supportTier": 1}
                ],
                "plugins": {"activated": 4, "failed": 0, "failures": []},
                "conversations": 0,
            }
            if spec.get("bootId"):
                row["bootId"] = spec["bootId"]
            if rss:
                row["rssBytes"] = int(rss)
            if breaker:
                breaker(seg_index, index, row)
            rows.append(row)
            uptime += interval
            moment = moment + dt.timedelta(seconds=interval)
            if index in skip_after:
                moment = moment + dt.timedelta(seconds=skip_after[index])
                uptime += skip_after[index]
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

    def run_audit(rows: list[dict]) -> SoakAudit:
        with tempfile.NamedTemporaryFile("w", suffix=".jsonl", delete=False) as handle:
            handle.write("\n".join(json.dumps(row) for row in rows) + "\n")
            path = Path(handle.name)
        try:
            return load([path])
        finally:
            path.unlink(missing_ok=True)

    def audit(rows: list[dict], interval: int = 300) -> dict:
        return summarise(run_audit(rows), interval, 900)

    def audit_cumulative(rows: list[dict], interval: int = 3600, **overrides) -> dict:
        options = {
            "target_hours": 24.0,
            "min_segment_hours": 6.0,
            "max_unexplained_percent": 2.0,
            "max_rss_drift_percent": 20.0,
        }
        options.update(overrides)
        return summarise_cumulative(run_audit(rows), interval, **options)

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

    # ---- cumulative mode ------------------------------------------------------------------
    # 6. Four 6h segments with power cuts in between: 24h effective runtime, every gap attributed.
    passing = _cumulative_fixture(
        [{"samples": 7, "bootId": f"boot-{i}", "rss": 100_000_000} for i in range(4)],
        interval=3600,
        extra_gap=2 * 3600,
    )
    summary = audit_cumulative(passing)
    check("cumulative: 24h built from 4 segments passes", summary["result"] == "PASS", summary["verdictReasons"])
    check("cumulative: effective runtime is 24h", summary["effectiveHours"] == 24.0, summary["effectiveHours"])
    check("cumulative: segments and longest segment are reported",
          summary["segmentCount"] == 4 and summary["longestSegmentHours"] == 6.0,
          (summary["segmentCount"], summary["longestSegmentHours"]))
    check("cumulative: power cuts attributed to host-down",
          summary["gapAttribution"]["host-down"] == 3 and summary["gapAttribution"]["unknown"] == 0,
          summary["gapAttribution"])
    check("cumulative: RSS drift measured when rssBytes exists", summary["worstRssDriftPercent"] == 0.0,
          summary["worstRssDriftPercent"])

    # 7. Same 24h total, but assembled from 3h fragments -> must fail the min-segment guard.
    fragmentary = _cumulative_fixture(
        [{"samples": 4, "bootId": f"boot-{i}", "rss": 100_000_000} for i in range(8)],
        interval=3600,
        extra_gap=2 * 3600,
    )
    summary = audit_cumulative(fragmentary)
    check("cumulative: fragmented 24h fails the min-segment guard",
          summary["result"] == "FAIL"
          and any("longest continuous segment" in reason for reason in summary["verdictReasons"]),
          summary["verdictReasons"])

    # 8. A gap whose endpoints carry neither bootId nor startedAt cannot be attributed.
    def strip_identity(seg_index: int, sample_index: int, row: dict) -> None:
        if seg_index == 0 and sample_index in (3, 4):
            row.pop("bootId", None)
            row.pop("startedAt", None)

    unattributed = _cumulative_fixture(
        [{"samples": 7, "bootId": "boot-a", "rss": 100_000_000,
          "skip_after": {3: 2 * 3600}}],
        interval=3600,
        breaker=strip_identity,
    )
    summary = audit_cumulative(unattributed)
    check("cumulative: unattributable gap is reported as unknown",
          summary["gapAttribution"]["unknown"] == 1 and summary["result"] == "FAIL"
          and any("could not be attributed" in reason for reason in summary["verdictReasons"]),
          (summary["gapAttribution"], summary["verdictReasons"]))

    # 9. Two 12h segments but memory grows 100MB -> 300MB in the first one.
    drifting = _cumulative_fixture(
        [
            {"samples": 13, "bootId": "boot-a", "rss": [100_000_000] * 6 + [300_000_000] * 7},
            {"samples": 13, "bootId": "boot-b", "rss": 300_000_000},
        ],
        interval=3600,
        extra_gap=2 * 3600,
    )
    summary = audit_cumulative(drifting)
    check("cumulative: RSS drift over the limit fails the run",
          summary["result"] == "FAIL"
          and any("RSS drift" in reason for reason in summary["verdictReasons"]),
          (summary["worstRssDriftPercent"], summary["verdictReasons"]))
    check("cumulative: drift uses the worst segment", summary["worstRssDriftPercent"] == 200.0,
          summary["worstRssDriftPercent"])

    # 10. Legacy payloads (no bootId/rssBytes) degrade instead of raising.
    legacy = _cumulative_fixture(
        [{"samples": 7, "rss": None} for _ in range(2)], interval=3600, extra_gap=2 * 3600
    )
    for row in legacy:
        row.pop("bootId", None)
        row.pop("rssBytes", None)
    summary = audit_cumulative(legacy)
    check("cumulative: legacy payload degrades RSS drift to insufficient-data",
          summary["worstRssDriftPercent"] is None, summary["worstRssDriftPercent"])
    check("cumulative: legacy payload still attributes gaps from startedAt",
          summary["gapAttribution"]["host-restart"] == 1 and summary["gapAttribution"]["unknown"] == 0,
          summary["gapAttribution"])

    if failures:
        print(f"self-test FAILED ({failures} case(s))", file=sys.stderr)
        return 1
    print("self-test passed (10 scenarios: 5 wallclock + 5 cumulative)")
    return 0


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description="Audit a Natter passive soak jsonl.")
    parser.add_argument("files", nargs="*", type=Path, help="soak jsonl file(s)")
    parser.add_argument("--interval", type=int, default=120, help="expected sampling interval in seconds (default 120)")
    parser.add_argument("--max-gap", type=int, default=360, help="largest tolerated gap in seconds (default 360)")
    parser.add_argument("--min-coverage", type=float, default=95.0, help="minimum coverage percent (default 95)")
    parser.add_argument("--json", action="store_true", help="emit the report as JSON")
    parser.add_argument("--self-test", action="store_true", help="run synthetic fixtures and exit")
    parser.add_argument(
        "--mode",
        choices=("wallclock", "cumulative"),
        default="wallclock",
        help="wallclock = one uninterrupted window (default); cumulative = effective runtime across "
             "restarts and power cuts",
    )
    parser.add_argument("--target-hours", type=float, default=24.0,
                        help="cumulative: effective runtime target (default 24)")
    parser.add_argument("--min-segment-hours", type=float, default=6.0,
                        help="cumulative: required longest continuous segment (default 6)")
    parser.add_argument("--max-unexplained-percent", type=float, default=2.0,
                        help="cumulative: allowed share of unexplained gap time (default 2)")
    parser.add_argument("--max-rss-drift-percent", type=float, default=20.0,
                        help="cumulative: allowed worst-segment RSS growth (default 20)")
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

    if args.mode == "cumulative":
        # The wallclock coverage/durationale rules do not apply: gaps are expected and attributed.
        wallclock = summarise(audit, args.interval, args.max_gap)
        cumulative = summarise_cumulative(
            audit,
            args.interval,
            args.target_hours,
            args.min_segment_hours,
            args.max_unexplained_percent,
            args.max_rss_drift_percent,
        )
        summary = {
            **wallclock,
            **cumulative,
            "wallclock": {
                "result": wallclock.get("result"),
                "coveragePercent": wallclock.get("coveragePercent"),
                "reasons": wallclock.get("reasons", []),
            },
        }
        summary["reasons"] = cumulative["verdictReasons"]
        if args.json:
            print(json.dumps(summary, indent=2, ensure_ascii=False))
        else:
            print_cumulative(summary)
        return 0 if summary["result"] == "PASS" else 1

    summary = summarise(audit, args.interval, args.max_gap)
    summary["mode"] = "wallclock"
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

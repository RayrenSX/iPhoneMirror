"""Correlate script input with hook decisions, sends and transport recovery.

Image differences are observations, not proof that an iOS command succeeded.
Legacy runs without downAt/upAt use a wider observation window and are labelled.
"""
import argparse
import bisect
import collections
import datetime as dt
import json
from pathlib import Path
import re


def timestamp(value):
    return dt.datetime.fromisoformat(value).timestamp()


def read_log(path):
    result = []
    for line in path.read_text(encoding="utf-8-sig", errors="replace").splitlines():
        if not line.startswith("20"):
            continue
        fields = {}
        for name, value in re.findall(r'(\w+)=("(?:\\.|[^"\\])*"|\S+)', line):
            if value.startswith('"'):
                try:
                    value = json.loads(value)
                except ValueError:
                    value = value[1:-1]
            fields[name] = value
        fields["time"] = timestamp(fields.get("observed_at", line.split()[0]))
        fields["line"] = line
        result.append(fields)
    return result


def recovery_windows(logs):
    # USB can repair inside one bridge, while wireless restarts the bridge and
    # only the host reports Ready. Keep separate transport windows and close
    # either at its authoritative Ready instead of marking all later input as
    # "during recovery" when recovery_completed never appears.
    active = {}
    windows = []
    for item in logs:
        mode = item.get("mode")
        if mode not in ("usb", "wireless"):
            continue
        if item.get("bridge_code") in ("recovery_triggered", "transport_failure_detected", "recovery_started"):
            active.setdefault(mode, item["time"])
        elif (item.get("bridge_code") == "recovery_completed" or item.get("event") == "stage_ready") and mode in active:
            windows.append((active.pop(mode), item["time"]))
    windows.extend((start, float("inf")) for start in active.values())
    return sorted(windows)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directory", type=Path)
    args = parser.parse_args()
    folder = args.directory
    events = [json.loads(line) for line in (folder / "events.jsonl").read_text(
        encoding="utf-8-sig").splitlines()]
    logs = []
    for pattern in ("application.log*", "reverse-control.log*"):
        for path in (folder / "logs").glob(pattern):
            logs.extend(read_log(path))
    logs.sort(key=lambda item: item["time"])
    times = [item["time"] for item in logs]
    recoveries = recovery_windows(logs)
    recovery_times = sorted(end for _, end in recoveries if end != float("inf"))
    rows = []
    for event in events:
        if event["kind"] != "keypress":
            continue
        press = event["data"]
        exact = "downAt" in press
        start = timestamp(press["downAt"]) - .03 if exact else timestamp(event["time"]) - 1.8
        end = timestamp(event["time"]) + .03
        nearby = logs[bisect.bisect_left(times, start):bisect.bisect_right(times, end)]
        hook = [item for item in nearby if item.get("event") == "function_key_route"
                and item.get("source") == "hook" and item.get("down") == "True"
                and item.get("virtual_key") == str(press["key"])]
        ups = [item for item in nearby if item.get("event") == "function_key_route"
               and item.get("source") == "hook" and item.get("down") == "False"
               and item.get("virtual_key") == str(press["key"])]
        recovered_presses = [item for item in nearby if item.get("event") == "function_key_route"
                             and item.get("source") == "window_press"
                             and item.get("virtual_key") == str(press["key"])
                             and item.get("decision") == "shortcut_press_recovered"]
        recovered = [item for item in nearby if item.get("event") == "function_key_route"
                     and item.get("source") == "window_release"
                     and item.get("virtual_key") == str(press["key"])
                     and item.get("decision") == "shortcut_release_recovered"]
        legacy = [item for item in nearby if item.get("event") == "function_key_received"
                  and item.get("virtual_key") == str(press["key"])]
        sent = any(f"system_shortcut_sent action={press['action']} " in item["line"] for item in nearby)
        recovery = [item for item in nearby if item.get("bridge_code") == "recovery_triggered"]
        recovery_index = bisect.bisect_right(recovery_times, start) - 1
        since_recovery = start - recovery_times[recovery_index] if recovery_index >= 0 else None
        overlaps_recovery = any(begin <= end and finish >= start for begin, finish in recoveries)
        failures = [item["line"] for item in nearby if "system_shortcut_failed" in item["line"]]
        skipped = [item.get("reason") for item in nearby if item.get("event") == "system_shortcut_skipped"]
        decisions = [item.get("decision") for item in hook]
        if hook:
            category = "matched_sent" if "shortcut_matched" in decisions and sent else (
                "matched_not_sent" if "shortcut_matched" in decisions else "decision_" + str(decisions[0]))
        elif recovered_presses:
            category = "window_press_recovered_sent" if sent else "window_press_recovered_not_sent"
        else:
            category = "legacy_observed" if legacy else "no_hook_observation"
            category += "_sent" if sent else "_not_sent"
        rows.append({"index": press["index"], "action": press["action"], "key": press["key"],
                     "hookDownOmitted": press.get("hookDownOmitted", False),
                     "category": category, "exactInputTimes": exact, "hookDowns": len(hook),
                     "hookUps": len(ups), "decisions": decisions, "sent": sent,
                     "windowReleaseRecovered": bool(recovered),
                     "windowPressRecovered": bool(recovered_presses),
                     "skipped": skipped, "recoveryTriggered": bool(recovery),
                     "secondsSinceRecovery": since_recovery, "sendFailures": failures,
                     "overlapsRecovery": overlaps_recovery,
                     "foregroundStable": (press.get("foregroundBefore") == press.get("foregroundAfter")) if exact else None,
                     "imageDifference": press["imageDifference"],
                     "expectedDismissal": press.get("expectedDismissal", False),
                     "evidence": [item["line"] for item in nearby if item.get("event") in
                                  ("function_key_route", "system_shortcut_skipped", "shortcut_cancelled")]
                     if category != "matched_sent" else []})
    for index, row in enumerate(rows):
        if row["windowReleaseRecovered"]:
            following = next((item for item in rows[index + 1:] if item["key"] == row["key"]), None)
            row["nextSameKeyIndex"] = following["index"] if following else None
            row["nextSameKeySent"] = following["category"] == "matched_sent" if following else None
    summary = {"keypresses": len(rows), "categories": dict(collections.Counter(row["category"] for row in rows)),
               "sent": sum(row["sent"] for row in rows),
               "windowPressRecovered": sum(row["windowPressRecovered"] for row in rows),
               "injectedHookDownOmissions": sum(row["hookDownOmitted"] for row in rows),
               "injectedHookDownRecovered": sum(row["hookDownOmitted"] and row["windowPressRecovered"] for row in rows),
               "injectedHookDownNotRecovered": sum(row["hookDownOmitted"] and not row["windowPressRecovered"] for row in rows),
               "unrecoveredHookDown": sum(row["hookDowns"] == 0 and not row["windowPressRecovered"] for row in rows),
               "missingHookUp": sum(row["hookDowns"] > 0 and row["hookUps"] == 0 for row in rows),
               "windowReleaseRecovered": sum(row["windowReleaseRecovered"] for row in rows),
               "recoveredNextSameKeySent": sum(row.get("nextSameKeySent") is True for row in rows),
               "notSentDuringRecovery": sum(not row["sent"] and row["overlapsRecovery"] for row in rows),
               "notSentOutsideRecovery": sum(not row["sent"] and not row["overlapsRecovery"] for row in rows),
               "unrecoveredHookUp": sum(row["hookDowns"] > 0 and row["hookUps"] == 0 and
                                        not row["windowReleaseRecovered"] for row in rows),
               "lowImageChange": sum(row["imageDifference"] < .01 and row["action"] in
                                     ("ControlCenter", "NotificationCenter", "AppSwitcher") for row in rows),
               "sentLowImageChange": sum(row["sent"] and row["imageDifference"] < .01 and row["action"] in
                                         ("ControlCenter", "NotificationCenter", "AppSwitcher") for row in rows),
               "lowHomeDismissalChange": sum(row["imageDifference"] < .01 and row["expectedDismissal"] for row in rows),
               "limitations": "Sent is transport evidence, not iPhone UI confirmation; low image change requires review."}
    (folder / "routing-analysis.json").write_text(json.dumps({"summary": summary, "presses": rows},
        ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps(summary, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()

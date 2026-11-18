#!/usr/bin/env python3
"""Summarize projection geometry from capture logs without opening a receiver.

Usage: python scripts/analyze_projection_geometry.py capture.log [more.log ...]
       python scripts/analyze_projection_geometry.py capture.log --output report.json

Only recognized structured messages are exported. Dimensions describe encoded or
decoded rectangles, not physical orientation, stretching, cropping or black bars.
Associations are forward-only and scoped to one file and process session. A hub
launcher's thread is not the host IPC thread: only a matching host startup binds
the latter. Missing or ambiguous associations stay explicitly unknown.
"""

from __future__ import annotations

import argparse
from dataclasses import dataclass
import json
from pathlib import Path
import re
import sys
from typing import Iterable


_TIMESTAMP = re.compile(r"^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}(?:\.\d+)?)\s+")
_FIELD = re.compile(r"(?:^|\s)([a-zA-Z_][a-zA-Z_0-9]*)=([^\s]+)")
_SIZE = re.compile(r"([0-9]+)x([0-9]+)\Z")
_CAPABILITY = re.compile(r"([0-9]+)x([0-9]+)@([0-9]+)\Z")
_FINGERPRINT = re.compile(r"anon-[0-9a-fA-F]{1,64}\Z")


def dimensions(width: int, height: int) -> dict | None:
    """Describe a rectangle; square pixels do not reveal device orientation."""
    if not (0 < width <= 65536 and 0 < height <= 65536):
        return None
    return {
        "width": width,
        "height": height,
        "shape": "square" if width == height else "rectangle",
        "orientation": "unknown" if width == height else
            "landscape" if width > height else "portrait",
    }


def _size(value: str | None) -> dict | None:
    match = _SIZE.fullmatch(value or "")
    return dimensions(int(match[1]), int(match[2])) if match else None


def _capability(value: str | None) -> dict:
    match = _CAPABILITY.fullmatch(value or "")
    if match:
        rectangle = dimensions(int(match[1]), int(match[2]))
        if rectangle and 0 < int(match[3]) <= 1000:
            return {"status": "known", **rectangle, "fps": int(match[3])}
    return {"status": "unknown"}


def _record(line: str) -> tuple[str | None, str | None, str | None, str]:
    timestamp = None
    match = _TIMESTAMP.match(line)
    if match:
        timestamp, line = match[1], line[match.end():]
    metadata = {}
    while line.startswith("[") and "]" in line:
        end = line.index("]")
        item = line[1:end]
        if "=" in item:
            key, value = item.split("=", 1)
            metadata[key] = value
        elif item == "startup":
            # The initial session record predates the normal bracketed header.
            metadata.update(dict(_FIELD.findall(line[end + 1:])))
        line = line[end + 1:].lstrip()
    return timestamp, metadata.get("session"), metadata.get("tid"), line.strip()


@dataclass
class _Host:
    label: str
    capability: dict
    mode: str


class _Session:
    def __init__(self, label: str, identified: bool):
        self.label = label
        self.identified = identified
        self.events: list[dict] = []
        self.hosts: dict[str, _Host] = {}
        self.host_count = 0
        self.threads: dict[str, _Host] = {}
        self.thread_labels: dict[str, str] = {}
        self.devices: dict[str, str] = {}
        self.device_hosts: dict[str, set[str]] = {}
        self.host_labels: dict[str, _Host] = {}
        self.wired_devices: dict[str, str] = {}
        self.last_events: dict[tuple, tuple[str, dict]] = {}

    def _device(self, value: str | None) -> str:
        if not value or not _FINGERPRINT.fullmatch(value):
            return "unknown"
        return self.devices.setdefault(value, f"device-{len(self.devices) + 1}")

    def _thread(self, value: str | None) -> str:
        if value is None:
            return "unknown"
        return self.thread_labels.setdefault(value, f"thread-{len(self.thread_labels) + 1}")

    def _host(self, fields: dict, startup: bool) -> _Host:
        pid = fields.get("pid", "")
        capability = _capability(fields.get("capability"))
        mode = fields.get("mode_arg" if startup else "mode", "unknown")
        if mode not in ("mirror", "media"):
            mode = "unknown"
        previous = self.hosts.get(pid) if pid.isdecimal() else None
        # hub host_started starts a new host lifetime, even if a PID is reused.
        if startup and previous and previous.capability == capability and previous.mode == mode:
            return previous
        if previous:
            # A truncated log can omit "stopped" before PID reuse. Revoke only
            # this previous host's bindings as soon as its new lifetime is
            # announced; the new startup must establish the IPC thread again.
            self.threads = {thread: host for thread, host in self.threads.items()
                            if host is not previous}
            for fingerprint, candidates in list(self.device_hosts.items()):
                candidates.discard(previous.label)
                if not candidates:
                    del self.device_hosts[fingerprint]
        self.host_count += 1
        host = _Host(f"receiver-{self.host_count}", capability, mode)
        if pid.isdecimal():
            self.hosts[pid] = host
        self.host_labels[host.label] = host
        return host

    def _wireless_context(self, thread: str | None, fingerprint: str | None) -> dict:
        device = self._device(fingerprint)
        host = self.threads.get(thread) if thread is not None else None
        association = "host_thread" if host else "unknown"
        if host and device != "unknown":
            self.device_hosts.setdefault(fingerprint, set()).add(host.label)
        elif thread is None and device != "unknown":
            candidates = self.device_hosts.get(fingerprint, set())
            if len(candidates) == 1:
                host = self.host_labels[next(iter(candidates))]
                association = "device_fingerprint"
        # An explicit but unbound thread can be a new receiver. Never carry a
        # device's former receiver capability over to it.
        return {
            "transport": "wireless",
            "receiver": host.label if host else "unknown",
            "receiver_mode": host.mode if host else "unknown",
            "thread": self._thread(thread),
            "device": device,
            "association": association,
            "capability": host.capability.copy() if host else {"status": "unknown"},
        }

    def _emit(self, event: dict, evidence: dict) -> None:
        # Collapse consecutive repetitions within each observation stream.
        # A -> B -> A remains three transitions. No backward association or
        # merge across devices, receiver lifetimes, threads or log formats.
        key = tuple(event.get(name) for name in
                    ("kind", "observation", "transport", "receiver", "thread", "device"))
        signature = json.dumps(event, sort_keys=True)
        previous = self.last_events.get(key)
        if previous and previous[0] == signature:
            previous[1]["occurrences"] += 1
            previous[1]["last_evidence"] = evidence
            return
        result = {**event, "evidence": evidence, "occurrences": 1}
        self.events.append(result)
        self.last_events[key] = signature, result

    def consume(self, body: str, thread: str | None, evidence: dict) -> None:
        fields = dict(_FIELD.findall(body))
        if body.startswith("wireless_hub stopped"):
            # This message has neither host PID nor receiver fingerprint. A
            # particular parallel receiver cannot be identified safely.
            self.threads.clear()
            self.device_hosts.clear()
            return
        if body.startswith("wireless_hub pipe_connected"):
            if thread is not None:
                self.threads.pop(thread, None)
            return
        launched = body.startswith("wireless_hub host_started ")
        startup = body.startswith("airplay_host: wireless_host startup ")
        if launched or startup:
            host = self._host(fields, startup)
            if startup and thread is not None and fields.get("pid", "").isdecimal():
                self.threads[thread] = host
            self._emit({
                "kind": "receiver_capability", "observation": "wireless_host_start",
                "transport": "wireless", "receiver": host.label,
                "receiver_mode": host.mode, "device": "unknown",
                "association": "host_pid" if fields.get("pid", "").isdecimal() else "unknown",
                "capability": host.capability.copy(),
            }, evidence)
            return
        if body.startswith(("wireless_hub connected ", "wireless_hub metadata ")):
            self._wireless_context(thread, fields.get("id_fp"))
            return
        if body.startswith("wireless_hub disconnected "):
            self.device_hosts.pop(fields.get("id_fp"), None)
            return
        if body.startswith("wireless_geometry "):
            source, output = _size(fields.get("source")), _size(fields.get("output"))
            if source and output:
                self._emit({
                    **self._wireless_context(thread, fields.get("device_fp")),
                    "kind": "source_geometry", "observation": "wireless_geometry",
                    "geometry": {"source": source, "output": output},
                }, evidence)
            return
        observation, rectangle, fingerprint = None, None, fields.get("device_fp")
        if body.startswith("wireless_resolution "):
            observation, rectangle = "wireless_resolution", _size(fields.get("decoded"))
        elif body.startswith("wireless_video published "):
            observation, rectangle = "wireless_video_published", _size(fields.get("size"))
        elif body.startswith("airplay_host: video callback "):
            observation, rectangle = "airplay_video_callback", _size(fields.get("size"))
        elif body.startswith("airplay_host: video_diag "):
            values = fields.get("values", "").split(",")
            event = fields.get("event")
            if event == "33" and len(values) == 6 and all(
                re.fullmatch(r"[0-9]{1,5}", value) for value in values
            ):
                codec, avc_only, source_width, source_height, output_width, output_height = map(int, values)
                if codec in (0, 1) and avc_only in (0, 1):
                    self._emit({
                        **self._wireless_context(thread, fingerprint),
                        "kind": "codec_negotiation", "observation": "airplay_codec_configuration",
                        "codec": "avc" if codec == 0 else "hevc",
                        "avc_diagnostic": bool(avc_only),
                        "geometry": {"source": dimensions(source_width, source_height),
                                     "output": dimensions(output_width, output_height)},
                    }, evidence)
                return
            if event == "34" and len(values) == 6 and all(
                re.fullmatch(r"[0-9]{1,10}", value) for value in values
            ):
                frames, receive_ms, sender_ms, long_gaps, processing_ms, peak_ms = map(int, values)
                if frames > 0 and receive_ms > 0:
                    self._emit({
                        **self._wireless_context(thread, fingerprint),
                        "kind": "receive_cadence", "observation": "airplay_encoded_cadence",
                        "frames": frames, "receive_ms": receive_ms, "sender_ms": sender_ms,
                        "receive_fps": round(1000 * frames / receive_ms, 2),
                        "sender_timestamp_fps": round(1000 * frames / sender_ms, 2) if sender_ms else None,
                        "sender_intervals_over_25_ms": long_gaps,
                        "processing_total_ms": processing_ms, "processing_peak_ms": peak_ms,
                    }, evidence)
                return
            if event == "4" and len(values) >= 2 and all(value.isdecimal() for value in values[:2]):
                observation, rectangle = "airplay_decoder_format", dimensions(int(values[0]), int(values[1]))
        if observation and rectangle:
            self._emit({
                **self._wireless_context(thread, fingerprint),
                "kind": "decoded_geometry", "observation": observation,
                "geometry": {"decoded": rectangle},
            }, evidence)
            return
        if body.startswith(("video_worker started", "video_worker stopped")):
            self.wired_devices.pop(thread, None)
        if body.startswith("usb_resolution "):
            rectangle = _size(fields.get("decoded"))
            device = self._device(fingerprint)
            if thread is not None:
                self.wired_devices[thread] = device
            observation = "usb_resolution"
        elif body.startswith("video_output "):
            if fields.get("width", "").isdecimal() and fields.get("height", "").isdecimal():
                rectangle = dimensions(int(fields["width"]), int(fields["height"]))
            device = self.wired_devices.get(thread, "unknown")
            observation = "video_output"
        else:
            return
        if rectangle:
            self._emit({
                "transport": "wired", "receiver": "not_applicable",
                "receiver_mode": "not_applicable", "thread": self._thread(thread),
                "device": device, "association": "not_applicable",
                "capability": {"status": "not_applicable"},
                "kind": "decoded_geometry", "observation": observation,
                "geometry": {"decoded": rectangle},
            }, evidence)


def analyze_lines(lines: Iterable[str], file: str = "capture.log") -> dict:
    """Analyze one file. Call separately for files; identities never cross files."""
    sessions: dict[tuple[int, str | None], _Session] = {}
    segment = 0
    for number, line in enumerate(lines, 1):
        if line.strip() == "=== iPhoneMirror capture session ===":
            segment += 1
            continue
        timestamp, session, thread, body = _record(line)
        # A line without a session header cannot inherit the most recently
        # observed process: multiple processes may have interleaved records.
        key = segment, session
        if key not in sessions:
            sessions[key] = _Session(f"session-{len(sessions) + 1}", session is not None)
        evidence = {"file": file, "line": number}
        if timestamp is not None:
            evidence["timestamp"] = timestamp
        sessions[key].consume(body, thread, evidence)
    return {
        "file": file,
        "sessions": [{"session": state.label,
                      "session_identity": "known" if state.identified else "unknown",
                      "timeline": state.events}
                     for state in sessions.values() if state.events],
    }


def analyze_files(paths: Iterable[str | Path]) -> dict:
    files = []
    for path in paths:
        path = Path(path)
        with path.open("r", encoding="utf-8-sig", errors="replace") as source:
            files.append(analyze_lines(source, str(path)))
    return {
        "schema_version": 1,
        "interpretation": [
            "Orientation describes rectangle dimensions only; square orientation is unknown.",
            "Dimensions alone do not establish stretching, cropping or black bars.",
            "Associations are forward-only and isolated by file and process session.",
            "Unidentified receiver stops invalidate wireless associations conservatively.",
            "Only recognized structured messages are included; unsupported associations are unknown.",
        ],
        "files": files,
    }


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("logs", nargs="+", type=Path, help="capture.log files to analyze independently")
    parser.add_argument("--output", type=Path, help="write JSON here instead of standard output")
    args = parser.parse_args(argv)
    if args.output and args.output.resolve() in {path.resolve() for path in args.logs}:
        parser.error("--output must not overwrite an input log")
    try:
        result = json.dumps(analyze_files(args.logs), ensure_ascii=False, indent=2) + "\n"
        if args.output:
            args.output.write_text(result, encoding="utf-8")
        else:
            sys.stdout.write(result)
    except OSError as error:
        # Avoid echoing exception messages that could include raw log contents.
        parser.exit(1, f"Could not read input or write output ({type(error).__name__}).\n")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

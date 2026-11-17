"""Exercise the local API without sending phone input or retaining screen content.

Set IPHONEMIRROR_API_KEY in the process environment. Start capture/control first.
"""
import argparse
import concurrent.futures
import http.client
import json
import os
import statistics
import struct
import time
import ctypes
import zlib
from datetime import datetime, timezone
from collections import Counter
from pathlib import Path


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--port", type=int, default=17890)
    parser.add_argument("--seconds", type=float, default=60)
    parser.add_argument("--rate", type=float, default=15)
    parser.add_argument("--concurrency", type=int, default=8)
    parser.add_argument("--app-pid", type=int)
    parser.add_argument("--burst", action="store_true", help="Request all sources simultaneously each cycle")
    parser.add_argument("--abort-every", type=int, default=0, help="Disconnect every Nth screenshot request")
    parser.add_argument("--idle", action="store_true", help="Monitor capture/control without issuing screenshots")
    parser.add_argument("--output", required=True)
    args = parser.parse_args()
    if args.seconds <= 0 or args.rate <= 0 or args.concurrency < 1 or args.abort_every < 0:
        parser.error("seconds/rate/concurrency must be positive; abort-every must be non-negative")
    started_at = datetime.now(timezone.utc).isoformat()
    headers = {"X-API-Key": os.environ["IPHONEMIRROR_API_KEY"]}

    def request(path):
        connection = http.client.HTTPConnection("127.0.0.1", args.port, timeout=15)
        started = time.monotonic()
        try:
            connection.request("GET", "/api/v1/" + path, headers=headers)
            response = connection.getresponse()
            data = response.read()
            return response.status, data, (time.monotonic() - started) * 1000
        finally:
            connection.close()

    status, data, _ = request("devices")
    if status != 200:
        raise RuntimeError(f"Device enumeration returned {status}")
    devices = [d for d in json.loads(data)["devices"] if d["capturing"]]
    if not devices:
        raise RuntimeError("No active captures")
    results, samples = [], []
    app_handle = None
    if args.app_pid:
        kernel = ctypes.WinDLL("kernel32", use_last_error=True)
        kernel.OpenProcess.restype = ctypes.c_void_p
        kernel.GetProcessTimes.argtypes = [ctypes.c_void_p] + [ctypes.c_void_p] * 4
        kernel.CloseHandle.argtypes = [ctypes.c_void_p]
        psapi = ctypes.WinDLL("psapi", use_last_error=True)
        psapi.GetProcessMemoryInfo.argtypes = [ctypes.c_void_p, ctypes.c_void_p, ctypes.c_uint32]
        class MemoryCounters(ctypes.Structure):
            _fields_ = [("cb", ctypes.c_uint32), ("faults", ctypes.c_uint32)] + [
                (name, ctypes.c_size_t) for name in ("peakWorkingSet", "workingSet", "peakPaged", "paged",
                    "peakNonPaged", "nonPaged", "pageFile", "peakPageFile", "private")]
        app_handle = kernel.OpenProcess(0x1000 | 0x10, False, args.app_pid)
    begin = time.monotonic()
    deadline = begin + args.seconds

    def capture(device, sequence):
        try:
            if args.abort_every and sequence % args.abort_every == 0:
                connection = http.client.HTTPConnection("127.0.0.1", args.port, timeout=15)
                try:
                    connection.request("GET", f"/api/v1/devices/{device['id']}/screenshot", headers=headers)
                finally:
                    connection.close()
                return {"status": "aborted", "valid": True, "device": device["id"]}
            status, png, elapsed = request(f"devices/{device['id']}/screenshot")
            valid = status != 200 or (png[:8] == b"\x89PNG\r\n\x1a\n" and len(png) >= 33)
            if status == 200 and valid:
                offset = 8
                while offset < len(png):
                    length = struct.unpack("!I", png[offset:offset + 4])[0]
                    end = offset + 8 + length
                    if end + 4 > len(png) or zlib.crc32(png[offset + 4:end]) != struct.unpack("!I", png[end:end + 4])[0]:
                        valid = False
                        break
                    offset = end + 4
                valid = valid and offset == len(png) and png[-8:-4] == b"IEND"
            size = struct.unpack("!II", png[16:24]) if status == 200 and valid else None
            code = None
            if status != 200:
                try: code = json.loads(png).get("error", {}).get("message")
                except (ValueError, AttributeError): pass
            return {"status": status, "ms": elapsed, "bytes": len(png), "valid": valid, "size": size, "error": code, "device": device["id"]}
        except Exception as error:
            return {"status": type(error).__name__, "valid": False}

    with concurrent.futures.ThreadPoolExecutor(max_workers=args.concurrency) as pool:
        pending = set()
        next_capture, next_status, index = begin, begin, 0
        while time.monotonic() < deadline:
            now = time.monotonic()
            if not args.idle and now >= next_capture:
                # Bound the client queue too: do not manufacture a retry storm.
                burst = len(devices) if args.burst else 1
                for _ in range(burst):
                    if len(pending) < args.concurrency:
                        pending.add(pool.submit(capture, devices[index % len(devices)], index + 1))
                        index += 1
                next_capture = now + burst / args.rate
            done = {f for f in pending if f.done()}
            results.extend(f.result() for f in done)
            pending -= done
            if now >= next_status:
                try:
                    code, state, latency = request("devices")
                    sample = {"at": now - begin, "status": code, "ms": latency}
                    if code == 200:
                        sample["devices"] = [{k: d[k] for k in ("id", "online", "capturing", "controlAvailable", "geometry")}
                                             for d in json.loads(state)["devices"]]
                    if app_handle:
                        times = [ctypes.c_uint64() for _ in range(4)]
                        if kernel.GetProcessTimes(app_handle, *(ctypes.byref(t) for t in times)):
                            sample["cpuSeconds"] = (times[2].value + times[3].value) / 10_000_000
                        memory = MemoryCounters()
                        memory.cb = ctypes.sizeof(memory)
                        if psapi.GetProcessMemoryInfo(app_handle, ctypes.byref(memory), memory.cb):
                            sample["privateMB"] = memory.private / 1048576
                            sample["workingSetMB"] = memory.workingSet / 1048576
                    samples.append(sample)
                except Exception as error:
                    samples.append({"at": now - begin, "error": type(error).__name__})
                next_status = now + 2
            time.sleep(0.002)
        results.extend(f.result() for f in pending)
    latencies = sorted(r["ms"] for r in results if "ms" in r)
    successes = sorted(r["ms"] for r in results if r["status"] == 200)
    expected_ids = {d["id"] for d in devices}
    controlled = {d["id"] for d in devices if d["controlAvailable"]}
    def unhealthy(sample):
        current = {d["id"]: d for d in sample.get("devices", [])}
        return (sample.get("status") != 200 or not expected_ids.issubset(current) or
                any(not current[i]["capturing"] or not current[i]["online"] for i in expected_ids) or
                any(not current[i]["controlAvailable"] for i in controlled))
    if app_handle:
        kernel.CloseHandle(app_handle)
    summary = {"seconds": time.monotonic() - begin, "rate": 0 if args.idle else args.rate,
               "startedAt": started_at, "endedAt": datetime.now(timezone.utc).isoformat(),
               "requests": len(results), "statuses": dict(Counter(str(r["status"]) for r in results)),
               "invalid": sum(not r["valid"] for r in results),
               "failedRequests": sum(r["status"] not in (200, 429, "aborted") for r in results),
               "unhealthySamples": sum(unhealthy(s) for s in samples),
               "errors": dict(Counter(r["error"] for r in results if r.get("error"))),
               "perDevice": {d["id"]: dict(Counter(str(r["status"]) for r in results if r.get("device") == d["id"])) for d in devices},
               "latencyMs": {"median": statistics.median(latencies) if latencies else None,
                             "p95": latencies[int((len(latencies) - 1) * .95)] if latencies else None,
                             "max": max(latencies, default=None)},
               "successLatencyMs": {"median": statistics.median(successes) if successes else None,
                                      "p95": successes[int((len(successes) - 1) * .95)] if successes else None,
                                      "max": max(successes, default=None)},
               "sizes": dict(Counter(str(r.get("size")) for r in results)), "samples": samples}
    destination = Path(args.output)
    destination.parent.mkdir(parents=True, exist_ok=True)
    destination.write_text(json.dumps(summary, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps({k: v for k, v in summary.items() if k != "samples"}, ensure_ascii=False))
    return int(summary["invalid"] != 0 or summary["failedRequests"] != 0 or summary["unhealthySamples"] != 0)


if __name__ == "__main__":
    raise SystemExit(main())

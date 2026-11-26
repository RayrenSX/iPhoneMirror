"""Measure idle discovery and concurrent UI status calls without starting receivers."""

import argparse
import ctypes
import json
import os
from pathlib import Path
import statistics
import threading
import time


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--core", type=Path, required=True)
    parser.add_argument("--iterations", type=int, default=20)
    parser.add_argument("--assert-status-budget-ms", type=float)
    args = parser.parse_args()
    if args.iterations < 2:
        parser.error("--iterations must be at least 2")
    path = args.core.resolve()
    with os.add_dll_directory(str(path.parent)):
        core = ctypes.CDLL(str(path))
        class EnvironmentInfo(ctypes.Structure):
            _fields_ = [("struct_size", ctypes.c_uint32), ("api_version", ctypes.c_uint32),
                ("service_installed", ctypes.c_int32), ("service_running", ctypes.c_int32),
                ("standard_mux", ctypes.c_int32), ("capture_mux", ctypes.c_int32),
                ("physical_devices", ctypes.c_uint32), ("diagnostic", ctypes.c_wchar * 512),
                ("libusb_available", ctypes.c_int32), ("usbdk_available", ctypes.c_int32),
                ("libusb_devices", ctypes.c_uint32), ("libusb_version", ctypes.c_wchar * 32),
                ("usbdk_known", ctypes.c_int32), ("libusb_devices_known", ctypes.c_int32)]

        core.im_initialize.argtypes = []
        core.im_initialize.restype = ctypes.c_int32
        core.im_shutdown.argtypes = []
        core.im_shutdown.restype = None
        core.im_get_environment.argtypes = [ctypes.POINTER(EnvironmentInfo)]
        core.im_get_environment.restype = ctypes.c_int32
        core.im_refresh_devices_ex.argtypes = [ctypes.c_void_p, ctypes.POINTER(ctypes.c_uint32), ctypes.c_int32]
        core.im_refresh_devices_ex.restype = ctypes.c_int32
        for name in ("im_media_cast_receiver_get_status", "im_wireless_receiver_get_status"):
            function = getattr(core, name)
            function.argtypes = [ctypes.POINTER(ctypes.c_int32), ctypes.POINTER(ctypes.c_int32)]
            function.restype = ctypes.c_int32

        def check(result):
            if result != 0:
                raise RuntimeError(f"Native API failed: {result}")

        def environment():
            info = EnvironmentInfo()
            info.struct_size = ctypes.sizeof(info)
            check(core.im_get_environment(ctypes.byref(info)))

        def inventory():
            count = ctypes.c_uint32()
            check(core.im_refresh_devices_ex(None, ctypes.byref(count), 0))
            return count.value

        def status():
            running, ready = ctypes.c_int32(), ctypes.c_int32()
            check(core.im_media_cast_receiver_get_status(ctypes.byref(running), ctypes.byref(ready)))
            check(core.im_wireless_receiver_get_status(ctypes.byref(running), ctypes.byref(ready)))

        def measure(operation):
            samples = []
            for _ in range(args.iterations):
                start = time.perf_counter()
                operation()
                samples.append((time.perf_counter() - start) * 1000)
            return {"median_ms": round(statistics.median(samples), 3),
                    "max_ms": round(max(samples), 3)}

        check(core.im_initialize())
        try:
            result = {"core": str(path), "iterations": args.iterations,
                      "wired_devices": inventory(),
                      "environment": measure(environment), "inventory": measure(inventory)}
            done = threading.Event()
            failures = []

            def discover():
                try:
                    for _ in range(args.iterations):
                        environment()
                except BaseException as error:
                    failures.append(error)
                finally:
                    done.set()

            worker = threading.Thread(target=discover)
            worker.start()
            latencies = []
            try:
                while not done.is_set():
                    start = time.perf_counter()
                    status()
                    latencies.append((time.perf_counter() - start) * 1000)
                    done.wait(0.005)
            finally:
                worker.join()
            if failures:
                raise failures[0]
            if not latencies:
                raise RuntimeError("No concurrent status samples were collected")
            result["concurrent_status"] = {"samples": len(latencies),
                "median_ms": round(statistics.median(latencies), 3),
                "max_ms": round(max(latencies), 3)}
            print(json.dumps(result, ensure_ascii=False, indent=2))
            if args.assert_status_budget_ms is not None and max(latencies) > args.assert_status_budget_ms:
                raise RuntimeError("Concurrent status exceeded the dispatcher blocking budget")
        finally:
            core.im_shutdown()


if __name__ == "__main__":
    main()

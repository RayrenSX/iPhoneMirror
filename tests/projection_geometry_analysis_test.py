"""Offline projection-log associations; no phone, app or network required."""

import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "scripts"))
from analyze_projection_geometry import analyze_files, analyze_lines


def line(body, thread=1, session="process-a"):
    metadata = f"[session={session}] " if session is not None else ""
    tid = f"[tid={thread}] " if thread is not None else ""
    return f"2026-11-17 19:11:23.150 {tid}{metadata}{body}\n"


def launch(pid, capability, mode="mirror", thread=1, session="process-a"):
    return line(f"wireless_hub host_started pid={pid} receiver_fp=anon-ab "
                f"capability={capability} mode={mode}", thread, session)


def startup(pid, capability, mode="mirror", thread=10, session="process-a"):
    return line(f"airplay_host: wireless_host startup pid={pid} parent_arg=1 "
                f"mode_arg={mode} capability={capability}", thread, session)


def geometry(size, thread=10, device="anon-aa", session="process-a"):
    return line(f"wireless_geometry device_fp={device} source={size} output={size}", thread, session)


def events(lines):
    return [event for session in analyze_lines(lines)["sessions"] for event in session["timeline"]]


class ProjectionGeometryAnalysisTests(unittest.TestCase):
    def test_encoded_cadence_distinguishes_receive_rate_and_sender_timestamps(self):
        timeline = events([
            startup(100, "2622x2622@60"),
            line("airplay_host: video_diag event=34 values=30,1000,500,0,200,10", 10),
            line("airplay_host: video_diag event=34 values=30,1000,1000,30,200,10", 10),
            line("airplay_host: video_diag event=34 values=60,1000,0,0,200,10", 10),
            line("airplay_host: video_diag event=34 values=30,0,1000,0,200,10", 10),
        ])
        cadence = [event for event in timeline if event["kind"] == "receive_cadence"]
        self.assertEqual([30.0, 30.0, 60.0], [event["receive_fps"] for event in cadence])
        self.assertEqual([60.0, 30.0, None], [event["sender_timestamp_fps"] for event in cadence])
        self.assertEqual([0, 30, 0], [event["sender_intervals_over_25_ms"] for event in cadence])
        self.assertTrue(all(event["receiver_mode"] == "mirror" for event in cadence))

    def test_accepted_codec_is_recorded_separately_from_decoded_geometry(self):
        timeline = events([
            startup(100, "2622x2622@60"),
            startup(200, "1920x1080@60", "media", thread=20),
            line("airplay_host: video_diag event=33 values=0,1,1206,2622,1206,2622", 10),
            line("airplay_host: video_diag event=33 values=0,1,2622,1206,2622,1206", 10),
            line("airplay_host: video_diag event=4 values=2622,1206,2624,0,0,0", 10),
            line("airplay_host: video_diag event=33 values=1,0,0,0,2160,994", 10),
            line("airplay_host: video_diag event=33 values=9,1,2160,994,2160,994", 10),
        ])
        negotiated = [event for event in timeline if event["kind"] == "codec_negotiation"]
        self.assertEqual(["avc", "avc", "hevc"], [event["codec"] for event in negotiated])
        self.assertEqual([True, True, False], [event["avc_diagnostic"] for event in negotiated])
        self.assertTrue(all(event["receiver_mode"] == "mirror" for event in negotiated))
        self.assertTrue(all(event["capability"]["width"] == 2622 for event in negotiated))
        self.assertEqual("landscape", negotiated[1]["geometry"]["source"]["orientation"])
        self.assertIsNone(negotiated[2]["geometry"]["source"])
        decoded = [event for event in timeline if event["kind"] == "decoded_geometry"]
        self.assertEqual(1, len(decoded))
        self.assertEqual(2622, decoded[0]["geometry"]["decoded"]["width"])

    def test_parallel_media_receiver_never_supplies_mirror_capability(self):
        timeline = events([
            launch(100, "2880x2880@60"), startup(100, "2880x2880@60"),
            launch(200, "1920x1080@60", "media"),
            startup(200, "1920x1080@60", "media", thread=20),
            geometry("994x2160"), geometry("2160x2160"),
            geometry("1920x1080", thread=20, device="anon-bb"),
        ])
        frames = [event for event in timeline if event["kind"] == "source_geometry"]
        self.assertEqual([2880, 2880, 1920], [event["capability"]["width"] for event in frames])
        self.assertEqual(["mirror", "mirror", "media"], [event["receiver_mode"] for event in frames])
        self.assertEqual(["receiver-1", "receiver-1", "receiver-2"], [event["receiver"] for event in frames])
        square = frames[1]["geometry"]["output"]
        self.assertEqual(("square", "unknown"), (square["shape"], square["orientation"]))
        self.assertEqual(2, len([event for event in timeline if event["kind"] == "receiver_capability"]))

    def test_frames_before_startup_remain_unknown_and_launcher_thread_is_unbound(self):
        timeline = events([
            geometry("994x2160"), launch(100, "2880x2880@60"),
            geometry("994x2160", thread=1), geometry("994x2160"),
            startup(100, "2880x2880@60"), geometry("2160x2160"),
        ])
        frames = [event for event in timeline if event["kind"] == "source_geometry"]
        self.assertEqual(["unknown", "unknown", "known"], [event["capability"]["status"] for event in frames])
        self.assertEqual([1, 3, 6], [event["evidence"]["line"] for event in frames])
        self.assertEqual(2, frames[0]["occurrences"])
        self.assertEqual(4, frames[0]["last_evidence"]["line"])

    def test_geometry_changes_keep_return_transition_and_deduplicate_repeats(self):
        timeline = events([
            startup(100, "2880x2880@60"),
            geometry("994x2160"), geometry("994x2160"),
            geometry("2160x2160"), geometry("2160x2160"), geometry("994x2160"),
        ])
        frames = [event for event in timeline if event["kind"] == "source_geometry"]
        self.assertEqual([994, 2160, 994], [event["geometry"]["source"]["width"] for event in frames])
        self.assertEqual([2, 2, 1], [event["occurrences"] for event in frames])

    def test_wired_output_never_inherits_any_wireless_capability(self):
        timeline = events([
            startup(100, "2880x2880@60"),
            line("video_output n=120 width=1082 height=2340", thread=10),
            line("usb_resolution device_fp=anon-cc panel=1080x2340 decoded=1082x2340", thread=10),
            line("video_output n=240 width=2340 height=1082", thread=10),
        ])
        wired = [event for event in timeline if event["transport"] == "wired"]
        self.assertEqual(3, len(wired))
        self.assertTrue(all(event["capability"] == {"status": "not_applicable"} for event in wired))
        self.assertEqual("unknown", wired[0]["device"])
        self.assertEqual(wired[1]["device"], wired[2]["device"])

    def test_sessions_files_and_reused_threads_are_isolated(self):
        report = analyze_lines([
            startup(100, "2880x2880@60"), geometry("2160x2160"),
            geometry("1920x884", session="process-b"),
        ])
        self.assertEqual(2, len(report["sessions"]))
        self.assertEqual("unknown", report["sessions"][1]["timeline"][0]["capability"]["status"])
        with tempfile.TemporaryDirectory() as directory:
            first, second = Path(directory) / "first.log", Path(directory) / "second.log"
            first.write_text(startup(100, "2880x2880@60"), encoding="utf-8")
            second.write_text(geometry("2160x2160"), encoding="utf-8")
            report = analyze_files([first, second])
            self.assertEqual("unknown", report["files"][1]["sessions"][0]["timeline"][0]["capability"]["status"])

    def test_receiver_restart_requires_new_startup_and_new_host_lifetime(self):
        timeline = events([
            launch(100, "2880x2880@60"), startup(100, "2880x2880@60"),
            geometry("2160x2160"), line("wireless_hub stopped"),
            launch(100, "1920x1080@60"), geometry("1920x884"),
            startup(100, "1920x1080@60"), geometry("1920x884"),
        ])
        frames = [event for event in timeline if event["kind"] == "source_geometry"]
        self.assertEqual(["receiver-1", "unknown", "receiver-2"], [event["receiver"] for event in frames])

    def test_pid_reuse_without_stop_revokes_only_previous_host_bindings(self):
        timeline = events([
            launch(100, "2880x2880@60"), startup(100, "2880x2880@60"),
            launch(200, "1920x1080@60", "media"),
            startup(200, "1920x1080@60", "media", thread=20),
            geometry("994x2160"), geometry("1920x1080", thread=20, device="anon-bb"),
            launch(100, "1920x1080@60"),
            geometry("2160x2160"), geometry("2160x2160", thread=None),
            geometry("1200x800", thread=20, device="anon-bb"),
            startup(100, "1920x1080@60"), geometry("1920x884"),
        ])
        frames = [event for event in timeline if event["kind"] == "source_geometry"]
        self.assertEqual(["receiver-1", "receiver-2", "unknown", "unknown", "receiver-2", "receiver-3"],
                         [event["receiver"] for event in frames])
        self.assertEqual("media", frames[-2]["receiver_mode"])
        self.assertEqual("known", frames[-2]["capability"]["status"])
        self.assertEqual(1920, frames[-1]["capability"]["width"])

    def test_device_only_lookup_is_forward_and_refuses_an_unbound_thread(self):
        timeline = events([
            startup(100, "2880x2880@60"), geometry("994x2160"),
            geometry("2160x2160", thread=None),
            geometry("2160x2160", thread=30),
        ])
        frames = [event for event in timeline if event["kind"] == "source_geometry"]
        self.assertEqual(["host_thread", "device_fingerprint", "unknown"], [event["association"] for event in frames])
        self.assertEqual("unknown", frames[-1]["capability"]["status"])

    def test_decoder_and_resolution_events_keep_only_structured_dimensions(self):
        timeline = events([
            startup(100, "2880x2880@60"),
            line("airplay_host: video_diag event=4 values=2160,2160,2176,0,0,0", 10),
            line("wireless_resolution model=iPhone18,3 panel=1206x2622 decoded=2160x2160 orientation=portrait", 10),
            line("wireless_video published device_fp=anon-aa size=2160x2160 bytes=6998400", 10),
            line("airplay_host: video callback index=3 size=2160x2160 remote=192.168.1.99 device_id=SECRET", 10),
            line("other source=1x2 output=2x1 device_id=SECRET remote=192.168.1.99", 10),
        ])
        decoded = [event for event in timeline if event["kind"] == "decoded_geometry"]
        self.assertEqual(4, len(decoded))
        self.assertTrue(all(event["geometry"]["decoded"]["orientation"] == "unknown" for event in decoded))
        output = json.dumps(timeline)
        for private in ("SECRET", "192.168.1.99", "anon-aa", "process-a", "iPhone18,3"):
            self.assertNotIn(private, output)

    def test_unknown_session_boundaries_and_invalid_dimensions(self):
        report = analyze_lines([
            startup(100, "2880x2880@60", session=None), geometry("2160x2160", session=None),
            "=== iPhoneMirror capture session ===\n", geometry("2160x2160", session=None),
            geometry("0x0", session=None), geometry("999999x1", session=None),
        ])
        self.assertEqual(2, len(report["sessions"]))
        self.assertTrue(all(session["session_identity"] == "unknown" for session in report["sessions"]))
        second = report["sessions"][1]["timeline"]
        self.assertEqual(1, len(second))
        self.assertEqual("unknown", second[0]["capability"]["status"])

    def test_missing_session_never_inherits_previous_process_and_marker_resets_state(self):
        report = analyze_lines([
            startup(100, "2880x2880@60"),
            geometry("2160x2160", session=None),
            "=== iPhoneMirror capture session ===\n",
            geometry("2160x2160"),
        ])
        self.assertEqual(3, len(report["sessions"]))
        for session in report["sessions"][1:]:
            self.assertEqual("unknown", session["timeline"][0]["capability"]["status"])

    def test_device_seen_on_two_hosts_has_no_unambiguous_threadless_capability(self):
        timeline = events([
            startup(100, "2880x2880@60"), geometry("994x2160"),
            startup(200, "1920x1080@60", "media", thread=20),
            geometry("1920x884", thread=20), geometry("2160x2160", thread=None),
        ])
        self.assertEqual("unknown", timeline[-1]["capability"]["status"])
        self.assertEqual("unknown", timeline[-1]["association"])

    def test_cli_output_and_input_overwrite_protection(self):
        with tempfile.TemporaryDirectory() as directory:
            source, output = Path(directory) / "capture.log", Path(directory) / "report.json"
            text = startup(100, "2880x2880@60") + geometry("2160x2160")
            source.write_text(text, encoding="utf-8")
            command = [sys.executable, str(ROOT / "scripts/analyze_projection_geometry.py"), str(source)]
            result = subprocess.run(command + ["--output", str(output)], capture_output=True, text=True)
            self.assertEqual(0, result.returncode, result.stderr)
            report = json.loads(output.read_text(encoding="utf-8"))
            self.assertEqual(str(source), report["files"][0]["sessions"][0]["timeline"][0]["evidence"]["file"])
            self.assertEqual("", result.stdout)
            result = subprocess.run(command + ["--output", str(source)], capture_output=True, text=True)
            self.assertNotEqual(0, result.returncode)
            self.assertEqual(text, source.read_text(encoding="utf-8"))


if __name__ == "__main__":
    unittest.main()

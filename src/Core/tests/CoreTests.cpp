#include "Audio/WasapiRenderer.h"
#include "Media/CoreMedia.h"
#include "Media/H264.h"
#include "Media/MediaFoundationDecoder.h"
#include "Protocol/Plist.h"
#include "Protocol/QuickTimePacket.h"
#include "Protocol/QuickTimeSession.h"
#include "Transport/AppleUsbIdentityCache.h"
#include "Transport/LibUsb0Transport.h"
#include "Transport/LibUsb0Readiness.h"
#include "Transport/QtUsbTransport.h"
#include "Capture/CaptureSession.h"
#include "Capture/WirelessCaptureSession.h"
#include "Device/AppleUsbDiscovery.h"
#include "Logging.h"
#include "IpcProtocol.h"
#include "iPhoneMirror/CoreApi.h"

#include <Windows.h>

#include <algorithm>
#include <atomic>
#include <chrono>
#include <cmath>
#include <cstdint>
#include <exception>
#include <filesystem>
#include <fstream>
#include <future>
#include <iostream>
#include <limits>
#include <memory>
#include <span>
#include <stdexcept>
#include <string>
#include <thread>
#include <vector>

namespace {

int failures{};

std::vector<std::uint8_t> read_fixture(const char* path) {
    std::ifstream stream(path, std::ios::binary);
    if (!stream) throw std::runtime_error(std::string("cannot open fixture: ") + path);
    return {std::istreambuf_iterator<char>(stream), std::istreambuf_iterator<char>()};
}

iPhoneMirror::quicktime::Packet decode_framed(const std::vector<std::uint8_t>& bytes) {
    iPhoneMirror::quicktime::StreamDecoder decoder;
    const auto packets = decoder.push(bytes);
    if (packets.size() != 1) throw std::runtime_error("fixture does not contain exactly one framed packet");
    return packets.front();
}

void check(bool condition, const char* message) {
    if (!condition) {
        ++failures;
        std::cerr << "FAIL: " << message << '\n';
    }
}

bool contains_ascii(const std::vector<std::uint8_t>& bytes, std::string_view value) {
    return std::search(bytes.begin(), bytes.end(), value.begin(), value.end()) != bytes.end();
}

std::vector<std::uint8_t> initial_hpd1(iPhoneMirror::quicktime::SessionOptions options) {
    using namespace iPhoneMirror::quicktime;
    SessionProtocol session(options);
    (void)session.process(decode_framed(make_ping()));
    const auto event = session.process(decode_framed(
        read_fixture("fixtures/quicktime_video_hack/cwpa-request1")));
    if (event.outbound.empty()) throw std::runtime_error("CWPA did not produce HPD1");
    return event.outbound.front();
}

template <typename Function>
void check_throws(Function&& function, const char* message) {
    try {
        function();
        check(false, message);
    } catch (...) {
    }
}

void test_plist() {
    const std::string xml = R"(<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
  <key>DeviceList</key><array><dict>
    <key>DeviceID</key><integer>7</integer>
    <key>Properties</key><dict>
      <key>SerialNumber</key><string>00008120-A&amp;B</string>
      <key>ConnectionType</key><string>USB</string>
    </dict>
  </dict></array>
  <key>Enabled</key><true/>
</dict></plist>)";
    const auto root = iPhoneMirror::plist::parse_xml(xml);
    check(root.type == iPhoneMirror::plist::Type::Dictionary, "plist root dictionary");
    check(root.find("Enabled") && root.find("Enabled")->bool_or(), "plist boolean");
    const auto* devices = root.find("DeviceList");
    check(devices && devices->array.size() == 1, "plist array");
    const auto* properties = devices->array.front().find("Properties");
    check(properties && properties->find("SerialNumber")->string_or() == "00008120-A&B", "plist XML entity");

    const auto round_trip = iPhoneMirror::plist::parse_xml(iPhoneMirror::plist::to_xml(root));
    check(round_trip.find("Enabled")->bool_or(), "plist serialization round trip");

    check_throws([] {
        (void)iPhoneMirror::plist::parse_xml("<integer/>");
    }, "empty plist integer rejected");
    check_throws([] {
        (void)iPhoneMirror::plist::parse_xml("<real></real>");
    }, "empty plist real rejected");
    check_throws([] {
        (void)iPhoneMirror::plist::parse_xml("<string>&#xD800;</string>");
    }, "surrogate XML entity rejected");
    check_throws([] {
        (void)iPhoneMirror::plist::parse_xml("<string>&#x110000;</string>");
    }, "out-of-range XML entity rejected");
}

void test_quicktime_framing() {
    const auto ping = iPhoneMirror::quicktime::make_ping();
    const std::vector<std::uint8_t> captured_ping{
        0x10, 0x00, 0x00, 0x00, 0x67, 0x6e, 0x69, 0x70,
        0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00,
    };
    check(ping == captured_ping, "PING matches reference wire bytes");
    iPhoneMirror::quicktime::StreamDecoder decoder;
    auto packets = decoder.push(std::span(ping).first(3));
    check(packets.empty() && decoder.buffered_bytes() == 3, "fragmented header retained");
    packets = decoder.push(std::span(ping).subspan(3, 5));
    check(packets.empty(), "fragmented payload retained");
    packets = decoder.push(std::span(ping).subspan(8));
    check(packets.size() == 1, "fragmented ping assembled");
    check(packets.front().kind == iPhoneMirror::quicktime::PacketKind::Ping, "ping classified");

    const std::uint64_t clock = 0x0102030405060708ULL;
    const auto need = iPhoneMirror::quicktime::make_need(clock);
    check(need[4] == 0x6e && need[5] == 0x79 && need[6] == 0x73 && need[7] == 0x61,
        "ASYN uses reference wire byte order");
    check(need[16] == 0x64 && need[17] == 0x65 && need[18] == 0x65 && need[19] == 0x6e,
        "NEED uses reference wire byte order");
    packets = decoder.push(need);
    check(packets.size() == 1 && packets.front().subtype == iPhoneMirror::quicktime::fourcc('n', 'e', 'e', 'd'), "NEED subtype");
    check(packets.front().clock_ref == clock, "NEED clock reference");

    const std::vector<std::uint8_t> invalid{1, 0, 0, 0};
    check_throws([&] { (void)decoder.push(invalid); }, "invalid QuickTime length rejected");
}

void test_h264() {
    const std::vector<std::uint8_t> avcc{
        0, 0, 0, 3, 0x65, 0xaa, 0xbb,
        0, 0, 0, 2, 0x41, 0xcc,
    };
    const auto annex_b = iPhoneMirror::h264::avcc_to_annex_b(avcc);
    const std::vector<std::uint8_t> expected{
        0, 0, 0, 1, 0x65, 0xaa, 0xbb,
        0, 0, 0, 1, 0x41, 0xcc,
    };
    check(annex_b == expected, "AVCC converted to Annex-B");
    check(iPhoneMirror::h264::is_keyframe_avcc(avcc), "IDR detected as keyframe");
    check_throws([] {
        const std::vector<std::uint8_t> truncated{0, 0, 0, 8, 0x65};
        (void)iPhoneMirror::h264::avcc_to_annex_b(truncated);
    }, "truncated AVCC rejected");
}

void test_coremedia() {
    std::vector<std::uint8_t> time(24);
    time[0] = 0xe8; time[1] = 0x03; // value = 1000
    time[8] = 0xe8; time[9] = 0x03; // timescale = 1000
    time[12] = 1;                  // valid flag
    const auto parsed = iPhoneMirror::coremedia::parse_time(time);
    check(parsed.valid() && std::abs(parsed.seconds() - 1.0) < 0.0001, "CMTime parsed");
    check(parsed.to_100ns() && *parsed.to_100ns() == 10'000'000,
        "CMTime converts to 100ns units without floating-point rounding");
    using iPhoneMirror::coremedia::CMTime;
    check(CMTime{.value = -1500, .timescale = 1000, .flags = 1}.to_100ns() == -15'000'000,
        "negative CMTime conversion preserves truncation semantics");
    check(CMTime{.value = std::numeric_limits<std::int64_t>::max(),
            .timescale = 10'000'000, .flags = 1}.to_100ns() ==
            std::numeric_limits<std::int64_t>::max() &&
        CMTime{.value = std::numeric_limits<std::int64_t>::min(),
            .timescale = 10'000'000, .flags = 1}.to_100ns() ==
            std::numeric_limits<std::int64_t>::min(),
        "CMTime conversion handles exact int64 boundaries");
    check(!CMTime{.value = std::numeric_limits<std::int64_t>::max(),
            .timescale = 1, .flags = 1}.to_100ns() &&
        !CMTime{.value = 1, .timescale = -1, .flags = 1}.valid() &&
        !CMTime{.value = 1, .timescale = 1, .flags = 5}.valid(),
        "CMTime rejects overflow, negative timescales, and implied infinity flags");

    std::vector<std::uint8_t> sample;
    const auto append32 = [&sample](std::uint32_t value) {
        for (int shift = 0; shift < 32; shift += 8) sample.push_back(static_cast<std::uint8_t>(value >> shift));
    };
    const auto append64 = [&append32](std::uint64_t value) {
        append32(static_cast<std::uint32_t>(value));
        append32(static_cast<std::uint32_t>(value >> 32U));
    };
    append32(iPhoneMirror::quicktime::fourcc('a', 's', 'y', 'n'));
    append64(42);
    append32(iPhoneMirror::quicktime::fourcc('f', 'e', 'e', 'd'));
    append32(12); // includes this length field; 8 bytes follow after it
    append32(iPhoneMirror::quicktime::fourcc('s', 'b', 'u', 'f'));
    append32(0x11223344);
    const auto envelope = iPhoneMirror::coremedia::parse_sample_envelope(sample);
    check(envelope.video && envelope.clock_ref == 42, "FEED envelope parsed");
    check(envelope.serialized_sample_buffer.size() == 8, "sbuf span length");

    const auto append32_to = [](std::vector<std::uint8_t>& target, std::uint32_t value) {
        for (int shift = 0; shift < 32; shift += 8) target.push_back(static_cast<std::uint8_t>(value >> shift));
    };
    const auto chunk = [&append32_to](std::uint32_t magic, const std::vector<std::uint8_t>& payload) {
        std::vector<std::uint8_t> result;
        append32_to(result, static_cast<std::uint32_t>(8 + payload.size()));
        append32_to(result, magic);
        result.insert(result.end(), payload.begin(), payload.end());
        return result;
    };
    const auto append = [](std::vector<std::uint8_t>& target, const std::vector<std::uint8_t>& value) {
        target.insert(target.end(), value.begin(), value.end());
    };

    std::vector<std::uint8_t> format_payload;
    std::vector<std::uint8_t> media_type;
    append32_to(media_type, iPhoneMirror::quicktime::fourcc('v', 'i', 'd', 'e'));
    append(format_payload, chunk(iPhoneMirror::quicktime::fourcc('m', 'd', 'i', 'a'), media_type));
    std::vector<std::uint8_t> dimensions;
    append32_to(dimensions, 1920); append32_to(dimensions, 1080);
    append(format_payload, chunk(iPhoneMirror::quicktime::fourcc('v', 'd', 'i', 'm'), dimensions));
    std::vector<std::uint8_t> codec;
    append32_to(codec, iPhoneMirror::quicktime::fourcc('a', 'v', 'c', '1'));
    append(format_payload, chunk(iPhoneMirror::quicktime::fourcc('c', 'o', 'd', 'c'), codec));
    std::vector<std::uint8_t> extensions;
    append32_to(extensions, iPhoneMirror::quicktime::fourcc('d', 'a', 't', 'v'));
    const std::vector<std::uint8_t> avcc{1, 100, 0, 40, 0xff, 0xe1, 0, 2, 0x67, 0x64, 1, 0, 2, 0x68, 0xee};
    append(extensions, avcc);
    append(format_payload, chunk(iPhoneMirror::quicktime::fourcc('e', 'x', 't', 'n'), extensions));

    std::vector<std::uint8_t> serialized;
    append32_to(serialized, iPhoneMirror::quicktime::fourcc('s', 'b', 'u', 'f'));
    std::vector<std::uint8_t> count;
    append32_to(count, 1);
    append(serialized, chunk(iPhoneMirror::quicktime::fourcc('n', 's', 'm', 'p'), count));
    const std::vector<std::uint8_t> encoded{0, 0, 0, 2, 0x65, 0xaa};
    append(serialized, chunk(iPhoneMirror::quicktime::fourcc('s', 'd', 'a', 't'), encoded));
    append(serialized, chunk(iPhoneMirror::quicktime::fourcc('f', 'd', 's', 'c'), format_payload));

    const auto parsed_sample = iPhoneMirror::coremedia::parse_sample_buffer(serialized);
    check(parsed_sample.sample_count == 1 && parsed_sample.sample_data == encoded, "CMSampleBuffer payload parsed");
    check(parsed_sample.format && parsed_sample.format->width == 1920 && parsed_sample.format->height == 1080,
        "video format dimensions parsed");
    check(parsed_sample.format && parsed_sample.format->nalu_length_size == 4,
        "AVCC NAL length size parsed");
    check(parsed_sample.format && parsed_sample.format->sequence_parameter_sets.size() == 1 &&
        parsed_sample.format->picture_parameter_sets.size() == 1, "AVCC SPS/PPS parsed");

    std::vector<std::uint8_t> hevc_format_payload;
    append(hevc_format_payload, chunk(iPhoneMirror::quicktime::fourcc('m', 'd', 'i', 'a'), media_type));
    append(hevc_format_payload, chunk(iPhoneMirror::quicktime::fourcc('v', 'd', 'i', 'm'), dimensions));
    std::vector<std::uint8_t> hevc_codec;
    append32_to(hevc_codec, iPhoneMirror::quicktime::fourcc('h', 'v', 'c', '1'));
    append(hevc_format_payload, chunk(iPhoneMirror::quicktime::fourcc('c', 'o', 'd', 'c'), hevc_codec));
    std::vector<std::uint8_t> hvcc{
        1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 120,
        0xf0, 0, 0xfc, 0xfd, 0xfa, 0xfa, 0, 0, 0xff, 3,
    };
    const auto append_hevc_array = [&hvcc](std::uint8_t type,
        std::initializer_list<std::uint8_t> nalu) {
        hvcc.push_back(static_cast<std::uint8_t>(0x80U | type));
        hvcc.insert(hvcc.end(), {0, 1, 0, static_cast<std::uint8_t>(nalu.size())});
        hvcc.insert(hvcc.end(), nalu);
    };
    append_hevc_array(32, {0x40, 0x01});
    append_hevc_array(33, {0x42, 0x01});
    append_hevc_array(34, {0x44, 0x01});
    std::vector<std::uint8_t> hevc_extensions;
    append32_to(hevc_extensions, iPhoneMirror::quicktime::fourcc('d', 'a', 't', 'v'));
    append(hevc_extensions, hvcc);
    append(hevc_format_payload,
        chunk(iPhoneMirror::quicktime::fourcc('e', 'x', 't', 'n'), hevc_extensions));
    std::vector<std::uint8_t> hevc_serialized;
    append32_to(hevc_serialized, iPhoneMirror::quicktime::fourcc('s', 'b', 'u', 'f'));
    const std::vector<std::uint8_t> hevc_idr{0, 0, 0, 2, 0x26, 0x01};
    append(hevc_serialized,
        chunk(iPhoneMirror::quicktime::fourcc('s', 'd', 'a', 't'), hevc_idr));
    append(hevc_serialized,
        chunk(iPhoneMirror::quicktime::fourcc('f', 'd', 's', 'c'), hevc_format_payload));
    const auto parsed_hevc = iPhoneMirror::coremedia::parse_sample_buffer(hevc_serialized);
    check(parsed_hevc.format &&
        parsed_hevc.format->video_codec() == iPhoneMirror::coremedia::VideoCodec::Hevc &&
        parsed_hevc.format->bit_depth_luma == 10 &&
        parsed_hevc.format->video_parameter_sets.size() == 1 &&
        parsed_hevc.format->sequence_parameter_sets.size() == 1 &&
        parsed_hevc.format->picture_parameter_sets.size() == 1,
        "hvcC parses HEVC VPS/SPS/PPS and 10-bit format metadata");
    check(parsed_hevc.format && iPhoneMirror::media::detail::is_random_access_sample(
        *parsed_hevc.format, hevc_idr), "HEVC IDR is recognized as a random-access sample");
}

void test_upstream_capture_fixtures() {
    iPhoneMirror::quicktime::StreamDecoder decoder;
    const auto feed_bytes = read_fixture("fixtures/quicktime_video_hack/asyn-feed");
    const auto feed_packets = decoder.push(feed_bytes);
    check(feed_packets.size() == 1 && feed_packets.front().is_video_sample(), "upstream FEED frame classified");
    const auto feed_envelope = iPhoneMirror::coremedia::parse_sample_envelope(feed_packets.front().payload);
    const auto video = iPhoneMirror::coremedia::parse_sample_buffer(feed_envelope.serialized_sample_buffer);
    check(video.sample_count == 1 && video.sample_data.size() == 90750, "upstream H264 sample extracted");
    check(video.format && video.format->width == 1126 && video.format->height == 2436, "upstream video dimensions extracted");
    check(video.format && video.format->codec == iPhoneMirror::quicktime::fourcc('a', 'v', 'c', '1'), "upstream AVC1 codec extracted");
    check(video.format && !video.format->sequence_parameter_sets.empty() && !video.format->picture_parameter_sets.empty(),
        "upstream SPS/PPS extracted");
    check(iPhoneMirror::h264::is_keyframe_avcc(video.sample_data,
        video.format ? video.format->nalu_length_size : 4), "upstream FEED contains IDR frame");

    decoder.reset();
    const auto eat_bytes = read_fixture("fixtures/quicktime_video_hack/asyn-eat");
    // This upstream fixture intentionally omits the outer 4-byte USB length,
    // unlike asyn-feed. parse_payload accepts the post-framing representation.
    const auto eat_packet = iPhoneMirror::quicktime::parse_payload(eat_bytes);
    check(eat_packet.is_audio_sample(), "upstream EAT frame classified");
    const auto eat_envelope = iPhoneMirror::coremedia::parse_sample_envelope(eat_packet.payload);
    const auto audio = iPhoneMirror::coremedia::parse_sample_buffer(eat_envelope.serialized_sample_buffer);
    check(audio.sample_count == 1024 && audio.sample_data.size() == 4096, "upstream PCM sample extracted");
    check(audio.format && audio.format->audio && audio.format->audio->sample_rate == 48000.0 &&
        audio.format->audio->channels_per_frame == 2 && audio.format->audio->bits_per_channel == 16,
        "upstream 48 kHz stereo PCM format extracted");

    decoder.reset();
    const auto afmt_bytes = read_fixture("fixtures/quicktime_video_hack/afmt-request");
    const auto afmt_packets = decoder.push(afmt_bytes);
    check(afmt_packets.size() == 1 && afmt_packets.front().kind == iPhoneMirror::quicktime::PacketKind::Sync &&
        afmt_packets.front().subtype == iPhoneMirror::quicktime::fourcc('a', 'f', 'm', 't'),
        "upstream AFMT frame classified");
    const auto afmt = iPhoneMirror::coremedia::parse_audio_format(
        std::span(afmt_packets.front().payload).subspan(24));
    check(afmt.sample_rate == 48000.0 && afmt.channels_per_frame == 2 && afmt.bits_per_channel == 16,
        "upstream AFMT ASBD parsed");

    decoder.reset();
    const auto cvrp_bytes = read_fixture("fixtures/quicktime_video_hack/cvrp-request");
    const auto cvrp_packets = decoder.push(cvrp_bytes);
    check(cvrp_packets.size() == 1 && cvrp_packets.front().kind == iPhoneMirror::quicktime::PacketKind::Sync &&
        cvrp_packets.front().subtype == iPhoneMirror::quicktime::fourcc('c', 'v', 'r', 'p'),
        "upstream CVRP frame classified");
}

void test_session_protocol() {
    using namespace iPhoneMirror::quicktime;
    SessionProtocol session(SessionOptions{.requested_width = 1920, .requested_height = 1080});

    auto event = session.process(decode_framed(make_ping()));
    check(event.state == SessionState::WaitingForAudioClock && event.outbound.size() == 1 &&
        event.outbound.front() == make_ping(), "session replies to PING");

    const auto cwpa = decode_framed(read_fixture("fixtures/quicktime_video_hack/cwpa-request1"));
    event = session.process(cwpa);
    check(event.state == SessionState::Negotiating && event.outbound.size() == 3,
        "CWPA produces HPD1, clock reply and HPA1");
    check(decode_framed(event.outbound[0]).subtype == fourcc('h', 'p', 'd', '1'), "CWPA sends HPD1");
    check(decode_framed(event.outbound[1]).kind == PacketKind::Reply, "CWPA sends clock RPLY");
    check(decode_framed(event.outbound[2]).subtype == fourcc('h', 'p', 'a', '1'), "CWPA sends HPA1");

    const auto afmt = decode_framed(read_fixture("fixtures/quicktime_video_hack/afmt-request"));
    event = session.process(afmt);
    check(event.outbound.size() == 1 && session.negotiated_audio() &&
        session.negotiated_audio()->sample_rate == 48000.0, "AFMT negotiated and acknowledged");
    check(event.outbound.front() == read_fixture("fixtures/quicktime_video_hack/afmt-reply"),
        "AFMT reply matches upstream golden packet");

    const auto cvrp = decode_framed(read_fixture("fixtures/quicktime_video_hack/cvrp-request"));
    event = session.process(cvrp);
    check(event.outbound.size() == 2 && decode_framed(event.outbound[0]).subtype == fourcc('n', 'e', 'e', 'd') &&
        decode_framed(event.outbound[1]).kind == PacketKind::Reply, "CVRP produces NEED and clock reply");

    event = session.process(decode_framed(read_fixture("fixtures/quicktime_video_hack/clok-request")));
    check(event.outbound.size() == 1 && decode_framed(event.outbound[0]).kind == PacketKind::Reply,
        "CLOK acknowledged");
    event = session.process(decode_framed(read_fixture("fixtures/quicktime_video_hack/time-request1")));
    check(event.outbound.size() == 1 && event.outbound[0].size() == 44, "TIME returns CMTime");
    event = session.process(decode_framed(read_fixture("fixtures/quicktime_video_hack/skew-request")));
    check(event.outbound.size() == 1 && event.outbound[0].size() == 28, "SKEW returns clock rate");
    event = session.process(decode_framed(read_fixture("fixtures/quicktime_video_hack/og-request")));
    check(event.outbound.size() == 1 && event.outbound[0].size() == 24, "OG acknowledged");

    event = session.process(decode_framed(read_fixture("fixtures/quicktime_video_hack/asyn-feed")));
    check(event.state == SessionState::Streaming && event.video_sample && event.outbound.size() == 1,
        "FEED emits video and replenishes NEED");
    const auto eat_bytes = read_fixture("fixtures/quicktime_video_hack/asyn-eat");
    event = session.process(parse_payload(eat_bytes));
    check(event.audio_sample && session.audio_packets() == 1, "EAT emits PCM sample");

    const auto stop = session.stop_messages();
    check(stop.size() == 2 && decode_framed(stop[0]).subtype == fourcc('h', 'p', 'a', '0') &&
        decode_framed(stop[1]).subtype == fourcc('h', 'p', 'd', '0'), "session emits HPA0 and HPD0");
    event = session.process(decode_framed(read_fixture(
        "fixtures/quicktime_video_hack/asyn-feed")));
    check(event.state == SessionState::Stopping && event.outbound.empty(),
        "late video packets cannot roll teardown back to streaming or request more frames");
    const auto stop_complete = session.complete_stop_messages();
    check(stop_complete.size() == 1 &&
        decode_framed(stop_complete.front()).subtype == fourcc('h', 'p', 'd', '0') &&
        session.state() == SessionState::Stopped,
        "session emits the final HPD0 after release notifications");
}

void test_usb_projection_modes() {
    using namespace iPhoneMirror::capture;

    const auto demo = make_usb_display_configuration(
        UsbProjectionMode::Demo, 1206, 2622);
    check(demo.session_options.demo_mode, "demo mode enables Valeria");
    check(!demo.session_options.request_native_display_size,
        "demo mode includes a native DisplaySize to start video");
    check(demo.session_options.requested_width == 1206 &&
        demo.session_options.requested_height == 2622,
        "demo mode requests native portrait dimensions");
    check(!demo.adaptive_reconfiguration,
        "demo mode does not run AirPlay display reconfiguration");
    const auto demo_hpd1 = initial_hpd1(demo.session_options);
    check(contains_ascii(demo_hpd1, "Valeria"), "demo HPD1 contains Valeria");
    check(contains_ascii(demo_hpd1, "DisplaySize"),
        "demo HPD1 contains the native DisplaySize required for video");

    const auto airplay = make_usb_display_configuration(
        UsbProjectionMode::AirPlay, 1206, 2622);
    check(!airplay.session_options.demo_mode, "AirPlay mode disables Valeria");
    check(airplay.session_options.requested_width == 1206 &&
        airplay.session_options.requested_height == 2622,
        "AirPlay mode requests native portrait dimensions");
    check(airplay.adaptive_reconfiguration,
        "AirPlay mode enables adaptive display reconfiguration");
    check(contains_ascii(initial_hpd1(airplay.session_options), "DisplaySize"),
        "AirPlay HPD1 contains DisplaySize");

    const auto custom_airplay = make_usb_display_configuration(
        UsbProjectionMode::AirPlay, 1206, 2622, 1920, 1080);
    check(custom_airplay.session_options.requested_width == 1920 &&
        custom_airplay.session_options.requested_height == 1080,
        "AirPlay mode preserves advanced custom dimensions");

    const auto aisi = make_usb_display_configuration(
        UsbProjectionMode::Aisi, 1206, 2622, 1920, 1080);
    check(!aisi.session_options.demo_mode, "Aisi mode disables Valeria");
    check(aisi.session_options.requested_width == 1565 &&
        aisi.session_options.requested_height == 1565,
        "Aisi mode uses its fixed square display target");
    check(!aisi.adaptive_reconfiguration,
        "Aisi mode keeps the fixed target during orientation changes");
}

void test_libusb_runtime() {
    class CountingProbeSource final : public iPhoneMirror::transport::UsbRuntimeProbeSource {
    public:
        void read_user_mode_metadata(
            iPhoneMirror::transport::UsbRuntimeProbe& probe) override {
            ++metadata_calls;
            probe.runtime_available = true;
            probe.usbdk_helper_installed = true;
            probe.version = "test";
        }

        void probe_usb_backends(
            iPhoneMirror::transport::UsbRuntimeProbe& probe) override {
            ++backend_calls;
            probe.usbdk_backend_probed = true;
            probe.usbdk_backend_available = true;
            probe.apple_device_count_probed = true;
            probe.apple_device_count = 1;
        }

        int metadata_calls{};
        int backend_calls{};
    };

    const bool legacy_runtime_was_loaded =
        GetModuleHandleW(L"libusb0.dll") != nullptr;
    CountingProbeSource source;
    const auto automatic = iPhoneMirror::transport::probe_usb_runtime(source);
    check(source.metadata_calls == 1,
        "automatic runtime probe reads user-mode metadata once");
    check(source.backend_calls == 0,
        "automatic runtime probe does not enter USB backends");
    check(!automatic.usbdk_backend_probed &&
        !automatic.apple_device_count_probed,
        "automatic runtime probe reports backend state as unknown");

    const auto explicit_probe =
        iPhoneMirror::transport::probe_usb_runtime(source, true);
    check(source.metadata_calls == 2 && source.backend_calls == 1,
        "explicit runtime probe enters USB backends exactly once");
    check(explicit_probe.usbdk_backend_probed &&
        explicit_probe.usbdk_backend_available &&
        explicit_probe.apple_device_count_probed &&
        explicit_probe.apple_device_count == 1,
        "explicit runtime probe returns backend results");

    const auto probe = iPhoneMirror::transport::probe_usb_runtime();
    check(probe.runtime_available, "libusb runtime loads");
    check(probe.version.starts_with("1.0.29"), "libusb runtime version is 1.0.29");
    check(!probe.usbdk_backend_probed && !probe.apple_device_count_probed,
        "system runtime metadata leaves USB backend state unknown");
    if (!legacy_runtime_was_loaded) {
        check(GetModuleHandleW(L"libusb0.dll") == nullptr,
            "automatic runtime metadata does not load the legacy USB runtime");
    }
}

void test_apple_usb_serial_matching() {
    using iPhoneMirror::transport::apple_usb_serial_equal;
    check(apple_usb_serial_equal(
        "000081010000000000000001", "00008101-0000000000000001"),
        "24-character USB serial matches 25-character usbmux UDID");
    check(apple_usb_serial_equal(
        "00008150-0000000000000002", "00008150-0000000000000002"),
        "Apple serial matching is case-insensitive");
    check(!apple_usb_serial_equal(
        "000081010000000000000001", "00008150-0000000000000002"),
        "different Apple serials do not match");

    std::string padded_serial = "00008103000E74501104A01E";
    padded_serial.resize(40, '\0');
    check(apple_usb_serial_equal(
        padded_serial, "00008103-000E74501104A01E"),
        "USB descriptors padded with NUL code points match usbmux UDIDs");
    check(!apple_usb_serial_equal(
        std::string("00008103000E74501104A01E\0unexpected", 35),
        "00008103-000E74501104A01E"),
        "non-padding bytes after an embedded NUL are rejected");
    check(!apple_usb_serial_equal({}, {}) &&
        !apple_usb_serial_equal("   ", "\t"),
        "empty normalized USB serials never identify a device");
}

void test_apple_usb_filter_safety() {
    using iPhoneMirror::device::is_unsafe_apple_usb_filter_combination;
    using iPhoneMirror::device::apple_usb_parent_instance_matches_serial;
    using iPhoneMirror::device::libusb0_apple_interface_path_matches;
    using iPhoneMirror::device::AppleNormalUsbStackEvidence;
    using iPhoneMirror::device::is_complete_apple_normal_usb_stack;
    const std::vector<std::wstring> libusb0{L"libusb0"};
    const std::vector<std::wstring> libusb0_mixed_case{L"LiBuSb0"};
    const std::vector<std::wstring> apple_lower{L"AppleLowerFilter"};
    const std::vector<std::wstring> apple_kmdf{L"AppleKmdfFilter"};
    const std::vector<std::wstring> apple_lower_mixed_case{L"aPpLeLoWeRfIlTeR"};
    const std::vector<std::wstring> no_filters;

    check(is_complete_apple_normal_usb_stack({true, true, true}),
        "normal Apple USB recovery requires the parent and both essential interfaces");
    check(!is_complete_apple_normal_usb_stack({true, false, true}) &&
            !is_complete_apple_normal_usb_stack({true, true, false}) &&
            !is_complete_apple_normal_usb_stack({false, true, true}),
        "a partial WPD, management, or parent recovery is never reported as normal");

    check(is_unsafe_apple_usb_filter_combination(libusb0, apple_lower),
        "libusb0 plus AppleLowerFilter is diagnosed as a high-risk stack");
    check(is_unsafe_apple_usb_filter_combination(libusb0, apple_kmdf),
        "libusb0 plus AppleKmdfFilter is diagnosed as a high-risk stack");
    check(is_unsafe_apple_usb_filter_combination(
            libusb0_mixed_case, apple_lower_mixed_case),
        "Apple USB filter safety matching is case-insensitive");
    check(!is_unsafe_apple_usb_filter_combination(libusb0, no_filters),
        "libusb0 without an Apple lower filter is not this unsafe combination");
    check(!is_unsafe_apple_usb_filter_combination(no_filters, apple_lower),
        "an Apple lower filter without libusb0 is not this unsafe combination");

    check(apple_usb_parent_instance_matches_serial(
            L"USB\\VID_05AC&PID_12A8\\0000810100044D600A22001E",
            "00008101-00044D600A22001E"),
        "Apple USB parent identity matches normalized exact serial forms");
    check(!apple_usb_parent_instance_matches_serial(
            L"USB\\VID_05AC&PID_12A8\\00008150001903580A9B401C",
            "00008101-00044D600A22001E") &&
        !apple_usb_parent_instance_matches_serial(
            L"USB\\VID_05AC&PID_12A8&MI_01\\0000810100044D600A22001E",
            "00008101-00044D600A22001E"),
        "Apple USB parent identity rejects another phone and interface children");

    constexpr auto interface_path =
        LR"(\\?\USB#VID_05AC&PID_12A8#0000810100044D600A22001E#{f9f3ff14-ae21-48a0-8a25-8011a7a931d9})";
    check(libusb0_apple_interface_path_matches(interface_path, 0x12a8,
            "00008101-00044D600A22001E"),
        "libusb0 interface readiness matches the exact Apple VID, PID and serial");
    check(!libusb0_apple_interface_path_matches(interface_path, 0x12ab,
            "00008101-00044D600A22001E") &&
        !libusb0_apple_interface_path_matches(interface_path, 0x12a8,
            "00008150-001903580A9B401C") &&
        !libusb0_apple_interface_path_matches(
            LR"(\\?\USB#VID_05AC&PID_12A8#0000810100044D600A22001E#{a5dcbf10-6530-11d2-901f-00c04fb951ed})",
            0x12a8, "00008101-00044D600A22001E"),
        "libusb0 readiness rejects another product, phone and generic USB interface");
}

void test_active_apple_usb_identity_cache() {
    using namespace iPhoneMirror::transport;
    const AppleUsbIdentity identity{
        .serial = "00008101-00044D600A22001E",
        .topology_id = "test-active-usb:3:2",
    };
    check(cached_active_apple_usb_serial(identity.topology_id).empty(),
        "active USB identity cache started with a stale entry");
    check(retain_active_apple_usb_identity(identity),
        "active USB identity cache rejected the first owner");
    check(apple_usb_serial_equal(
            cached_active_apple_usb_serial(identity.topology_id), identity.serial),
        "active USB identity cache did not return the retained serial");
    check(retain_active_apple_usb_identity(identity),
        "active USB identity cache rejected a matching second owner");

    auto conflicting = identity;
    conflicting.serial = "00008101-0000000000000000";
    check(!retain_active_apple_usb_identity(conflicting),
        "active USB identity cache accepted a conflicting serial");

    release_active_apple_usb_identity(identity.topology_id, identity.serial);
    check(!cached_active_apple_usb_serial(identity.topology_id).empty(),
        "active USB identity cache dropped a shared owner too early");
    release_active_apple_usb_identity(identity.topology_id, identity.serial);
    check(cached_active_apple_usb_serial(identity.topology_id).empty(),
        "active USB identity cache retained a released owner");

}

void test_apple_usb_reenumeration_selection() {
    using namespace iPhoneMirror::transport;

    AppleUsbDevice initial;
    initial.vendor_id = 0x05ac;
    initial.product_id = 0x12ab;
    initial.serial = "00008103000E74501104A01E";
    initial.topology_id = "3:2.4";
    initial.can_open = true;
    initial.configuration_count = 5;
    initial.highest_configuration_value = 5;
    const auto identity = make_apple_usb_identity(initial);
    check(identity.expected_quicktime_configuration == 6 &&
        identity.original_product_id == 0x12ab,
        "modern iPad identity derives appended QuickTime configuration 6");
    check(apple_usb_candidate_in_scope("3:9.9", identity),
        "an exact serial keeps re-enumerated candidates whose topology key changed");
    auto topology_only_identity = identity;
    topology_only_identity.serial.clear();
    check(apple_usb_candidate_in_scope(initial.topology_id,
            topology_only_identity) &&
        !apple_usb_candidate_in_scope("3:9.9", topology_only_identity),
        "a topology-only identity never expands to an unrelated physical device");

    AppleUsbDevice descriptor_unavailable;
    descriptor_unavailable.serial = initial.serial;
    const auto incomplete_identity = make_apple_usb_identity(descriptor_unavailable);
    check(incomplete_identity.expected_quicktime_configuration == 0,
        "missing descriptors do not invent a conventional QuickTime configuration");

    AppleUsbDevice other;
    other.vendor_id = 0x05ac;
    other.product_id = 0x12ab;
    other.serial = "000081010000000000000001";
    other.topology_id = "3:2.3";
    other.can_open = true;
    other.quicktime_configuration = true;
    other.quicktime_endpoints = {
        .configuration = 5, .interface_number = 2,
        .bulk_in = 0x86, .bulk_out = 0x05,
        .bulk_in_packet_size = 512, .bulk_out_packet_size = 512,
    };

    AppleUsbDevice reenumerated = initial;
    reenumerated.product_id = 0x12cd; // PID changes are not device identity.
    reenumerated.address = 19;
    reenumerated.serial.resize(40, '\0');
    reenumerated.configuration_count = 6;
    reenumerated.highest_configuration_value = 6;
    reenumerated.quicktime_configuration = true;
    reenumerated.quicktime_endpoints = {
        .configuration = 6, .interface_number = 4, .alternate_setting = 1,
        .bulk_in = 0x87, .bulk_out = 0x06,
        .bulk_in_packet_size = 1024, .bulk_out_packet_size = 1024,
    };

    const std::vector exact_candidates{other, reenumerated};
    const auto exact = select_apple_usb_device(exact_candidates, identity, true);
    check(exact.index && *exact.index == 1 &&
        exact.match_kind == AppleUsbMatchKind::Serial && !exact.ambiguous,
        "re-enumeration selects the exact iPad despite PID/address changes");

    auto same_model_other = reenumerated;
    same_model_other.serial = other.serial;
    same_model_other.topology_id = other.topology_id;
    same_model_other.address = 20;
    const std::vector same_model_candidates{same_model_other, reenumerated};
    const auto same_model = select_apple_usb_device(same_model_candidates,
        identity, true);
    check(same_model.index && *same_model.index == 1 &&
        same_model.match_kind == AppleUsbMatchKind::Serial,
        "same-model devices with the same PID remain isolated by serial");

    auto serial_temporarily_unreadable = reenumerated;
    serial_temporarily_unreadable.serial.clear();
    const std::vector topology_candidates{other, serial_temporarily_unreadable};
    const auto topology = select_apple_usb_device(topology_candidates,
        identity, true);
    check(topology.index && *topology.index == 1 &&
        topology.match_kind == AppleUsbMatchKind::Topology,
        "unique physical port safely bridges a temporarily unreadable serial");

    auto known_other_device = reenumerated;
    known_other_device.serial = other.serial;
    const std::vector known_other_candidates{known_other_device};
    const auto known_other = select_apple_usb_device(known_other_candidates,
        identity, true);
    check(!known_other.index && known_other.topology_matches == 0,
        "physical-port fallback rejects a known different device serial");

    auto ambiguous = serial_temporarily_unreadable;
    const std::vector ambiguous_candidates{serial_temporarily_unreadable, ambiguous};
    const auto ambiguous_selection = select_apple_usb_device(
        ambiguous_candidates, identity, true);
    check(!ambiguous_selection.index && ambiguous_selection.ambiguous,
        "ambiguous physical matches never cross-bind concurrent devices");

    auto same_pid_wrong_device = other;
    same_pid_wrong_device.topology_id = "3:2.8";
    const std::vector wrong_candidates{same_pid_wrong_device};
    check(!select_apple_usb_device(wrong_candidates, identity, true).index,
        "VID/PID equality alone never selects another connected device");

    const std::vector endpoint_candidates{
        other.quicktime_endpoints, reenumerated.quicktime_endpoints};
    const auto endpoints = select_best_quicktime_endpoints(endpoint_candidates);
    check(endpoints.configuration == 6 && endpoints.interface_number == 4 &&
        endpoints.alternate_setting == 1 && endpoints.bulk_in == 0x87 &&
        endpoints.bulk_out == 0x06,
        "QuickTime endpoint selection supports config 6 and alternate settings");
    const auto conventional = conventional_quicktime_endpoints(identity);
    check(conventional.configuration == 6 &&
        conventional.interface_number == 2 && conventional.bulk_in == 0x86 &&
        conventional.bulk_out == 0x05,
        "stale-descriptor fallback uses the derived appended configuration");

    const auto diagnostic = describe_apple_usb_candidates(exact_candidates,
        identity);
    check(diagnostic.find("configs=6/6") != std::string::npos &&
        diagnostic.find("serial_match=true") != std::string::npos,
        "USB selection diagnostics record descriptor and match decisions");

    auto active_reenumerated = reenumerated;
    active_reenumerated.active_configuration =
        active_reenumerated.quicktime_endpoints.configuration;
    active_reenumerated.active_configuration_known = true;
    check(can_initialize_libusb0_quicktime_configuration(
            AppleUsbMatchKind::Serial, active_reenumerated,
            active_reenumerated.quicktime_endpoints),
        "libusb0 configuration initialization accepts an exact serial and descriptor-backed interface");
    check(!can_initialize_libusb0_quicktime_configuration(
            AppleUsbMatchKind::Topology, active_reenumerated,
            active_reenumerated.quicktime_endpoints),
        "libusb0 configuration initialization rejects topology-only matches");
    check(!can_initialize_libusb0_quicktime_configuration(
            AppleUsbMatchKind::None, active_reenumerated,
            active_reenumerated.quicktime_endpoints),
        "libusb0 configuration initialization rejects an unverified match");
    auto inactive_reenumerated = active_reenumerated;
    inactive_reenumerated.active_configuration = 4;
    check(!can_initialize_libusb0_quicktime_configuration(
            AppleUsbMatchKind::Serial, inactive_reenumerated,
            inactive_reenumerated.quicktime_endpoints),
        "libusb0 configuration initialization rejects a retained descriptor in a normal active configuration");
    auto invented_endpoints = active_reenumerated.quicktime_endpoints;
    invented_endpoints.configuration = 5;
    check(!can_initialize_libusb0_quicktime_configuration(
            AppleUsbMatchKind::Serial, active_reenumerated, invented_endpoints),
        "libusb0 configuration initialization rejects endpoints not present in the selected descriptor");
    invented_endpoints = active_reenumerated.quicktime_endpoints;
    invented_endpoints.bulk_in ^= 0x01;
    check(!can_initialize_libusb0_quicktime_configuration(
            AppleUsbMatchKind::Serial, active_reenumerated, invented_endpoints),
        "libusb0 configuration initialization rejects descriptor-mismatched bulk endpoints");
    check(is_libusb0_invalid_configuration_claim(-22,
            "libusb0-dll:err [claim_interface] could not claim interface 2, invalid configuration 0"),
        "libusb0 invalid-configuration claim is narrowly recognized");
    check(!is_libusb0_invalid_configuration_claim(-22,
            "libusb0-dll:err [claim_interface] could not claim interface 2, busy"),
        "libusb0 busy claim does not authorize reconfiguration");
    check(!is_libusb0_invalid_configuration_claim(-16,
            "libusb0-dll:err [claim_interface] could not claim interface 2, invalid configuration 0"),
        "other libusb0 claim errors do not authorize reconfiguration");
    const std::array<std::uint8_t, 32> quicktime_descriptor{
        9, 2, 32, 0, 1, 5, 0, 0x80, 50,
        9, 4, 2, 0, 2, 0xff, 0x2a, 0, 0,
        7, 5, 0x81, 2, 0x00, 0x02, 0,
        7, 5, 0x02, 2, 0x00, 0x02, 0,
    };
    const auto parsed_quicktime = parse_libusb0_quicktime_configuration(
        quicktime_descriptor, 5);
    check(parsed_quicktime.configuration == 5 &&
        parsed_quicktime.interface_number == 2 &&
        parsed_quicktime.bulk_in == 0x81 &&
        parsed_quicktime.bulk_out == 0x02,
        "single-handle readiness parses a descriptor-backed QuickTime interface");
    auto truncated_quicktime = quicktime_descriptor;
    truncated_quicktime[18] = 64;
    check(parse_libusb0_quicktime_configuration(
            truncated_quicktime, 5).configuration == 0 &&
        parse_libusb0_quicktime_configuration(
            quicktime_descriptor, 6).configuration == 0,
        "single-handle readiness rejects truncated and unexpected configurations");
    auto active_quicktime = reenumerated;
    active_quicktime.active_configuration =
        active_quicktime.quicktime_endpoints.configuration;
    active_quicktime.active_configuration_known = true;
    check(is_libusb0_quicktime_configuration_active(active_quicktime),
        "matching active and descriptor configurations identify a reusable QuickTime node");
    active_quicktime.active_configuration = 4;
    check(!is_libusb0_quicktime_configuration_active(active_quicktime),
        "a retained QuickTime descriptor does not make the normal active configuration reusable");
    active_quicktime.active_configuration = 0;
    check(!is_libusb0_quicktime_configuration_active(active_quicktime),
        "an unknown active configuration is never assumed to be QuickTime");
    active_quicktime.active_configuration =
        active_quicktime.quicktime_endpoints.configuration;
    active_quicktime.active_configuration_known = false;
    check(!is_libusb0_quicktime_configuration_active(active_quicktime),
        "a configuration value without a successful GET_CONFIGURATION is not trusted");
}

void test_media_foundation_decoder() {
    const auto packet = decode_framed(read_fixture("fixtures/quicktime_video_hack/asyn-feed"));
    const auto envelope = iPhoneMirror::coremedia::parse_sample_envelope(packet.payload);
    const auto sample = iPhoneMirror::coremedia::parse_sample_buffer(envelope.serialized_sample_buffer);
    check(sample.format.has_value(), "decoder fixture has video format");
    if (!sample.format) return;
    iPhoneMirror::media::MediaFoundationH264Decoder decoder;
    auto oversized = *sample.format;
    oversized.width = iPhoneMirror::media::detail::MaxDecodedVideoDimension + 1;
    check_throws([&] { decoder.configure(oversized, 60, 1); },
        "Media Foundation rejects oversized H264 dimensions before allocation");
    decoder.configure(*sample.format, 60, 1);
    check(!decoder.selected_decoder_name().empty() &&
        decoder.output_pixel_format() == iPhoneMirror::media::PixelFormat::Nv12,
        "decoder reports the selected MFT and negotiated output format");
    std::vector<iPhoneMirror::media::DecodedFrame> frames;
    for (int index = 0; index < 8 && frames.empty(); ++index) {
        auto decoded = decoder.decode(sample.sample_data, static_cast<std::int64_t>(index) * 166667, 166667);
        for (auto& frame : decoded) frames.push_back(std::move(frame));
    }
    if (frames.empty()) {
        auto drained = decoder.drain();
        for (auto& frame : drained) frames.push_back(std::move(frame));
    }
    check(!frames.empty(), "Media Foundation decodes captured H264 IDR");
    if (!frames.empty()) {
        check(frames.back().width == 1126 && frames.back().height == 2436, "decoded NV12 dimensions match format");
        check(!frames.back().nv12.empty(), "decoded NV12 frame contains pixels");
    }

    iPhoneMirror::media::MediaFoundationH264Decoder software_decoder(
        iPhoneMirror::media::DecoderPreference::SoftwareCompatible);
    software_decoder.configure(*sample.format, 60, 1);
    check(!software_decoder.selected_decoder_is_hardware(),
        "software-compatible H264 policy explicitly selects CPU decoding");
    std::vector<iPhoneMirror::media::DecodedFrame> software_frames;
    for (int index = 0; index < 8 && software_frames.empty(); ++index) {
        auto decoded = software_decoder.decode(sample.sample_data,
            static_cast<std::int64_t>(index) * 166667, 166667);
        for (auto& frame : decoded) software_frames.push_back(std::move(frame));
    }
    if (software_frames.empty()) {
        auto drained = software_decoder.drain();
        for (auto& frame : drained) software_frames.push_back(std::move(frame));
    }
    check(!software_frames.empty(),
        "software-compatible Media Foundation decoder still decodes captured H264");
}

void test_decoder_switch_transaction() {
    using iPhoneMirror::capture::detail::DecoderPreferenceState;
    using iPhoneMirror::capture::detail::DecoderPreferenceUpdate;
    using iPhoneMirror::capture::detail::DecoderSwitchCoordinator;
    using iPhoneMirror::capture::detail::trial_and_commit_decoder;
    using iPhoneMirror::capture::DecoderRuntimeMode;
    using iPhoneMirror::capture::DecoderSwitchPhase;
    using iPhoneMirror::media::DecoderPreference;

    DecoderSwitchCoordinator coordinator(DecoderPreference::Auto);
    const auto initial_status = coordinator.status();
    check(initial_status.phase == DecoderSwitchPhase::Applied &&
        initial_status.requested == DecoderPreference::Auto &&
        initial_status.applied == DecoderPreference::Auto &&
        initial_status.requested_generation == initial_status.applied_generation &&
        initial_status.runtime_mode == DecoderRuntimeMode::Unknown,
        "decoder switch status starts applied without inventing an acceleration mode");
    auto active_decoder = std::make_unique<int>(1);
    const auto hardware_request = coordinator.request(
        DecoderPreference::HardwarePreferred);
    const auto pending_status = coordinator.status();
    check(pending_status.phase == DecoderSwitchPhase::Pending &&
        pending_status.requested == DecoderPreference::HardwarePreferred &&
        pending_status.applied == DecoderPreference::Auto &&
        pending_status.requested_generation > pending_status.applied_generation,
        "decoder request remains pending until its trial decode commits");
    bool trial_completed{};
    bool commit_observed_trial{};
    std::vector<int> accepted_output{7};
    auto replacement = std::make_unique<int>(2);
    const bool empty_output_committed = trial_and_commit_decoder(
        coordinator, hardware_request.current, replacement,
        [&](auto& candidate) {
            trial_completed = candidate && *candidate == 2;
            return std::vector<int>{};
        },
        [&](std::unique_ptr<int>&& accepted_decoder,
            std::vector<int>&& trial_output) noexcept {
            commit_observed_trial = trial_completed;
            active_decoder.swap(accepted_decoder);
            accepted_output.swap(trial_output);
        }, DecoderRuntimeMode::Hardware);
    check(empty_output_committed && commit_observed_trial &&
        active_decoder && *active_decoder == 2 && replacement &&
        *replacement == 1 && accepted_output.empty(),
        "decoder switch commits only after a successful trial decode, including empty output");
    const auto hardware_status = coordinator.status();
    check(hardware_status.phase == DecoderSwitchPhase::Applied &&
        hardware_status.applied == DecoderPreference::HardwarePreferred &&
        hardware_status.runtime_mode == DecoderRuntimeMode::Hardware,
        "committed decoder status exposes the applied policy and actual engine");

    const auto software_request = coordinator.request(
        DecoderPreference::SoftwareCompatible);
    replacement = std::make_unique<int>(3);
    bool failed_candidate_committed{};
    bool trial_threw{};
    try {
        (void)trial_and_commit_decoder(
            coordinator, software_request.current, replacement,
            [](auto&) -> std::vector<int> {
                throw std::runtime_error("synthetic decoder rejection");
            },
            [&](auto&&, auto&&) noexcept {
                failed_candidate_committed = true;
            });
    } catch (const std::runtime_error&) {
        trial_threw = true;
    }
    check(trial_threw && !failed_candidate_committed && active_decoder &&
        *active_decoder == 2 && replacement && *replacement == 3,
        "failed candidate trial decode retains the known-good decoder");
    check(coordinator.mark_failed_if_current(software_request.current),
        "current decoder rejection records a failed generation");
    const auto failed_status = coordinator.status();
    check(failed_status.phase == DecoderSwitchPhase::Failed &&
        failed_status.requested == DecoderPreference::SoftwareCompatible &&
        failed_status.applied == DecoderPreference::HardwarePreferred &&
        failed_status.runtime_mode == DecoderRuntimeMode::Hardware,
        "failed decoder status keeps reporting the known-good active engine");

    const auto stale_request = coordinator.request(DecoderPreference::Auto);
    replacement = std::make_unique<int>(4);
    DecoderPreferenceUpdate superseding_request;
    const bool stale_candidate_committed = trial_and_commit_decoder(
        coordinator, stale_request.current, replacement,
        [&](auto&) {
            superseding_request = coordinator.request(
                DecoderPreference::HardwarePreferred);
            return std::vector<int>{11};
        },
        [&](auto&&, auto&&) noexcept {
            failed_candidate_committed = true;
        });
    check(!stale_candidate_committed && superseding_request.changed &&
        coordinator.requested() == superseding_request.current &&
        active_decoder && *active_decoder == 2 && replacement &&
        *replacement == 4,
        "candidate decoded for a superseded generation cannot replace the active decoder");
    check(!coordinator.mark_failed_if_current(stale_request.current) &&
        coordinator.status().phase == DecoderSwitchPhase::Pending,
        "a stale decoder failure cannot poison the superseding request status");

    DecoderSwitchCoordinator synchronized(DecoderPreference::Auto);
    const DecoderPreferenceState expected = synchronized.request(
        DecoderPreference::HardwarePreferred).current;
    std::promise<void> commit_entered_promise;
    auto commit_entered = commit_entered_promise.get_future();
    std::promise<void> release_commit_promise;
    auto release_commit = release_commit_promise.get_future();
    std::promise<void> setter_finished_promise;
    auto setter_finished = setter_finished_promise.get_future();
    bool commit_succeeded{};
    DecoderPreferenceUpdate concurrent_update;
    std::jthread commit_thread([&] {
        commit_succeeded = synchronized.commit_if_current(expected, [&] {
            commit_entered_promise.set_value();
            release_commit.wait();
        });
    });
    if (commit_entered.wait_for(std::chrono::seconds(1)) !=
        std::future_status::ready) {
        check(false, "decoder commit transaction enters its synchronized boundary");
        release_commit_promise.set_value();
        commit_thread.join();
        return;
    }
    std::jthread setter_thread([&] {
        concurrent_update = synchronized.request(
            DecoderPreference::SoftwareCompatible);
        setter_finished_promise.set_value();
    });
    check(setter_finished.wait_for(std::chrono::milliseconds(50)) ==
        std::future_status::timeout,
        "decoder preference publication cannot cross the final recheck and swap boundary");
    release_commit_promise.set_value();
    commit_thread.join();
    setter_thread.join();
    check(commit_succeeded && concurrent_update.changed &&
        concurrent_update.previous == expected &&
        synchronized.requested() == concurrent_update.current,
        "a request arriving during commit is published as the next generation");
}

void test_wireless_decoder_status() {
    using iPhoneMirror::capture::DecoderRuntimeMode;
    using iPhoneMirror::capture::DecoderSwitchPhase;
    using iPhoneMirror::capture::WirelessCaptureSession;
    using iPhoneMirror::media::DecoderPreference;

    WirelessCaptureSession session(nullptr, L"test-wireless-device", {});
    session.set_decoder_preference(DecoderPreference::HardwarePreferred);
    const auto status = session.decoder_switch_status();
    check(status.phase == DecoderSwitchPhase::Applied &&
        status.requested == DecoderPreference::HardwarePreferred &&
        status.applied == DecoderPreference::HardwarePreferred &&
        status.runtime_mode == DecoderRuntimeMode::External,
        "wireless session reports the external receiver decoder instead of detecting forever");
}

void test_capture_media_safety_helpers() {
    using iPhoneMirror::capture::detail::StreamingSilenceWatchdog;
    using iPhoneMirror::capture::detail::VideoQueueAction;
    using iPhoneMirror::capture::detail::VideoQueueBudget;

    const auto odd_nv12 = iPhoneMirror::media::detail::checked_nv12_buffer_size(3, 3);
    check(odd_nv12 && *odd_nv12 == 20,
        "checked NV12 size uses even stride and rounded chroma height");
    const auto maximum_nv12 = iPhoneMirror::media::detail::checked_nv12_buffer_size(
        iPhoneMirror::media::detail::MaxDecodedVideoDimension,
        iPhoneMirror::media::detail::MaxDecodedVideoDimension);
    check(maximum_nv12 && *maximum_nv12 == 100663296,
        "maximum supported NV12 dimensions have a checked 64-bit size");
    check(!iPhoneMirror::media::detail::checked_nv12_buffer_size(0, 1080) &&
        !iPhoneMirror::media::detail::checked_nv12_buffer_size(8193, 1080) &&
        !iPhoneMirror::media::detail::checked_nv12_buffer_size(UINT32_MAX, UINT32_MAX),
        "checked NV12 size rejects zero, oversized, and overflowing dimensions");
    const auto odd_p010 = iPhoneMirror::media::detail::checked_video_buffer_size(
        3, 3, iPhoneMirror::media::PixelFormat::P010);
    check(odd_p010 && *odd_p010 == 40,
        "checked P010 size uses two-byte components and an even chroma stride");
    const auto maximum_p010 = iPhoneMirror::media::detail::checked_video_buffer_size(
        iPhoneMirror::media::detail::MaxDecodedVideoDimension,
        iPhoneMirror::media::detail::MaxDecodedVideoDimension,
        iPhoneMirror::media::PixelFormat::P010);
    check(maximum_p010 && *maximum_p010 == 201326592,
        "maximum supported P010 allocation remains bounded");

    using iPhoneMirror::media::detail::DecoderAcceleration;
    check(iPhoneMirror::media::detail::classify_dxva_mode(0) ==
            DecoderAcceleration::Unknown &&
        iPhoneMirror::media::detail::classify_dxva_mode(1) ==
            DecoderAcceleration::Software &&
        iPhoneMirror::media::detail::classify_dxva_mode(2) ==
            DecoderAcceleration::Hardware &&
        iPhoneMirror::media::detail::classify_dxva_mode(3) ==
            DecoderAcceleration::Hardware &&
        iPhoneMirror::media::detail::classify_dxva_mode(4) ==
            DecoderAcceleration::Hardware &&
        iPhoneMirror::media::detail::classify_dxva_mode(5) ==
            DecoderAcceleration::Unknown,
        "DXVA mode classification only resolves documented SW/MC/IDCT/VLD values");

    const auto odd_dxgi_nv12 =
        iPhoneMirror::media::detail::checked_dxgi_readback_layout(
            3, 3, 3, 3, 1, 1, 0, 1, 4,
            iPhoneMirror::media::PixelFormat::Nv12);
    check(odd_dxgi_nv12 && odd_dxgi_nv12->minimum_row_pitch == 4 &&
            odd_dxgi_nv12->row_count == 5 && odd_dxgi_nv12->total_bytes == 20,
        "DXGI NV12 layout rounds odd luma width and chroma height safely");
    const auto padded_dxgi_p010 =
        iPhoneMirror::media::detail::checked_dxgi_readback_layout(
            3, 3, 4, 4, 2, 3, 4, 1, 8,
            iPhoneMirror::media::PixelFormat::P010);
    check(padded_dxgi_p010 && padded_dxgi_p010->minimum_row_pitch == 8 &&
            padded_dxgi_p010->row_count == 6 &&
            padded_dxgi_p010->total_bytes == 48,
        "DXGI P010 layout accepts a mip-zero array slice and padded allocation");
    check(!iPhoneMirror::media::detail::checked_dxgi_readback_layout(
            3, 3, 4, 4, 2, 3, 3, 1, 8,
            iPhoneMirror::media::PixelFormat::P010) &&
        !iPhoneMirror::media::detail::checked_dxgi_readback_layout(
            3, 3, 4, 4, 1, 1, 0, 2, 8,
            iPhoneMirror::media::PixelFormat::P010) &&
        !iPhoneMirror::media::detail::checked_dxgi_readback_layout(
            3, 3, 516, 4, 1, 1, 0, 1, 1032,
            iPhoneMirror::media::PixelFormat::P010) &&
        !iPhoneMirror::media::detail::checked_dxgi_readback_layout(
            3, 3, 4, 4, 1, 1, 0, 1, 6,
            iPhoneMirror::media::PixelFormat::P010),
        "DXGI readback rejects nonzero mips, multisampling, excess padding, and short rows");
    const auto near_limit_dxgi =
        iPhoneMirror::media::detail::checked_dxgi_readback_layout(
            8192, 8192, 8192, 8192, 1, 1, 0, 1, 21844,
            iPhoneMirror::media::PixelFormat::P010);
    check(near_limit_dxgi &&
            near_limit_dxgi->total_bytes <=
                iPhoneMirror::media::detail::MaxDxgiReadbackBytes &&
        !iPhoneMirror::media::detail::checked_dxgi_readback_layout(
            8192, 8192, 8192, 8192, 1, 1, 0, 1, 21846,
            iPhoneMirror::media::PixelFormat::P010),
        "DXGI readback enforces the 256 MiB mapped allocation ceiling");

    iPhoneMirror::coremedia::VideoColorDescription sdr_color{
        .primaries = iPhoneMirror::coremedia::ColorPrimaries::Bt709,
        .transfer = iPhoneMirror::coremedia::TransferFunction::Bt709,
        .matrix = iPhoneMirror::coremedia::MatrixCoefficients::Bt709,
        .range = iPhoneMirror::coremedia::ColorRange::Limited,
    };
    const auto p010_black = iPhoneMirror::media::detail::convert_yuv_to_sdr(
        static_cast<double>(64U << 6U) / 65535.0,
        static_cast<double>(512U << 6U) / 65535.0,
        static_cast<double>(512U << 6U) / 65535.0,
        sdr_color, iPhoneMirror::media::PixelFormat::P010);
    const auto p010_white = iPhoneMirror::media::detail::convert_yuv_to_sdr(
        static_cast<double>(940U << 6U) / 65535.0,
        static_cast<double>(512U << 6U) / 65535.0,
        static_cast<double>(512U << 6U) / 65535.0,
        sdr_color, iPhoneMirror::media::PixelFormat::P010);
    check(p010_black.red < 0.001 && p010_black.green < 0.001 &&
        p010_black.blue < 0.001 && p010_white.red > 0.999 &&
        p010_white.green > 0.999 && p010_white.blue > 0.999,
        "P010 limited-range conversion preserves reference black and white");

    auto hdr_color = sdr_color;
    hdr_color.primaries = iPhoneMirror::coremedia::ColorPrimaries::Bt2020;
    hdr_color.transfer = iPhoneMirror::coremedia::TransferFunction::Pq;
    hdr_color.matrix = iPhoneMirror::coremedia::MatrixCoefficients::Bt2020;
    hdr_color.range = iPhoneMirror::coremedia::ColorRange::Full;
    hdr_color.hdr.max_mastering_luminance = 1000;
    const auto tone_mapped = iPhoneMirror::media::detail::convert_yuv_to_sdr(
        0.75, static_cast<double>(512U << 6U) / 65535.0,
        static_cast<double>(512U << 6U) / 65535.0,
        hdr_color, iPhoneMirror::media::PixelFormat::P010);
    check(std::isfinite(tone_mapped.red) && tone_mapped.red > 0.8 &&
        tone_mapped.red <= 1.0 && tone_mapped.green > 0.8 &&
        tone_mapped.blue > 0.8,
        "PQ BT.2020 conversion deterministically tone-maps HDR into SDR gamut");
    check(iPhoneMirror::media::decoder_preference_name(
        iPhoneMirror::media::DecoderPreference::HardwarePreferred) ==
        "hardware_preferred", "decoder policy values have stable diagnostics");

    iPhoneMirror::coremedia::AudioStreamBasicDescription audio_format{
        .sample_rate = 48000,
        .format_id = 0x6c70636dU,
        .format_flags = 1U << 2U,
        .bytes_per_packet = 4,
        .frames_per_packet = 1,
        .bytes_per_frame = 4,
        .channels_per_frame = 2,
        .bits_per_channel = 16,
    };
    const auto audio_layout =
        iPhoneMirror::audio::detail::checked_wasapi_buffer_layout(audio_format);
    check(audio_layout && audio_layout->block_align == 4 &&
        audio_layout->capacity_frames == 8192 &&
        audio_layout->capacity_bytes == 32768,
        "WASAPI validates PCM before computing its bounded ring layout");
    const auto audio_capacity = audio_layout ? audio_layout->capacity_frames : 0;

    const auto queue_1024 = iPhoneMirror::audio::detail::wasapi_queue_thresholds(
        1024, audio_capacity, 1056);
    const auto queue_2048 = iPhoneMirror::audio::detail::wasapi_queue_thresholds(
        2048, audio_capacity, 1056);
    const auto queue_4096 = iPhoneMirror::audio::detail::wasapi_queue_thresholds(
        4096, audio_capacity, 1056);
    check(queue_1024.startup_frames == 3072 &&
        queue_1024.high_water_frames == 4096 &&
        queue_2048.startup_frames == 3104 &&
        queue_2048.high_water_frames == 5152 &&
        queue_4096.startup_frames == 5152 &&
        queue_4096.high_water_frames == 8192,
        "WASAPI jitter thresholds adapt to the observed PCM packet and endpoint sizes");

    const auto queue_4096_before_endpoint =
        iPhoneMirror::audio::detail::wasapi_queue_thresholds(
            4096, audio_capacity);
    const auto first_large_packet =
        iPhoneMirror::audio::detail::plan_wasapi_enqueue(
            0, 4096, audio_capacity,
            queue_4096_before_endpoint);
    const auto second_large_packet =
        iPhoneMirror::audio::detail::plan_wasapi_enqueue(
            4096, 4096, audio_capacity,
            queue_4096_before_endpoint);
    check(first_large_packet.drop_existing_frames == 0 &&
        first_large_packet.final_frames == 4096 &&
        second_large_packet.drop_existing_frames == 0 &&
        second_large_packet.final_frames == 8192,
        "WASAPI keeps two 4096-frame packets instead of discarding the jitter reserve");

    const auto large_packet_catchup =
        iPhoneMirror::audio::detail::plan_wasapi_enqueue(
            4097, 4096, audio_capacity, queue_4096);
    const auto medium_packet_at_limit =
        iPhoneMirror::audio::detail::plan_wasapi_enqueue(
            queue_2048.startup_frames, 2048,
            audio_capacity, queue_2048);
    const auto large_packet_burst =
        iPhoneMirror::audio::detail::plan_wasapi_enqueue(
            4512, 4096, audio_capacity, queue_4096);
    check(large_packet_catchup.drop_existing_frames == 1 &&
        large_packet_catchup.final_frames == queue_4096.high_water_frames &&
        large_packet_burst.drop_existing_frames == 416 &&
        large_packet_burst.final_frames == queue_4096.high_water_frames &&
        medium_packet_at_limit.drop_existing_frames == 0 &&
        medium_packet_at_limit.final_frames == queue_2048.high_water_frames,
        "WASAPI drops only the excess above the adaptive high-water mark");

    const auto saturated_queue =
        iPhoneMirror::audio::detail::wasapi_queue_thresholds(
            audio_capacity * 2, audio_capacity, audio_capacity);
    check(saturated_queue.startup_frames == audio_capacity &&
        saturated_queue.high_water_frames == audio_capacity,
        "WASAPI queue threshold arithmetic saturates at ring capacity");

    audio_format.sample_rate = std::numeric_limits<double>::infinity();
    check(!iPhoneMirror::audio::detail::checked_wasapi_buffer_layout(audio_format),
        "WASAPI rejects non-finite rates before allocation");
    audio_format.sample_rate = 48000;
    audio_format.bytes_per_frame = std::numeric_limits<std::uint32_t>::max();
    check(!iPhoneMirror::audio::detail::checked_wasapi_buffer_layout(audio_format),
        "WASAPI rejects malformed block alignment before allocation");

    constexpr auto packed = iPhoneMirror::capture::detail::pack_video_dimensions(
        1206, 2622);
    constexpr auto unpacked =
        iPhoneMirror::capture::detail::unpack_video_dimensions(packed);
    static_assert(unpacked.width == 1206 && unpacked.height == 2622);
    check(unpacked.width == 1206 && unpacked.height == 2622,
        "adaptive display dimensions publish as one atomic value");

    StreamingSilenceWatchdog silence_watchdog;
    const StreamingSilenceWatchdog::Clock::time_point media_started{};
    check(!silence_watchdog.expired(media_started + std::chrono::seconds(30)),
        "streaming silence watchdog does not fire before valid media arrives");
    silence_watchdog.observe_media(media_started);
    check(!silence_watchdog.expired(
            media_started + StreamingSilenceWatchdog::SilenceLimit -
                std::chrono::milliseconds(1)),
        "streaming silence watchdog keeps a full ten-second grace period");
    check(silence_watchdog.expired(
            media_started + StreamingSilenceWatchdog::SilenceLimit) &&
        silence_watchdog.silence_duration(
            media_started + StreamingSilenceWatchdog::SilenceLimit) ==
                std::chrono::milliseconds(10'000),
        "streaming silence watchdog expires at ten seconds");
    silence_watchdog.observe_media(media_started + std::chrono::seconds(9));
    check(!silence_watchdog.expired(media_started + std::chrono::seconds(18)) &&
        silence_watchdog.expired(media_started + std::chrono::seconds(19)),
        "new video or audio media resets the streaming silence deadline");

    VideoQueueBudget budget;
    check(budget.has_capacity(0, 0, 1024),
        "empty compressed video queue accepts a normal sample");
    const auto overflow = budget.admit(VideoQueueBudget::MaxPendingSamples,
        4096, 1024, false);
    check(overflow.action == VideoQueueAction::ClearAndDrop &&
        overflow.entered_recovery && budget.awaiting_keyframe() &&
        overflow.dropped_samples == VideoQueueBudget::MaxPendingSamples + 1,
        "queue overflow drops the stale GOP and waits for a keyframe");
    const auto inter_frame = budget.admit(0, 0, 2048, false);
    check(inter_frame.action == VideoQueueAction::DropIncoming &&
        budget.awaiting_keyframe(),
        "queue recovery rejects inter frames without growing memory");
    const auto keyframe = budget.admit(0, 0, 4096, true);
    check(keyframe.action == VideoQueueAction::ReplaceWithKeyframe &&
        !budget.awaiting_keyframe(),
        "queue recovery resumes at a keyframe with decoder reset");
    check(!budget.has_capacity(0, 0, VideoQueueBudget::MaxPendingBytes + 1),
        "single compressed samples cannot exceed the queue byte budget");

    iPhoneMirror::capture::detail::VideoWorkerFailure standard_failure;
    try {
        throw std::runtime_error("decoder fault");
    } catch (...) {
        standard_failure.capture_current();
    }
    check(standard_failure.failed(), "decoder worker failure is observable by capture loop");
    check_throws([&] { standard_failure.rethrow_if_set(); },
        "decoder worker exception is rethrown on the capture thread");

    iPhoneMirror::capture::detail::VideoWorkerFailure nonstandard_failure;
    try {
        throw 7;
    } catch (...) {
        nonstandard_failure.capture_current();
    }
    bool normalized_nonstandard{};
    try {
        nonstandard_failure.rethrow_if_set();
    } catch (const std::runtime_error&) {
        normalized_nonstandard = true;
    }
    check(normalized_nonstandard,
        "non-standard decoder exceptions are normalized before leaving noexcept run");
}

void test_image_adjustment_api_validation() {
    const auto invalid_argument =
        static_cast<std::int32_t>(iPhoneMirror::Result::InvalidArgument);
    const auto not_initialized =
        static_cast<std::int32_t>(iPhoneMirror::Result::NotInitialized);

    check(im_set_image_adjustments(-1.0F, 0.0F, 0.0F, 0.5F) == not_initialized &&
        im_set_image_adjustments(1.0F, 2.0F, 2.0F, 2.0F) == not_initialized,
        "image adjustment API accepts every inclusive boundary before initialization");
    check(im_set_image_adjustments(-1.001F, 1.0F, 1.0F, 1.0F) == invalid_argument &&
        im_set_image_adjustments(1.001F, 1.0F, 1.0F, 1.0F) == invalid_argument,
        "image adjustment API rejects brightness outside [-1, 1]");
    check(im_set_image_adjustments(0.0F, -0.001F, 1.0F, 1.0F) == invalid_argument &&
        im_set_image_adjustments(0.0F, 2.001F, 1.0F, 1.0F) == invalid_argument,
        "image adjustment API rejects contrast outside [0, 2]");
    check(im_set_image_adjustments(0.0F, 1.0F, -0.001F, 1.0F) == invalid_argument &&
        im_set_image_adjustments(0.0F, 1.0F, 2.001F, 1.0F) == invalid_argument,
        "image adjustment API rejects saturation outside [0, 2]");
    check(im_set_image_adjustments(0.0F, 1.0F, 1.0F, 0.499F) == invalid_argument &&
        im_set_image_adjustments(0.0F, 1.0F, 1.0F, 2.001F) == invalid_argument,
        "image adjustment API rejects gamma outside [0.5, 2]");
    check(im_set_image_adjustments(std::numeric_limits<float>::quiet_NaN(),
              1.0F, 1.0F, 1.0F) == invalid_argument &&
        im_set_image_adjustments(0.0F, std::numeric_limits<float>::infinity(),
              1.0F, 1.0F) == invalid_argument,
        "image adjustment API rejects non-finite values");
    check(im_session_set_image_adjustments(0, -1.001F, 1.0F, 1.0F, 1.0F) ==
            invalid_argument &&
        im_session_set_image_adjustments(0, 0.0F, 1.0F, 1.0F, 0.499F) ==
            invalid_argument,
        "session image adjustment API validates values before resolving handles");
}

void test_wireless_i420_conversion() {
    iPhoneMirror::wireless::MessageHeader header;
    header.type = iPhoneMirror::wireless::MessageType::Video;
    header.width = 3;
    header.height = 2;
    header.stride[0] = 4;
    header.stride[1] = 2;
    header.stride[2] = 2;
    header.plane_size[0] = 8;
    header.plane_size[1] = 2;
    header.plane_size[2] = 2;
    const std::vector<std::uint8_t> i420{
        1, 2, 3, 99, 4, 5, 6, 99,
        10, 11,
        20, 21,
    };
    std::vector<std::uint8_t> nv12;
    std::int32_t stride{};
    check(iPhoneMirror::capture::detail::convert_i420_to_nv12(
        header, i420, nv12, stride), "wireless I420 frame converts to NV12");
    check(stride == 4, "wireless NV12 stride is even");
    check(nv12 == std::vector<std::uint8_t>{
        1, 2, 3, 0, 4, 5, 6, 0, 10, 20, 11, 21,
    }, "wireless NV12 planes and chroma order are correct");
    check(!iPhoneMirror::capture::detail::convert_i420_to_nv12(
        header, std::span(i420).first(11), nv12, stride),
        "wireless conversion rejects truncated planes");
    check(sizeof(iPhoneMirror::wireless::MessageHeader) == 392 &&
        sizeof(iPhoneMirror::CaptureStatus) == 464 &&
        iPhoneMirror::ApiVersion == 18 &&
        header.magic == iPhoneMirror::wireless::IpcMagic &&
        header.version == iPhoneMirror::wireless::IpcVersion &&
        iPhoneMirror::wireless::IpcVersion == 6,
        "wireless IPC header layout and version are stable");
    check(static_cast<std::uint32_t>(
            iPhoneMirror::capture::MediaCastCommandType::Pause) == 3 &&
        static_cast<std::uint32_t>(
            iPhoneMirror::capture::MediaCastCommandType::Resume) == 4 &&
        static_cast<std::uint32_t>(
            iPhoneMirror::capture::MediaCastCommandType::Seek) == 5,
        "media playback controls have stable public ABI values");
}

void test_wireless_multi_stream_isolation() {
    iPhoneMirror::capture::CapturePreferences preferences;
    preferences.play_audio = false;
    auto first = std::make_shared<iPhoneMirror::capture::WirelessClientStream>(
        L"00:11:22:33:44:55", L"First iPhone");
    auto second = std::make_shared<iPhoneMirror::capture::WirelessClientStream>(
        L"66:77:88:99:AA:BB", L"Second iPhone");
    first->set_identity(L"First iPhone", true);
    second->set_identity(L"Second iPhone", true);
    first->attach(preferences);
    second->attach(preferences);

    iPhoneMirror::wireless::MessageHeader first_header;
    first_header.type = iPhoneMirror::wireless::MessageType::Video;
    first_header.width = 4;
    first_header.height = 2;
    first_header.stride[0] = 4;
    first_header.stride[1] = first_header.stride[2] = 2;
    first_header.plane_size[0] = 8;
    first_header.plane_size[1] = first_header.plane_size[2] = 2;
    const std::vector<std::uint8_t> first_i420{
        1, 2, 3, 4, 5, 6, 7, 8, 10, 11, 20, 21,
    };
    auto second_header = first_header;
    second_header.width = 2;
    second_header.stride[0] = 2;
    second_header.stride[1] = second_header.stride[2] = 1;
    second_header.plane_size[0] = 4;
    second_header.plane_size[1] = second_header.plane_size[2] = 1;
    const std::vector<std::uint8_t> second_i420{31, 32, 33, 34, 40, 50};

    first->publish_video(first_header, first_i420);
    second->publish_video(second_header, second_i420);
    const auto first_snapshot = first->snapshot();
    const auto second_snapshot = second->snapshot();
    check(first_snapshot.width == 4 && first_snapshot.height == 2 &&
        first_snapshot.video_frames == 1, "first wireless client owns its frame state");
    check(second_snapshot.width == 2 && second_snapshot.height == 2 &&
        second_snapshot.video_frames == 1, "second wireless client owns its frame state");
    check(first->latest_frame()->nv12 != second->latest_frame()->nv12,
        "wireless client pixel buffers are isolated");

    first->set_identity(L"First iPhone", false);
    check(first->snapshot().width == 0 && second->snapshot().width == 2,
        "disconnect clears only the matching wireless client");
    first->publish_video(first_header, first_i420);
    check(first->snapshot().video_frames == 1 && first->latest_frame() == nullptr,
        "late frames after disconnect cannot repopulate the wireless client");
    first->detach();
    second->detach();
}

void test_media_command_queue() {
    using iPhoneMirror::capture::MediaCastCommand;
    using iPhoneMirror::capture::MediaCastCommandType;
    iPhoneMirror::capture::detail::MediaCommandQueue queue;
    check(queue.push(MediaCastCommand{.id = 1, .type = MediaCastCommandType::Play,
        .url = L"https://example.test/video.mp4"}), "first media command is accepted");
    check(queue.push(MediaCastCommand{.id = 2, .type = MediaCastCommandType::Seek,
        .start_position = 12.5}), "newer seek command is accepted");
    check(queue.push(MediaCastCommand{.id = 3, .type = MediaCastCommandType::Pause}),
        "newer pause command is accepted");
    check(queue.pop().id == 1 && queue.pop().id == 2 && queue.pop().id == 3,
        "media commands preserve Play/Seek/Pause order");

    check(!queue.push(MediaCastCommand{.id = 3, .type = MediaCastCommandType::Resume}) &&
        !queue.push(MediaCastCommand{.id = 2, .type = MediaCastCommandType::Seek}) &&
        !queue.push(MediaCastCommand{.id = 0, .type = MediaCastCommandType::Stop}),
        "stale and zero media commands are rejected");
    check(queue.size() == 0 && queue.latest_id() == 3,
        "media command queue rejects duplicate and out-of-order command ids");

    queue.reset();
    check(queue.push(MediaCastCommand{.id = 10, .type = MediaCastCommandType::Play}),
        "media queue accepts play after reset");
    for (std::uint64_t id = 11; id <= 90; ++id)
        check(queue.push(MediaCastCommand{.id = id, .type = MediaCastCommandType::Seek,
            .start_position = static_cast<double>(id)}),
            "monotonic media command flood is accepted");
    check(queue.size() == 64, "media command queue has a hard bound");
    check(queue.pop().id == 10, "control floods never evict the Play prerequisite");
    std::uint64_t final_id{};
    while (queue.size() != 0) final_id = queue.pop().id;
    check(final_id == 90 && queue.latest_id() == 90,
        "media command queue retains the newest control and command id");
}

void test_logging_shutdown_boundary() {
    namespace logging = iPhoneMirror::logging;
    logging::shutdown();

    const auto path = std::filesystem::temp_directory_path() /
        (L"iPhoneMirror-logging-boundary-" +
            std::to_wstring(GetCurrentProcessId()) + L".log");
    std::error_code error;
    std::filesystem::remove(path, error);
    SetEnvironmentVariableW(L"IPHONE_MIRROR_LOG_FILE", path.c_str());

    logging::write("late-write-must-be-discarded");
    check(!std::filesystem::exists(path),
        "logging shutdown prevents a late write from reopening the file");

    logging::initialize();
    constexpr std::string_view sensitive_identifier =
        "00008110-0012345678901234";
    const auto identifier_fingerprint = logging::fingerprint(sensitive_identifier);
    check(identifier_fingerprint == logging::fingerprint(sensitive_identifier) &&
        identifier_fingerprint != logging::fingerprint("another-device") &&
        identifier_fingerprint.starts_with("anon-") &&
        identifier_fingerprint.find(sensitive_identifier) == std::string::npos,
        "log fingerprints are stable within a process and do not expose identifiers");
    logging::write("test_identifier_fp=" + identifier_fingerprint);
    logging::write("write-after-explicit-reinitialize");
    logging::write(iPhoneMirror::logging::Level::Warning, "test/category",
        "warning-line-one\nwarning-line-two");
    logging::write_event(iPhoneMirror::logging::Level::Error, "test",
        "synthetic_failure", "code=42");
    logging::shutdown();
    std::ifstream stream(path, std::ios::binary);
    const std::string contents{
        std::istreambuf_iterator<char>(stream), std::istreambuf_iterator<char>()};
    check(contents.find("late-write-must-be-discarded") == std::string::npos &&
        contents.find(sensitive_identifier) == std::string::npos &&
        contents.find(identifier_fingerprint) != std::string::npos &&
        contents.find("write-after-explicit-reinitialize") != std::string::npos &&
        contents.find("[level=WARN] [category=test_category]") != std::string::npos &&
        contents.find("warning-line-one warning-line-two") != std::string::npos &&
        contents.find("[level=ERROR] [category=test]") != std::string::npos &&
        contents.find("event=synthetic_failure code=42") != std::string::npos &&
        contents.find("[seq=") != std::string::npos &&
        contents.find("[session=") != std::string::npos &&
        contents.find("dropped_before_start=1") != std::string::npos &&
        contents.find("[shutdown] session=") != std::string::npos &&
        contents.find("warnings=1 errors=1") != std::string::npos,
        "structured logging preserves context, sanitizes lines, and writes a summary");

    SetEnvironmentVariableW(L"IPHONE_MIRROR_LOG_FILE", nullptr);
    stream.close();
    std::filesystem::remove(path, error);

    const auto failed_directory = std::filesystem::temp_directory_path() /
        (L"iPhoneMirror-logging-failure-" +
            std::to_wstring(GetCurrentProcessId()));
    std::filesystem::remove_all(failed_directory, error);
    std::filesystem::create_directories(failed_directory, error);
    SetEnvironmentVariableW(L"IPHONE_MIRROR_LOG_FILE", failed_directory.c_str());
    logging::initialize();
    logging::write("write-to-directory-must-be-counted");

    // A failed destination must be recoverable without tearing down Core. In
    // production this covers a transiently unavailable custom log directory.
    SetEnvironmentVariableW(L"IPHONE_MIRROR_LOG_FILE", path.c_str());
    logging::initialize();
    logging::write("write-after-failed-log-target");
    logging::shutdown();
    std::ifstream recovery_stream(path, std::ios::binary);
    const std::string recovery_contents{
        std::istreambuf_iterator<char>(recovery_stream),
        std::istreambuf_iterator<char>()};
    check(recovery_contents.find("dropped_before_start=1") != std::string::npos &&
        recovery_contents.find("write-after-failed-log-target") != std::string::npos,
        "failed log targets recover in-place and retain the dropped-write count");
    recovery_stream.close();
    SetEnvironmentVariableW(L"IPHONE_MIRROR_LOG_FILE", nullptr);
    std::filesystem::remove(path, error);
    std::filesystem::remove_all(failed_directory, error);
}

} // namespace

int main(int argc, char** argv) {
    if (argc == 2 && std::string_view(argv[1]) == "--usb-runtime-probe-only") {
        try {
            test_libusb_runtime();
            test_active_apple_usb_identity_cache();
        } catch (const std::exception& error) {
            std::cerr << "UNEXPECTED: " << error.what() << '\n';
            return 2;
        }
        if (failures != 0) {
            std::cerr << failures << " test(s) failed\n";
            return 1;
        }
        std::cout << "USB runtime probe policy tests passed\n";
        return 0;
    }

    {
        const iPhoneMirror::quicktime::SessionOptions native_options;
        check(native_options.requested_width == 1206 && native_options.requested_height == 2622,
            "default HPD1 requests the verified native portrait tier");
        check(!native_options.request_native_display_size,
            "default HPD1 includes the verified native DisplaySize");
        check(!native_options.demo_mode, "default HPD1 preserves the real status bar");
    }
    try {
        test_plist();
        test_quicktime_framing();
        test_h264();
        test_coremedia();
        test_upstream_capture_fixtures();
        test_session_protocol();
        test_usb_projection_modes();
        test_apple_usb_serial_matching();
        test_apple_usb_filter_safety();
        test_active_apple_usb_identity_cache();
        test_apple_usb_reenumeration_selection();
        test_libusb_runtime();
        test_media_foundation_decoder();
        test_decoder_switch_transaction();
        test_wireless_decoder_status();
        test_capture_media_safety_helpers();
        test_image_adjustment_api_validation();
        test_wireless_i420_conversion();
        test_wireless_multi_stream_isolation();
        test_media_command_queue();
        test_logging_shutdown_boundary();
    } catch (const std::exception& error) {
        std::cerr << "UNEXPECTED: " << error.what() << '\n';
        return 2;
    }
    if (failures != 0) {
        std::cerr << failures << " test(s) failed\n";
        return 1;
    }
    std::cout << "All native protocol tests passed\n";
    return 0;
}

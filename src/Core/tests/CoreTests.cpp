#include "Media/CoreMedia.h"
#include "Media/H264.h"
#include "Media/MediaFoundationDecoder.h"
#include "Protocol/Plist.h"
#include "Protocol/QuickTimePacket.h"
#include "Protocol/QuickTimeSession.h"
#include "Transport/LibUsb0Readiness.h"
#include "Transport/QtUsbTransport.h"
#include "Capture/CaptureSession.h"
#include "Capture/WirelessCaptureSession.h"
#include "IpcProtocol.h"

#include <algorithm>
#include <cmath>
#include <cstdint>
#include <exception>
#include <fstream>
#include <iostream>
#include <span>
#include <stdexcept>
#include <string>
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
    const auto probe = iPhoneMirror::transport::probe_usb_runtime();
    check(probe.runtime_available, "libusb runtime loads");
    check(probe.version.starts_with("1.0.29"), "libusb runtime version is 1.0.29");
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
}

void test_media_foundation_decoder() {
    const auto packet = decode_framed(read_fixture("fixtures/quicktime_video_hack/asyn-feed"));
    const auto envelope = iPhoneMirror::coremedia::parse_sample_envelope(packet.payload);
    const auto sample = iPhoneMirror::coremedia::parse_sample_buffer(envelope.serialized_sample_buffer);
    check(sample.format.has_value(), "decoder fixture has video format");
    if (!sample.format) return;
    iPhoneMirror::media::MediaFoundationH264Decoder decoder;
    decoder.configure(*sample.format, 60, 1);
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
}

void test_capture_preflight_without_device() {
    iPhoneMirror::capture::CaptureSession session("definitely-not-a-real-udid");
    check_throws([&] { session.start(false); }, "capture preflight rejects missing USB device");
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
    check(sizeof(iPhoneMirror::wireless::MessageHeader) == 256 &&
        header.magic == iPhoneMirror::wireless::IpcMagic &&
        header.version == iPhoneMirror::wireless::IpcVersion,
        "wireless IPC header layout and version are stable");
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
    first->detach();
    second->detach();
}

} // namespace

int main() {
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
        test_libusb_runtime();
        test_media_foundation_decoder();
        test_capture_preflight_without_device();
        test_wireless_i420_conversion();
        test_wireless_multi_stream_isolation();
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

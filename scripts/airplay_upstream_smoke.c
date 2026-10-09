/* Exercise the actual patched receiver library, including its audio queue. */
#include <winsock2.h>
#include <windows.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <math.h>
#include "logger.h"
#include "raop.h"
#include "raop_buffer.h"
#include "raop_rtp.h"
#include "raop_rtp_mirror.h"
#include "mirror_buffer.h"
#include "byteutils.h"
#include "sha512.h"
#include "aes.h"
#include "compat.h"
#define CHECK(x) do { if (!(x)) { fprintf(stderr, "FAIL line %d: %s\n", __LINE__, #x); exit(1); } } while (0)
static HANDLE state_event, frame_event, geometry_event, disconnected_event;
static volatile LONG last_state, video_length, video_type;
static unsigned char video_bytes[1024];
static void log_message(void *arg, int level, const char *message) {
    (void)arg; (void)level;
    if (strstr(message, "IPHONE_MIRROR_VIDEO_STATE\t")) {
        const char *last = strrchr(message, '\t');
        InterlockedExchange(&last_state, atoi(last + 1)); SetEvent(state_event);
    }
    if (strstr(message, "IPHONE_MIRROR_VIDEO_GEOMETRY\t")) SetEvent(geometry_event);
}
static void video(void *cls, h264_decode_struct *frame, const char *name, const char *id) {
    (void)cls; (void)name; (void)id;
    CHECK(frame->data_len > 0 && frame->data_len <= sizeof(video_bytes));
    memcpy(video_bytes, frame->data, frame->data_len);
    InterlockedExchange(&video_length, frame->data_len);
    InterlockedExchange(&video_type, frame->frame_type); SetEvent(frame_event);
}
static void disconnected(void *cls, const char *name, const char *id) {
    (void)cls; (void)name; (void)id; SetEvent(disconnected_event);
}
static SOCKET connect_local(unsigned short port) {
    SOCKET socket_fd = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
    struct sockaddr_in address = {0};
    address.sin_family = AF_INET; address.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    address.sin_port = htons(port);
    CHECK(socket_fd != INVALID_SOCKET);
    CHECK(connect(socket_fd, (struct sockaddr *)&address, sizeof(address)) == 0);
    return socket_fd;
}
static void wait_state(int state) {
    ULONGLONG limit = GetTickCount64() + 4000;
    do {
        CHECK(WaitForSingleObject(state_event, 4000) == WAIT_OBJECT_0);
        if (last_state == state) return;
    } while (GetTickCount64() < limit);
    CHECK(0);
}
static void send_packet(SOCKET socket_fd, unsigned char *header, const unsigned char *payload, int size) {
    memcpy(header, &size, 4);
    CHECK(send(socket_fd, (char *)header, 128, 0) == 128);
    if (size) CHECK(send(socket_fd, (const char *)payload, size, 0) == size);
}
static void test_mirror(logger_t *logger) {
    const unsigned char address[4] = {127,0,0,1}, key[32] = {0};
    raop_callbacks_t callbacks = {0};
    unsigned short timing_port = 0, data_port = 0;
    raop_rtp_mirror_t *mirror;
    mirror_buffer_t *cipher;
    unsigned char header[128] = {0}, config[313] = {1,100,0,31,255,225,0,2,0x67,1,1,1,44};
    unsigned char nal[8] = {0,0,0,4,0x65,1,2,3}, encrypted[8];
    float width = 1920, height = 1080;
    SOCKET socket_fd;
    callbacks.video_process = video;
    callbacks.disconnected = disconnected;
    mirror = raop_rtp_mirror_init(logger, &callbacks, address, 4, address, 4,
        "test", "test-device", key, key, 9);
    CHECK(mirror);
    raop_rtp_init_mirror_aes(mirror, 42);
    cipher = mirror_buffer_init(logger, key, key); CHECK(cipher);
    mirror_buffer_init_aes(cipher, 42);
    raop_rtp_start_mirror(mirror, 0, 9, &timing_port, &data_port);
    CHECK(data_port && timing_port);
    socket_fd = connect_local(data_port); wait_state(0);
    header[4] = 1; header[6] = 0x56;
    send_packet(socket_fd, header, NULL, 0); wait_state(1);
    /* Idle/static frames must not activate the exact-read timeout. */
    Sleep(3200);
    header[6] = 0x16; send_packet(socket_fd, header, NULL, 0); wait_state(0);
    /* Short codec records and out-of-range lengths must not publish video. */
    for (int count = 1; count < 13; ++count) send_packet(socket_fd, header, config, count);
    config[6] = 0x80; send_packet(socket_fd, header, config, sizeof(config)); config[6] = 0;
    CHECK(WaitForSingleObject(frame_event, 200) == WAIT_TIMEOUT);
    memset(config + 13, 0x68, 300);
    memcpy(header + 40, &width, 4); memcpy(header + 44, &height, 4);
    memcpy(header + 56, &width, 4); memcpy(header + 60, &height, 4);
    header[8] = 11;
    send_packet(socket_fd, header, config, sizeof(config));
    CHECK(WaitForSingleObject(geometry_event, 4000) == WAIT_OBJECT_0);
    header[4] = 0;
    mirror_buffer_decrypt(cipher, nal, encrypted, sizeof(nal));
    send_packet(socket_fd, header, encrypted, sizeof(encrypted));
    CHECK(WaitForSingleObject(frame_event, 4000) == WAIT_OBJECT_0);
    CHECK(video_length == 318 && video_type == 0); /* PPS length really is 300. */
    closesocket(socket_fd); wait_state(2);
    socket_fd = connect_local(data_port); wait_state(0);
    header[4] = 1; header[6] = 0x56;
    send_packet(socket_fd, header, NULL, 0); wait_state(1);
    /* Header interrupted by FIN preserves the listener and resets pending SPS. */
    CHECK(send(socket_fd, (char *)header, 8, 0) == 8);
    closesocket(socket_fd); wait_state(2);
    socket_fd = connect_local(data_port); wait_state(1);
    header[6] = 0x16; send_packet(socket_fd, header, NULL, 0); wait_state(0);
    header[4] = 0;
    mirror_buffer_decrypt(cipher, nal, encrypted, sizeof(nal));
    send_packet(socket_fd, header, encrypted, sizeof(encrypted));
    CHECK(WaitForSingleObject(frame_event, 4000) == WAIT_OBJECT_0);
    CHECK(video_length == 8 && video_type == 1);
    /* A truncated header with a live peer must also time out and reconnect. */
    CHECK(send(socket_fd, (char *)header, 5, 0) == 5); wait_state(2);
    closesocket(socket_fd);
    socket_fd = connect_local(data_port); wait_state(0);
    /* Retried SETUP for the same stream returns live ports and preserves CTR. */
    unsigned short retry_timing = 0, retry_data = 0;
    raop_rtp_init_mirror_aes(mirror, 42);
    raop_rtp_start_mirror(mirror, 0, 9, &retry_timing, &retry_data);
    CHECK(retry_timing == timing_port && retry_data == data_port);
    mirror_buffer_decrypt(cipher, nal, encrypted, sizeof(nal));
    send_packet(socket_fd, header, encrypted, sizeof(encrypted));
    CHECK(WaitForSingleObject(frame_event, 4000) == WAIT_OBJECT_0);
    CHECK(video_length == 8 && !memcmp(video_bytes + 4, nal + 4, 4));
    /* FIN and live-peer timeout inside ciphertext skip the complete declared frame.
     * Cover both a remaining CTR block and a cut crossing AES block boundaries. */
    for (int cut = 0; cut < 3; ++cut) {
        unsigned char large_nal[37] = {0,0,0,33,0x65}, large_encrypted[37];
        int size = cut == 1 ? sizeof(large_nal) : sizeof(nal);
        int delivered = cut == 1 ? 17 : 3;
        for (int i = 5; i < sizeof(large_nal); ++i) large_nal[i] = (unsigned char)i;
        mirror_buffer_decrypt(cipher, cut == 1 ? large_nal : nal, large_encrypted, size);
        memcpy(header, &size, 4);
        CHECK(send(socket_fd, (char *)header, sizeof(header), 0) == sizeof(header));
        CHECK(send(socket_fd, (char *)large_encrypted, delivered, 0) == delivered);
        if (cut < 2) closesocket(socket_fd);
        wait_state(2);
        if (cut == 2) closesocket(socket_fd);
        socket_fd = connect_local(data_port); wait_state(0);
        mirror_buffer_decrypt(cipher, nal, encrypted, sizeof(nal));
        send_packet(socket_fd, header, encrypted, sizeof(encrypted));
        CHECK(WaitForSingleObject(frame_event, 4000) == WAIT_OBJECT_0);
        CHECK(video_length == 8 && !memcmp(video_bytes + 4, nal + 4, 4));
    }
    /* New stream IDs restart cleanly both during a read and while reconnecting. */
    for (uint64_t id = 43; id <= 44; ++id) {
        if (id == 43) CHECK(send(socket_fd, (char *)header, 5, 0) == 5);
        else { closesocket(socket_fd); wait_state(2); }
        ResetEvent(disconnected_event);
        raop_rtp_init_mirror_aes(mirror, id);
        CHECK(WaitForSingleObject(disconnected_event, 4000) == WAIT_OBJECT_0);
        if (id == 43) closesocket(socket_fd);
        timing_port = data_port = 0;
        raop_rtp_start_mirror(mirror, 0, 9, &timing_port, &data_port);
        CHECK(data_port && timing_port);
        mirror_buffer_init_aes(cipher, id);
        socket_fd = connect_local(data_port); wait_state(0);
        header[4] = 1;
        send_packet(socket_fd, header, config, sizeof(config));
        header[4] = 0;
        mirror_buffer_decrypt(cipher, nal, encrypted, sizeof(nal));
        send_packet(socket_fd, header, encrypted, sizeof(encrypted));
        CHECK(WaitForSingleObject(frame_event, 4000) == WAIT_OBJECT_0);
        CHECK(video_length == 318 && video_type == 0 &&
            !memcmp(video_bytes + video_length - 4, nal + 4, 4));
    }
    /* A completely unseen frame makes CTR position unknowable. Do not remain
     * silently active forever; stop, then accept fresh keys through SETUP. */
    ResetEvent(disconnected_event);
    mirror_buffer_decrypt(cipher, nal, encrypted, sizeof(nal)); /* lost entire frame */
    for (int i = 0; i < 3; ++i) {
        mirror_buffer_decrypt(cipher, nal, encrypted, sizeof(nal));
        send_packet(socket_fd, header, encrypted, sizeof(encrypted));
    }
    CHECK(WaitForSingleObject(disconnected_event, 4000) == WAIT_OBJECT_0);
    CHECK(WaitForSingleObject(frame_event, 100) == WAIT_TIMEOUT);
    closesocket(socket_fd);
    raop_rtp_init_mirror_aes(mirror, 45);
    timing_port = data_port = 0;
    raop_rtp_start_mirror(mirror, 0, 9, &timing_port, &data_port);
    CHECK(data_port && timing_port);
    mirror_buffer_init_aes(cipher, 45);
    socket_fd = connect_local(data_port); wait_state(0);
    mirror_buffer_decrypt(cipher, nal, encrypted, sizeof(nal));
    send_packet(socket_fd, header, encrypted, sizeof(encrypted));
    CHECK(WaitForSingleObject(frame_event, 4000) == WAIT_OBJECT_0);
    CHECK(video_length == 8 && !memcmp(video_bytes + 4, nal + 4, 4));
    ULONGLONG start = GetTickCount64();
    raop_rtp_mirror_stop(mirror); raop_rtp_mirror_stop(mirror);
    CHECK(GetTickCount64() - start < 1500);
    closesocket(socket_fd);
    raop_rtp_mirror_destroy(mirror); mirror_buffer_destroy(cipher);
    puts("Mirror: parameter bounds, pause/static screen, FIN/header/payload timeout, CTR continuity, repeated/new SETUP, lost-cipher recovery and prompt stop passed.");
}
static void test_audio(logger_t *logger, const char *fixture) {
    unsigned char key[16] = {0}, iv[16] = {0}, secret[32] = {0}, derived[64];
    raop_buffer_t *buffer = raop_buffer_init(logger,key,iv,secret);
    sha512_context hash;
    uint32_t count, rate; uint16_t channels, bits;
    unsigned int pts; int length, frames = 0;
    unsigned char packet[32768] = {0};
    double sine = 0, cosine = 0, noise_sine = 0, noise_cosine = 0, energy = 0;
    double right_sine = 0, right_cosine = 0, right_energy = 0;
    double left_cross_sine = 0, left_cross_cosine = 0, right_cross_sine = 0, right_cross_cosine = 0;
    int sample_index = 0;
    FILE *file = fopen(fixture,"rb"); CHECK(buffer && file);
    CHECK(raop_buffer_queue(buffer,packet,12,NULL,8) < 0);
    sha512_init(&hash); sha512_update(&hash,key,16); sha512_update(&hash,secret,32); sha512_final(&hash,derived);
    while (fread(&count,4,1,file) == 1) {
        struct AES_ctx aes;
        const int16_t *pcm;
        CHECK(count > 0 && count < sizeof(packet)-12);
        CHECK(fread(packet+12,count,1,file) == 1);
        AES_init_ctx_iv(&aes,derived,iv);
        AES_CBC_encrypt_buffer(&aes,packet+12,count/16*16);
        packet[2] = frames >> 8; packet[3] = frames;
        CHECK(raop_buffer_queue(buffer,packet,(unsigned short)(count+12),NULL,8) == 1);
        pcm = raop_buffer_dequeue(buffer,&length,&pts,1,&rate,&channels,&bits);
        CHECK(pcm && length == 1920 && rate == 44100 && channels == 2 && bits == 16);
        if (frames > 5) for (int i = 0; i < 480; ++i,++sample_index) {
            double value = pcm[i*2];
            double angle = sample_index*6.283185307179586/44100.0;
            sine += value*sin(angle*997); cosine += value*cos(angle*997);
            noise_sine += value*sin(angle*137); noise_cosine += value*cos(angle*137);
            energy += value*value;
            left_cross_sine += value*sin(angle*1501); left_cross_cosine += value*cos(angle*1501);
            double right = pcm[i*2+1];
            right_sine += right*sin(angle*1501); right_cosine += right*cos(angle*1501);
            right_cross_sine += right*sin(angle*997); right_cross_cosine += right*cos(angle*997);
            right_energy += right*right;
        }
        ++frames;
    }
    CHECK(frames >= 30 && energy/sample_index > 10000000);
    CHECK(sine*sine+cosine*cosine > (noise_sine*noise_sine+noise_cosine*noise_cosine)*100);
    CHECK(right_energy/sample_index > 10000000);
    CHECK(sine*sine+cosine*cosine > (left_cross_sine*left_cross_sine+left_cross_cosine*left_cross_cosine)*100);
    CHECK(right_sine*right_sine+right_cosine*right_cosine >
        (right_cross_sine*right_cross_sine+right_cross_cosine*right_cross_cosine)*100);
    fclose(file); raop_buffer_destroy(buffer);
    raop_rtp_stop(NULL);
    puts("AAC-ELD: real encrypted 480-sample stereo frames decode at 44100 Hz; PCM energy/frequency and channel order passed.");
}
static void ignore_audio(void *arg, pcm_data_struct *data, const char *name, const char *id) {
    (void)arg; (void)data; (void)name; (void)id;
    CHECK(0); /* Empty/short UDP packets must never publish PCM. */
}
static void test_audio_stop(logger_t *logger) {
    const unsigned char address[4] = {127,0,0,1}, key[32] = {0};
    unsigned short control = 0, timing = 0, data = 0;
    raop_callbacks_t callbacks = {0};
    callbacks.audio_process = ignore_audio;
    raop_rtp_t *rtp = raop_rtp_init(logger,&callbacks,address,4,address,4,
        "test","test-device",key,key,key,9);
    SOCKET udp = socket(AF_INET,SOCK_DGRAM,IPPROTO_UDP);
    CHECK(rtp && udp != INVALID_SOCKET);
    for (int attempt = 0; attempt < 2; ++attempt) {
        struct sockaddr_in peer = {0}; unsigned char empty[12] = {0};
        raop_rtp_start_audio(rtp,1,0,9,8,&control,&timing,&data);
        CHECK(control && timing && data);
        unsigned short retry_control = 0, retry_timing = 0, retry_data = 0;
        raop_rtp_start_audio(rtp,1,0,9,8,&retry_control,&retry_timing,&retry_data);
        CHECK(retry_control == control && retry_timing == timing && retry_data == data);
        peer.sin_family=AF_INET; peer.sin_addr.s_addr=htonl(INADDR_LOOPBACK);
        peer.sin_port=htons(timing);
        CHECK(sendto(udp,(char *)empty,1,0,(struct sockaddr *)&peer,sizeof(peer)) == 1);
        peer.sin_port=htons(control);
        CHECK(sendto(udp,(char *)empty,1,0,(struct sockaddr *)&peer,sizeof(peer)) == 1);
        peer.sin_port=htons(data);
        CHECK(sendto(udp,(char *)empty,12,0,(struct sockaddr *)&peer,sizeof(peer)) == 12);
        Sleep(100);
        ULONGLONG start=GetTickCount64();
        raop_rtp_stop(rtp); raop_rtp_stop(rtp);
        CHECK(GetTickCount64()-start < 1500);
    }
    raop_rtp_destroy(rtp); closesocket(udp);
    puts("Audio UDP: repeated SETUP ports, short timing/control, empty RTP, stop without responder and restart passed.");
}
typedef struct {
    HANDLE event;
    volatile LONG count;
    unsigned int timestamps[8];
} audio_probe_t;
static void collect_audio(void *arg, pcm_data_struct *data, const char *name, const char *id) {
    audio_probe_t *probe = arg;
    LONG index = InterlockedCompareExchange(&probe->count,0,0);
    (void)name; (void)id;
    CHECK(data->data && data->data_len == 1920 && data->sample_rate == 44100 &&
        data->channels == 2 && data->bits_per_sample == 16 && index < 8);
    probe->timestamps[index] = data->pts;
    InterlockedIncrement(&probe->count);
    SetEvent(probe->event);
}
static int read_audio_packet(const char *fixture, unsigned char *packet) {
    unsigned char key[32] = {0}, derived[64];
    uint32_t size;
    sha512_context hash;
    struct AES_ctx aes;
    FILE *file = fopen(fixture,"rb"); CHECK(file);
    CHECK(fread(&size,4,1,file) == 1 && size > 0 && size < 32768-12);
    memset(packet,0,12); packet[0] = 0x80; packet[1] = 0x60;
    CHECK(fread(packet+12,size,1,file) == 1); fclose(file);
    sha512_init(&hash); sha512_update(&hash,key,16); sha512_update(&hash,key,32);
    sha512_final(&hash,derived);
    AES_init_ctx_iv(&aes,derived,key);
    AES_CBC_encrypt_buffer(&aes,packet+12,size/16*16);
    return (int)size+12;
}
static void send_audio_packet(SOCKET udp, unsigned short port, unsigned char *packet,
    int size, unsigned short sequence) {
    struct sockaddr_in peer = {0};
    packet[2] = (unsigned char)(sequence >> 8); packet[3] = (unsigned char)sequence;
    packet[4] = 0; packet[5] = 0; packet[6] = 0; packet[7] = (unsigned char)sequence;
    peer.sin_family = AF_INET; peer.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    peer.sin_port = htons(port);
    CHECK(sendto(udp,(char *)packet,size,0,(struct sockaddr *)&peer,sizeof(peer)) == size);
}
static void wait_audio_count(audio_probe_t *probe, LONG count) {
    ULONGLONG deadline = GetTickCount64()+2000;
    while (InterlockedCompareExchange(&probe->count,0,0) < count && GetTickCount64() < deadline)
        WaitForSingleObject(probe->event,100);
    CHECK(InterlockedCompareExchange(&probe->count,0,0) == count);
}
static void test_audio_peer(logger_t *logger, const char *fixture) {
    const unsigned char address[4] = {127,0,0,1}, key[32] = {0};
    unsigned short control = 0, timing = 0, data = 0;
    unsigned char packet[32768]; int size = read_audio_packet(fixture,packet);
    audio_probe_t probe = {0}; probe.event = CreateEvent(NULL,FALSE,FALSE,NULL);
    raop_callbacks_t callbacks = {0}; callbacks.cls = &probe; callbacks.audio_process = collect_audio;
    raop_rtp_t *rtp = raop_rtp_init(logger,&callbacks,address,4,address,4,
        "test","test-device",key,key,key,9); CHECK(rtp && probe.event);
    SOCKET foreign = socket(AF_INET,SOCK_DGRAM,IPPROTO_UDP);
    SOCKET valid = socket(AF_INET,SOCK_DGRAM,IPPROTO_UDP);
    struct sockaddr_in source = {0}; source.sin_family = AF_INET;
    source.sin_addr.s_addr = htonl(0x7f000002);
    CHECK(foreign != INVALID_SOCKET && valid != INVALID_SOCKET &&
        bind(foreign,(struct sockaddr *)&source,sizeof(source)) == 0);
    raop_rtp_start_audio(rtp,1,0,9,8,&control,&timing,&data); CHECK(data);
    send_audio_packet(foreign,data,packet,size,0);
    Sleep(200); CHECK(InterlockedCompareExchange(&probe.count,0,0) == 0);
    send_audio_packet(valid,data,packet,size,0); wait_audio_count(&probe,1);
    unsigned char resend[32772] = {0}; resend[0] = 0x80; resend[1] = 0xd6;
    packet[3] = 20; packet[7] = 20; memcpy(resend+4,packet,size);
    struct sockaddr_in peer = {0}; peer.sin_family = AF_INET;
    peer.sin_addr.s_addr = htonl(INADDR_LOOPBACK); peer.sin_port = htons(control);
    CHECK(sendto(foreign,(char *)resend,size+4,0,(struct sockaddr *)&peer,sizeof(peer)) == size+4);
    Sleep(100);
    send_audio_packet(valid,data,packet,size,1); wait_audio_count(&probe,2);
    CHECK(probe.timestamps[0] == 0 && probe.timestamps[1] == 1);
    raop_rtp_stop(rtp); raop_rtp_destroy(rtp);
    CloseHandle(probe.event); closesocket(foreign); closesocket(valid);
    puts("Audio peer: reject foreign-IP data/control and accept the negotiated sender passed.");
}
static void test_audio_resend(logger_t *logger, const char *fixture) {
    const unsigned char address[4] = {127,0,0,1}, key[32] = {0};
    unsigned short control = 0, timing = 0, data = 0;
    unsigned char packet[32768], resend[32772]; int size = read_audio_packet(fixture,packet);
    audio_probe_t probe = {0}; probe.event = CreateEvent(NULL,FALSE,FALSE,NULL);
    raop_callbacks_t callbacks = {0}; callbacks.cls = &probe; callbacks.audio_process = collect_audio;
    raop_rtp_t *rtp = raop_rtp_init(logger,&callbacks,address,4,address,4,
        "test","test-device",key,key,key,9); CHECK(rtp && probe.event);
    SOCKET udp = socket(AF_INET,SOCK_DGRAM,IPPROTO_UDP); CHECK(udp != INVALID_SOCKET);
    raop_rtp_start_audio(rtp,1,9,9,8,&control,&timing,&data); CHECK(control && data);
    send_audio_packet(udp,data,packet,size,0); wait_audio_count(&probe,1);
    send_audio_packet(udp,data,packet,size,2);
    Sleep(100); CHECK(InterlockedCompareExchange(&probe.count,0,0) == 1);
    packet[3] = 1; packet[7] = 1;
    memset(resend,0,4); resend[0] = 0x80; resend[1] = 0xd6;
    memcpy(resend+4,packet,size);
    struct sockaddr_in peer = {0}; peer.sin_family = AF_INET;
    peer.sin_addr.s_addr = htonl(INADDR_LOOPBACK); peer.sin_port = htons(control);
    CHECK(sendto(udp,(char *)resend,size+4,0,(struct sockaddr *)&peer,sizeof(peer)) == size+4);
    wait_audio_count(&probe,3);
    CHECK(probe.timestamps[0] == 0 && probe.timestamps[1] == 1 && probe.timestamps[2] == 2);
    raop_rtp_stop(rtp); raop_rtp_destroy(rtp); CloseHandle(probe.event); closesocket(udp);
    puts("Audio resend: the final control resend releases ordered PCM without a later data packet passed.");
}
static void test_audio_flush(logger_t *logger, const char *fixture) {
    unsigned char key[32] = {0}, packet[32768];
    int size = read_audio_packet(fixture,packet), length;
    unsigned int pts; uint32_t rate; uint16_t channels, bits;
    raop_buffer_t *buffer = raop_buffer_init(logger,key,key,key); CHECK(buffer);
    CHECK(raop_buffer_queue(buffer,packet,(unsigned short)size,NULL,8) == 1);
    CHECK(raop_buffer_dequeue(buffer,&length,&pts,1,&rate,&channels,&bits) && length == 1920);
    raop_buffer_flush(buffer,5);
    unsigned char invalid[13] = {0}; invalid[3] = 6; invalid[12] = 0xff;
    CHECK(raop_buffer_queue(buffer,invalid,sizeof(invalid),NULL,8) == 1);
    CHECK(!raop_buffer_dequeue(buffer,&length,&pts,1,&rate,&channels,&bits));
    CHECK(length == 0 && rate == 0 && channels == 0 && bits == 0);
    CHECK(raop_buffer_dequeue(buffer,&length,&pts,1,&rate,&channels,&bits) && length == 0);
    packet[3] = 7;
    CHECK(raop_buffer_queue(buffer,packet,(unsigned short)size,NULL,8) == 1);
    CHECK(raop_buffer_dequeue(buffer,&length,&pts,1,&rate,&channels,&bits) && length == 1920);
    raop_buffer_destroy(buffer);
    puts("Audio flush: discard the old decoder/concealment state before fresh PCM passed.");
}
typedef struct {
    raop_rtp_t *rtp;
    HANDLE entered, release, stopping, destroying;
} audio_shutdown_probe_t;
static void block_audio(void *arg, pcm_data_struct *data, const char *name, const char *id) {
    audio_shutdown_probe_t *probe = arg;
    (void)data; (void)name; (void)id;
    SetEvent(probe->entered);
    CHECK(WaitForSingleObject(probe->release,4000) == WAIT_OBJECT_0);
}
static DWORD WINAPI stop_audio_worker(void *arg) {
    audio_shutdown_probe_t *probe = arg;
    SetEvent(probe->stopping); raop_rtp_stop(probe->rtp); return 0;
}
static DWORD WINAPI destroy_audio_worker(void *arg) {
    audio_shutdown_probe_t *probe = arg;
    SetEvent(probe->destroying); raop_rtp_destroy(probe->rtp); return 0;
}
static void test_audio_shutdown(logger_t *logger, const char *fixture) {
    const unsigned char address[4] = {127,0,0,1}, key[32] = {0};
    unsigned short control = 0, timing = 0, data = 0;
    unsigned char packet[32768]; int size = read_audio_packet(fixture,packet);
    audio_shutdown_probe_t probe = {0};
    probe.entered = CreateEvent(NULL,FALSE,FALSE,NULL);
    probe.release = CreateEvent(NULL,FALSE,FALSE,NULL);
    probe.stopping = CreateEvent(NULL,FALSE,FALSE,NULL);
    probe.destroying = CreateEvent(NULL,FALSE,FALSE,NULL);
    raop_callbacks_t callbacks = {0}; callbacks.cls = &probe; callbacks.audio_process = block_audio;
    probe.rtp = raop_rtp_init(logger,&callbacks,address,4,address,4,
        "test","test-device",key,key,key,9); CHECK(probe.rtp);
    SOCKET udp = socket(AF_INET,SOCK_DGRAM,IPPROTO_UDP); CHECK(udp != INVALID_SOCKET);
    raop_rtp_start_audio(probe.rtp,1,0,9,8,&control,&timing,&data); CHECK(data);
    send_audio_packet(udp,data,packet,size,0);
    CHECK(WaitForSingleObject(probe.entered,2000) == WAIT_OBJECT_0);
    HANDLE stop = CreateThread(NULL,0,stop_audio_worker,&probe,0,NULL); CHECK(stop);
    CHECK(WaitForSingleObject(probe.stopping,1000) == WAIT_OBJECT_0);
    CHECK(WaitForSingleObject(stop,100) == WAIT_TIMEOUT);
    HANDLE destroy = CreateThread(NULL,0,destroy_audio_worker,&probe,0,NULL); CHECK(destroy);
    CHECK(WaitForSingleObject(probe.destroying,1000) == WAIT_OBJECT_0);
    CHECK(WaitForSingleObject(destroy,100) == WAIT_TIMEOUT);
    SetEvent(probe.release);
    CHECK(WaitForSingleObject(stop,1500) == WAIT_OBJECT_0);
    CHECK(WaitForSingleObject(destroy,1500) == WAIT_OBJECT_0);
    CloseHandle(stop); CloseHandle(destroy); closesocket(udp);
    CloseHandle(probe.entered); CloseHandle(probe.release);
    CloseHandle(probe.stopping); CloseHandle(probe.destroying);
    puts("Audio shutdown: concurrent stop/destroy waits for an in-flight callback before freeing state passed.");
}
static void test_ctr_chunks(logger_t *logger) {
    unsigned char key[32]={0}, plain[64], encrypted[64], decoded[65];
    mirror_buffer_t *writer=mirror_buffer_init(logger,key,key);
    mirror_buffer_t *reader=mirror_buffer_init(logger,key,key);
    const int chunks[]={8,5,19,32}; int offset=0;
    CHECK(writer && reader);
    for (int i=0;i<64;++i) plain[i]=(unsigned char)i;
    memset(decoded,0xa5,sizeof(decoded));
    mirror_buffer_init_aes(writer,42); mirror_buffer_init_aes(reader,42);
    mirror_buffer_decrypt(writer,plain,encrypted,64);
    for (int i=0;i<4;++i) {
        mirror_buffer_decrypt(reader,encrypted+offset,decoded+offset,chunks[i]); offset+=chunks[i];
        CHECK(decoded[offset] == 0xa5);
    }
    CHECK(memcmp(plain,decoded,64) == 0);
    mirror_buffer_destroy(writer); mirror_buffer_destroy(reader);
}
static void test_ipv6(logger_t *logger) {
    unsigned char address[16] = {0}, key[32] = {0}, header[128] = {0};
    unsigned char nal[8] = {0,0,0,4,0x65,1,2,3}, encrypted[8];
    unsigned short timing = 0, data = 0, control = 0;
    raop_callbacks_t callbacks = {0};
    struct sockaddr_in6 peer = {0};
    address[15] = 1; peer.sin6_family = AF_INET6;
    memcpy(&peer.sin6_addr,address,sizeof(address));
    callbacks.video_process = video; callbacks.audio_process = ignore_audio;
    raop_rtp_mirror_t *mirror = raop_rtp_mirror_init(logger,&callbacks,address,16,
        address,16,"IPv6","test-device",key,key,9);
    raop_rtp_t *audio = raop_rtp_init(logger,&callbacks,address,16,address,16,
        "IPv6","test-device",key,key,key,9);
    CHECK(mirror && audio);
    raop_rtp_init_mirror_aes(mirror,46);
    mirror_buffer_t *cipher = mirror_buffer_init(logger,key,key); CHECK(cipher);
    mirror_buffer_init_aes(cipher,46);
    raop_rtp_start_mirror(mirror,0,9,&timing,&data); CHECK(timing && data);
    SOCKET stream = socket(AF_INET6,SOCK_STREAM,IPPROTO_TCP); CHECK(stream != INVALID_SOCKET);
    peer.sin6_port = htons(data);
    CHECK(connect(stream,(struct sockaddr *)&peer,sizeof(peer)) == 0); wait_state(0);
    mirror_buffer_decrypt(cipher,nal,encrypted,sizeof(nal));
    send_packet(stream,header,encrypted,sizeof(encrypted));
    CHECK(WaitForSingleObject(frame_event,4000) == WAIT_OBJECT_0 && video_length == 8);
    closesocket(stream); wait_state(2);
    stream = socket(AF_INET6,SOCK_STREAM,IPPROTO_TCP); CHECK(stream != INVALID_SOCKET);
    CHECK(connect(stream,(struct sockaddr *)&peer,sizeof(peer)) == 0); wait_state(0);
    mirror_buffer_decrypt(cipher,nal,encrypted,sizeof(nal));
    send_packet(stream,header,encrypted,sizeof(encrypted));
    CHECK(WaitForSingleObject(frame_event,4000) == WAIT_OBJECT_0 && video_length == 8);
    raop_rtp_start_audio(audio,1,0,9,8,&control,&timing,&data); CHECK(control && timing && data);
    SOCKET udp = socket(AF_INET6,SOCK_DGRAM,IPPROTO_UDP); CHECK(udp != INVALID_SOCKET);
    peer.sin6_port = htons(data);
    CHECK(sendto(udp,(char *)header,12,0,(struct sockaddr *)&peer,sizeof(peer)) == 12);
    ULONGLONG start = GetTickCount64();
    raop_rtp_mirror_stop(mirror); raop_rtp_stop(audio);
    CHECK(GetTickCount64()-start < 1500);
    closesocket(stream); closesocket(udp);
    raop_rtp_mirror_destroy(mirror); raop_rtp_destroy(audio); mirror_buffer_destroy(cipher);
    puts("IPv6: binary remote address, data accept/reconnect/decrypt, audio sockets and bounded stop passed.");
}
int main(int argc, char **argv) {
    WSADATA wsa; uint64_t start;
    CHECK((argc == 2 || argc == 3) && WSAStartup(MAKEWORD(2,2),&wsa) == 0);
    state_event=CreateEvent(NULL,FALSE,FALSE,NULL);
    frame_event=CreateEvent(NULL,FALSE,FALSE,NULL);
    geometry_event=CreateEvent(NULL,FALSE,FALSE,NULL);
    disconnected_event=CreateEvent(NULL,FALSE,FALSE,NULL);
    logger_t *logger=logger_init(); CHECK(logger);
    logger_set_level(logger,LOGGER_INFO); logger_set_callback(logger,log_message,NULL);
    start=now_us(); Sleep(1100); CHECK(now_us()-start >= 1000000 && now_us()-start < 1600000);
    if (argc == 3) {
        if (!strcmp(argv[2],"audio-retry")) test_audio_stop(logger);
        else if (!strcmp(argv[2],"audio-peer")) test_audio_peer(logger,argv[1]);
        else if (!strcmp(argv[2],"audio-resend")) test_audio_resend(logger,argv[1]);
        else if (!strcmp(argv[2],"audio-flush")) test_audio_flush(logger,argv[1]);
        else if (!strcmp(argv[2],"audio-shutdown")) test_audio_shutdown(logger,argv[1]);
        else CHECK(0);
    } else {
        test_audio(logger,argv[1]); test_audio_stop(logger); test_audio_peer(logger,argv[1]);
        test_audio_resend(logger,argv[1]); test_audio_flush(logger,argv[1]);
        test_audio_shutdown(logger,argv[1]);
        test_ctr_chunks(logger); test_mirror(logger); test_ipv6(logger);
    }
    logger_destroy(logger); CloseHandle(state_event); CloseHandle(frame_event); CloseHandle(geometry_event); CloseHandle(disconnected_event);
    WSACleanup(); return 0;
}

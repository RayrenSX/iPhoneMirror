struct SFgAudioFrame {
    unsigned long long pts;
    unsigned int sampleRate;
    unsigned short channels;
    unsigned short bitsPerSample;
    unsigned int dataLen;
    unsigned char* data;
};

struct SFgVideoFrame {
    unsigned long long pts;
    int isKey;
    unsigned int width;
    unsigned int height;
    unsigned int pitch[3];
    unsigned int dataLen[3];
    unsigned int dataTotalLen;
    unsigned char* data;
};

class IAirServerCallback {
public:
    virtual void connected(const char*, const char*) = 0;
    virtual void disconnected(const char*, const char*) = 0;
    virtual void outputAudio(SFgAudioFrame*, const char*, const char*) = 0;
    virtual void outputVideo(SFgVideoFrame*, const char*, const char*) = 0;
    virtual void videoPlay(char*, double, double) = 0;
    virtual void videoGetPlayInfo(double*, double*, double*) = 0;
    virtual void setVolume(float, const char*, const char*) = 0;
    virtual void log(int, const char*) = 0;
};

extern "C" void* __cdecl stub_start(const char*, unsigned int, unsigned int,
    IAirServerCallback* callback) {
    if (!callback) return nullptr;
    callback->log(6, "stub protocol log");
    callback->connected("Stub iPhone", "00:11:22:33:44:55");

    unsigned char video_bytes[]{
        16, 32, 48, 64, 80, 96, 112, 128,
        90, 100,
        140, 150,
    };
    SFgVideoFrame video{
        .pts = 1,
        .isKey = 1,
        .width = 4,
        .height = 2,
        .pitch = {4, 2, 2},
        .dataLen = {8, 2, 2},
        .dataTotalLen = sizeof(video_bytes),
        .data = video_bytes,
    };
    callback->outputVideo(&video, "Stub iPhone", "00:11:22:33:44:55");

    unsigned char audio_bytes[]{0, 0, 1, 0, 2, 0, 3, 0};
    SFgAudioFrame audio{
        .pts = 1,
        .sampleRate = 48000,
        .channels = 2,
        .bitsPerSample = 16,
        .dataLen = sizeof(audio_bytes),
        .data = audio_bytes,
    };
    callback->outputAudio(&audio, "Stub iPhone", "00:11:22:33:44:55");
    callback->connected("Second iPhone", "66:77:88:99:AA:BB");
    callback->outputVideo(&video, "Second iPhone", "66:77:88:99:AA:BB");
    callback->outputAudio(&audio, "Second iPhone", "66:77:88:99:AA:BB");
    callback->disconnected("Second iPhone", "66:77:88:99:AA:BB");
    return reinterpret_cast<void*>(1);
}

extern "C" void __cdecl stub_stop(void*) {}

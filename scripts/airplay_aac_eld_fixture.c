/* Test-only AAC-ELD fixture generation. FDK is never linked into the receiver. */
#include <fdk-aac/aacenc_lib.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <math.h>
#define CHECK(x) do { if (!(x)) { fprintf(stderr, "fixture failed line %d\n", __LINE__); exit(1); } } while (0)
int main(int argc, char **argv) {
    HANDLE_AACENCODER encoder = NULL;
    AACENC_InfoStruct info = {0};
    FILE *file;
    CHECK(argc == 2);
    CHECK(aacEncOpen(&encoder, 0, 2) == AACENC_OK);
    CHECK(aacEncoder_SetParam(encoder, AACENC_AOT, 39) == AACENC_OK);
    CHECK(aacEncoder_SetParam(encoder, AACENC_SAMPLERATE, 44100) == AACENC_OK);
    CHECK(aacEncoder_SetParam(encoder, AACENC_CHANNELMODE, MODE_2) == AACENC_OK);
    CHECK(aacEncoder_SetParam(encoder, AACENC_BITRATE, 128000) == AACENC_OK);
    CHECK(aacEncoder_SetParam(encoder, AACENC_GRANULE_LENGTH, 480) == AACENC_OK);
    CHECK(aacEncoder_SetParam(encoder, AACENC_TRANSMUX, TT_MP4_RAW) == AACENC_OK);
    CHECK(aacEncoder_SetParam(encoder, AACENC_SBR_MODE, 0) == AACENC_OK);
    CHECK(aacEncEncode(encoder, NULL, NULL, NULL, NULL) == AACENC_OK);
    CHECK(aacEncInfo(encoder, &info) == AACENC_OK && info.frameLength == 480);
    printf("AAC-ELD ASC:");
    for (unsigned i = 0; i < info.confSize; ++i) printf(" %02x", info.confBuf[i]);
    puts("");
    CHECK(info.confSize == 4 && info.confBuf[0] == 0xf8 && info.confBuf[1] == 0xe8 &&
        info.confBuf[2] == 0x50 && info.confBuf[3] == 0x00);
    file = fopen(argv[1], "wb"); CHECK(file);
    for (int frame = 0; frame < 40; ++frame) {
        int16_t pcm[960]; unsigned char bytes[4096];
        void *in_pointer = pcm, *out_pointer = bytes;
        int in_id = IN_AUDIO_DATA, out_id = OUT_BITSTREAM_DATA;
        int in_bytes = sizeof(pcm), out_bytes = sizeof(bytes), in_element = 2, out_element = 1;
        AACENC_BufDesc input = {1, &in_pointer, &in_id, &in_bytes, &in_element};
        AACENC_BufDesc output = {1, &out_pointer, &out_id, &out_bytes, &out_element};
        AACENC_InArgs args = {960}; AACENC_OutArgs result = {0};
        for (int i = 0; i < 480; ++i) {
            pcm[i * 2] = (int16_t)(sin((frame * 480 + i) * 997.0 * 6.283185307179586 / 44100.0) * 12000);
            pcm[i * 2 + 1] = (int16_t)(sin((frame * 480 + i) * 1501.0 * 6.283185307179586 / 44100.0) * 8000);
        }
        CHECK(aacEncEncode(encoder, &input, &output, &args, &result) == AACENC_OK);
        if (result.numOutBytes) {
            uint32_t count = result.numOutBytes;
            CHECK(fwrite(&count, 4, 1, file) == 1);
            CHECK(fwrite(bytes, count, 1, file) == 1);
        }
    }
    fclose(file); aacEncClose(&encoder);
    return 0;
}

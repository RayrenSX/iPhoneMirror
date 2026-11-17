#include "../../../third_party/airplay-server/patches/airplay-request-policy.h"
#include "../../../third_party/airplay-server/patches/airplay-hevc-config.h"
#include "../../../third_party/airplay-server/patches/airplay-scrub-request.h"
#include <stdio.h>

static int test_scrub_requests(void) {
    struct scrub_case { const char *method, *url; int result; double position; } cases[] = {
        {"GET", "/scrub", IM_SCRUB_READ, 0},
        {"GET", "/scrub?position=0", IM_SCRUB_READ, 0},
        {"GET", "/scrub?position=invalid", IM_SCRUB_READ, 0},
        {"POST", "/scrub?position=0", IM_SCRUB_SEEK, 0},
        {"POST", "/scrub?position=12.500000", IM_SCRUB_SEEK, 12.5},
        {"POST", "/scrub?other=3&position=0.125&tail=4", IM_SCRUB_SEEK, 0.125},
        {"POST", "/scrub?pos%69tion=%31%32%2e%35", IM_SCRUB_SEEK, 12.5},
        {"POST", "/scrub?position=.5", IM_SCRUB_SEEK, .5},
        {"POST", "/scrub?position=5.", IM_SCRUB_SEEK, 5},
        {"POST", "/scrub?position=1.25e1", IM_SCRUB_SEEK, 12.5},
        {"POST", "/scrub?position=125E-1", IM_SCRUB_SEEK, 12.5},
        {"POST", "/scrub?position=%2B12.5", IM_SCRUB_SEEK, 12.5},
        {"POST", "/scrub", IM_SCRUB_MISSING, 0},
        {"POST", "/scrub?other=position=0", IM_SCRUB_MISSING, 0},
        {"POST", "/scrub?notposition=0", IM_SCRUB_MISSING, 0},
        {"POST", "/scrub?position", IM_SCRUB_INVALID, 0},
        {"POST", "/scrub?position=", IM_SCRUB_INVALID, 0},
        {"POST", "/scrub?position=bad", IM_SCRUB_INVALID, 0},
        {"POST", "/scrub?position=nan", IM_SCRUB_INVALID, 0},
        {"POST", "/scrub?position=inf", IM_SCRUB_INVALID, 0},
        {"POST", "/scrub?position=-0", IM_SCRUB_INVALID, 0},
        {"POST", "/scrub?position=-1", IM_SCRUB_INVALID, 0},
        {"POST", "/scrub?position=12x", IM_SCRUB_INVALID, 0},
        {"POST", "/scrub?position=0x12", IM_SCRUB_INVALID, 0},
        {"POST", "/scrub?position=1,5", IM_SCRUB_INVALID, 0},
        {"POST", "/scrub?position=+1", IM_SCRUB_INVALID, 0},
        {"POST", "/scrub?position=%201", IM_SCRUB_INVALID, 0},
        {"POST", "/scrub?position=1%20", IM_SCRUB_INVALID, 0},
        {"POST", "/scrub?position=1e", IM_SCRUB_INVALID, 0},
        {"POST", "/scrub?position=1e309", IM_SCRUB_INVALID, 0},
        {"POST", "/scrub?position=1e-400", IM_SCRUB_INVALID, 0},
        {"POST", "/scrub?position=0e999999999999", IM_SCRUB_INVALID, 0},
        {"POST", "/scrub?position=1%00.5", IM_SCRUB_INVALID, 0},
        {"POST", "/scrub?position=1%2", IM_SCRUB_INVALID, 0},
        {"POST", "/scrub?position=1%GG", IM_SCRUB_INVALID, 0},
        {"POST", "/scrub?position=1#fragment", IM_SCRUB_INVALID, 0},
        {"POST", "/scrub?position=0&position=1", IM_SCRUB_DUPLICATE, 0},
        {"POST", "/scrub?position=bad&position=0", IM_SCRUB_DUPLICATE, 0},
        {"POST", "/scrub?position=1&pos%69tion=1", IM_SCRUB_DUPLICATE, 0},
        {"PUT", "/scrub?position=0", IM_SCRUB_METHOD, 0},
        {"HEAD", "/scrub?position=0", IM_SCRUB_METHOD, 0},
        {NULL, "/scrub?position=0", IM_SCRUB_METHOD, 0},
        {"POST", "/scrubber?position=0", IM_SCRUB_NOT_ROUTE, 0},
        {"POST", "/scrub/other?position=0", IM_SCRUB_NOT_ROUTE, 0},
        {"POST", "/scrub#fragment", IM_SCRUB_NOT_ROUTE, 0},
        {"POST", NULL, IM_SCRUB_NOT_ROUTE, 0},
    };
    for (size_t i = 0; i < sizeof(cases) / sizeof(cases[0]); ++i) {
        double position = -1;
        int result = iphone_mirror_scrub_request(cases[i].method, cases[i].url, &position);
        double difference = position - cases[i].position;
        if (result != cases[i].result || difference < -0.00000001 || difference > 0.00000001) {
            fprintf(stderr, "Scrub case %zu failed: result=%d position=%.9g\n", i, result, position);
            return 0;
        }
    }
    char oversized[4300];
    memset(oversized, '1', sizeof(oversized));
    memcpy(oversized, "/scrub?position=", 16); oversized[sizeof(oversized)-1] = 0;
    double position;
    if (iphone_mirror_scrub_request("POST", oversized, &position) != IM_SCRUB_INVALID ||
        iphone_mirror_scrub_milliseconds(12.5) != 12500 ||
        iphone_mirror_scrub_milliseconds(DBL_MAX) != INT_MAX) return 0;
    puts("Scrub GET/POST separation, exact route/query, zero/decimal, encoding and numeric bounds pass");
    return 1;
}

static void be32(unsigned char *p, unsigned int n) {
    p[0] = n >> 24; p[1] = n >> 16; p[2] = n >> 8; p[3] = n;
}
int main(void) {
    if (!test_scrub_requests()) return 11;
    if (!iphone_mirror_request_headers_valid("POST", "/pair-verify", "RTSP/1.0", NULL, 0) ||
        !iphone_mirror_request_headers_valid("POST", "/pair-setup", "RTSP/1.0", NULL, 0) ||
        !iphone_mirror_request_headers_valid("POST", "/fp-setup", "RTSP/1.0", NULL, 0) ||
        iphone_mirror_request_headers_valid("GET", "/pair-verify", "RTSP/1.0", NULL, 0) ||
        iphone_mirror_request_headers_valid("POST", "/pair-verify/other", "RTSP/1.0", NULL, 0)) return 10;
    if (!iphone_mirror_request_headers_valid("POST", "/pair-verify", "HTTP/1.1", NULL, 0) ||
        !iphone_mirror_request_headers_valid("GET", "/info", "HTTP/1.0", NULL, 0) ||
        !iphone_mirror_request_headers_valid("SETUP", "rtsp://test", "RTSP/1.0", "1", 0) ||
        iphone_mirror_request_headers_valid("SETUP", "rtsp://test", "RTSP/1.0", NULL, 0) ||
        iphone_mirror_request_headers_valid(NULL, "/info", "HTTP/1.1", NULL, 0) ||
        iphone_mirror_request_headers_valid("POST", "/info", NULL, NULL, 1)) return 1;
    unsigned char config[138] = {0};
    be32(config, sizeof(config)); memcpy(config+4, "hvc1", 4);
    be32(config+86, sizeof(config)-86); memcpy(config+90, "hvcC", 4);
    config[94] = 1; config[115] = 3; config[116] = 3;
    for (int i = 0; i < 3; ++i) {
        int offset = 117 + i*7;
        config[offset] = 0x80 + 32 + i;
        config[offset+2] = 1; config[offset+4] = 2;
        config[offset+5] = (32+i)*2; config[offset+6] = 1;
    }
    int length;
    unsigned char *out = iphone_mirror_hevc_parameter_sets(config, sizeof(config), &length);
    if (!out || length != 18) return 2;
    for (int i = 0; i < 3; ++i)
        if (memcmp(out+i*6, "\0\0\0\1", 4) || out[i*6+4] != (32+i)*2 || out[i*6+5] != 1) return 3;
    free(out);
    for (size_t n = 0; n < sizeof(config); ++n) {
        out = iphone_mirror_hevc_parameter_sets(config, n, &length);
        if (out || length) return 4;
    }
    config[120] = 255;
    out = iphone_mirror_hevc_parameter_sets(config, sizeof(config), &length);
    if (out || length) return 5;
    config[120] = 0; config[116] = 2;
    out = iphone_mirror_hevc_parameter_sets(config, sizeof(config), &length);
    if (out || length) return 6;
    puts("HTTP/RTSP header policy and HEVC configuration bounds pass");
    return 0;
}

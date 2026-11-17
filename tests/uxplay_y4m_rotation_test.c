// SPDX-License-Identifier: GPL-3.0-only
// Build with pkg-config --cflags --libs gstreamer-app-1.0 gstreamer-video-1.0.
#include "../src/UxPlayHost/UxPlayY4mRotation.h"
#include <gst/app/gstappsrc.h>
#include <gst/app/gstappsink.h>
#include <gst/video/video.h>
#include <stdio.h>

int main(int argc, char **argv)
{
    gst_init(&argc, &argv);
    GError *error = NULL;
    GstElement *pipeline = gst_parse_launch(
        "appsrc name=input format=time ! y4menc name=iphone_mirror_y4m ! "
        "appsink name=output sync=false", &error);
    if (error) { fprintf(stderr, "%s\n", error->message); return 1; }
    if (argc < 2 || strcmp(argv[1], "--unpatched") != 0)
        iphone_mirror_y4m_rotation_install(pipeline);
    GstElement *source = gst_bin_get_by_name(GST_BIN(pipeline), "input");
    GstElement *sink = gst_bin_get_by_name(GST_BIN(pipeline), "output");
    gst_element_set_state(pipeline, GST_STATE_PLAYING);
    const int widths[] = {498, 498, 1920, 1920, 1080, 498};
    const int heights[] = {1080, 1080, 1080, 1080, 498, 1080};
    int result = 0;
    for (int n = 0; n < 6; ++n) {
        int w = widths[n], h = heights[n];
        GstVideoInfo info;
        gst_video_info_set_format(&info, GST_VIDEO_FORMAT_I420, w, h);
        info.fps_n = 30; info.fps_d = 1;
        GstCaps *caps = gst_video_info_to_caps(&info);
        gst_app_src_set_caps(GST_APP_SRC(source), caps);
        gst_caps_unref(caps);
        GstBuffer *input = gst_buffer_new_allocate(NULL, info.size, NULL);
        GstVideoFrame frame;
        gst_video_frame_map(&frame, &info, input, GST_MAP_WRITE);
        for (int p = 0; p < 3; ++p)
            for (int row = 0; row < (p ? h / 2 : h); ++row)
                memset((guint8 *)GST_VIDEO_FRAME_PLANE_DATA(&frame, p) +
                    row * GST_VIDEO_FRAME_PLANE_STRIDE(&frame, p),
                    32 + n * 8 + p * 64, p ? w / 2 : w);
        gst_video_frame_unmap(&frame);
        GST_BUFFER_PTS(input) = n * GST_SECOND / 30;
        GST_BUFFER_DURATION(input) = GST_SECOND / 30;
        gst_app_src_push_buffer(GST_APP_SRC(source), input);
        GstSample *sample = gst_app_sink_try_pull_sample(GST_APP_SINK(sink), 3 * GST_SECOND);
        if (!sample) { fprintf(stderr, "No output at frame %d\n", n); result = 2; break; }
        GstMapInfo map;
        gst_buffer_map(gst_sample_get_buffer(sample), &map, GST_MAP_READ);
        size_t offset = 0;
        int changed = n == 0 || widths[n-1] != w || heights[n-1] != h;
        if (changed) {
            const guint8 *end = memchr(map.data, '\n', map.size);
            gchar *line = end ? g_strndup((const gchar *)map.data, end-map.data) : g_strdup("");
            gchar *ws = g_strdup_printf(" W%d ", w), *hs = g_strdup_printf(" H%d ", h);
            if (!g_str_has_prefix(line, "YUV4MPEG2 ") || !strstr(line, ws) || !strstr(line, hs)) {
                fprintf(stderr, "Missing size header for frame %d (%dx%d)\n", n, w, h); result = 3;
            }
            offset = end ? end-map.data+1 : 0;
            g_free(line); g_free(ws); g_free(hs);
        }
        if (!result && (map.size != offset + 6 + (size_t)w*h*3/2 ||
            memcmp(map.data+offset, "FRAME\n", 6) != 0)) result = 4;
        offset += 6;
        if (!result) for (int p = 0; p < 3; ++p) {
            size_t count = (size_t)w*h/(p ? 4 : 1);
            for (size_t i = 0; i < count; ++i)
                if (map.data[offset+i] != 32+n*8+p*64) { result = 5; break; }
            offset += count;
        }
        gst_buffer_unmap(gst_sample_get_buffer(sample), &map);
        gst_sample_unref(sample);
        if (result) break;
    }
    gst_element_set_state(pipeline, GST_STATE_NULL);
    gst_object_unref(source); gst_object_unref(sink); gst_object_unref(pipeline);
    if (!result) puts("PASS: six frames, three size changes, packed Y/U/V pixels preserved");
    return result;
}

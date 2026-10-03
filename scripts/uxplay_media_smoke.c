/* Execute the packaged GStreamer decoder/plugins, without a phone or sound device. */
#include <gst/gst.h>
#include <gst/app/gstappsrc.h>
#include <stdio.h>
#include <stdlib.h>

int main(int argc, char **argv) {
    if (argc != 3 && argc != 4) return 2;
    GError *error = NULL;
    if (!gst_init_check(NULL, NULL, &error)) {
        fprintf(stderr, "GStreamer initialization: %s\n", error ? error->message : "failed");
        return 3;
    }
    GstElement *pipeline = gst_parse_launch(argv[1], &error);
    if (!pipeline || error) {
        fprintf(stderr, "Pipeline: %s\n", error ? error->message : "failed");
        return 4;
    }
    GstBus *bus = gst_element_get_bus(pipeline);
    if (gst_element_set_state(pipeline, GST_STATE_PLAYING) == GST_STATE_CHANGE_FAILURE) return 5;
    if (argc == 4) {
        /* Feed complete AAC-LC frames as UxPlay does, using its raw AAC caps. */
        FILE *input = fopen(argv[3], "rb");
        if (!input) return 6;
        GstElement *source = gst_bin_get_by_name(GST_BIN(pipeline), "audio_source");
        unsigned char header[7];
        guint64 frame = 0;
        while (fread(header, 1, 7, input) == 7) {
            if (header[0] != 0xff || (header[1] & 0xf6) != 0xf0 || !(header[1] & 1)) return 7;
            size_t size = ((size_t)(header[3] & 3) << 11) | ((size_t)header[4] << 3) | (header[5] >> 5);
            if (size < 7 || size > 8191) return 8;
            size -= 7;
            unsigned char bytes[8192];
            if (fread(bytes, 1, size, input) != size) return 9;
            GstBuffer *buffer = gst_buffer_new_allocate(NULL, size, NULL);
            gst_buffer_fill(buffer, 0, bytes, size);
            GST_BUFFER_PTS(buffer) = gst_util_uint64_scale(frame++ * 1024, GST_SECOND, 44100);
            GST_BUFFER_DURATION(buffer) = gst_util_uint64_scale(1024, GST_SECOND, 44100);
            if (gst_app_src_push_buffer(GST_APP_SRC(source), buffer) != GST_FLOW_OK) return 10;
        }
        fclose(input);
        gst_app_src_end_of_stream(GST_APP_SRC(source));
        gst_object_unref(source);
    }
    GstMessage *message = gst_bus_timed_pop_filtered(bus, 15 * GST_SECOND, GST_MESSAGE_EOS | GST_MESSAGE_ERROR);
    int result = 0;
    if (!message) {
        fprintf(stderr, "Pipeline timed out\n"); result = 11;
    } else if (GST_MESSAGE_TYPE(message) == GST_MESSAGE_ERROR) {
        gchar *debug = NULL;
        gst_message_parse_error(message, &error, &debug);
        fprintf(stderr, "%s: %s\n", error->message, debug ? debug : "");
        g_clear_error(&error); g_free(debug); result = 12;
    }
    if (message) gst_message_unref(message);
    gst_element_set_state(pipeline, GST_STATE_NULL);
    gst_object_unref(bus); gst_object_unref(pipeline);
    if (!result) printf("PASS packaged UxPlay %s decode pipeline reached EOS\n", argv[2]);
    return result;
}

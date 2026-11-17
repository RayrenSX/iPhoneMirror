// SPDX-License-Identifier: GPL-3.0-only
#pragma once
#include <gst/gst.h>
#include <string.h>

/* y4menc emits its stream header only once, even after input caps change.
 * Our private pipe accepts concatenated Y4M streams. Insert a new header at
 * the encoded buffer boundary, using the caps of that exact input frame. */
typedef struct { gint width, height; } IphoneMirrorY4mSize;

static GstPadProbeReturn iphone_mirror_y4m_buffer(GstPad *pad,
    GstPadProbeInfo *probe, gpointer data)
{
    IphoneMirrorY4mSize *size = data;
    GstBuffer *buffer = GST_PAD_PROBE_INFO_BUFFER(probe);
    GstElement *encoder = gst_pad_get_parent_element(pad);
    GstPad *sink = gst_element_get_static_pad(encoder, "sink");
    GstCaps *caps = gst_pad_get_current_caps(sink);
    gint width = 0, height = 0, fps_n = 0, fps_d = 1;
    if (caps) {
        const GstStructure *s = gst_caps_get_structure(caps, 0);
        const gchar *format = gst_structure_get_string(s, "format");
        if (g_strcmp0(format, "I420") == 0) {
            gst_structure_get_int(s, "width", &width);
            gst_structure_get_int(s, "height", &height);
            gst_structure_get_fraction(s, "framerate", &fps_n, &fps_d);
        }
        gst_caps_unref(caps);
    }
    gst_object_unref(sink);
    gst_object_unref(encoder);
    if (width > 0 && height > 0 &&
        (width != size->width || height != size->height)) {
        gchar prefix[10] = {0};
        gst_buffer_extract(buffer, 0, prefix, sizeof(prefix));
        if (memcmp(prefix, "YUV4MPEG2 ", sizeof(prefix)) != 0) {
            gchar *text = g_strdup_printf(
                "YUV4MPEG2 W%d H%d F%d:%d Ip A0:0 C420\n",
                width, height, fps_n, fps_d);
            GstBuffer *header = gst_buffer_new_allocate(NULL, strlen(text), NULL);
            gst_buffer_fill(header, 0, text, strlen(text));
            g_free(text);
            gst_buffer_copy_into(header, buffer, GST_BUFFER_COPY_METADATA, 0, -1);
            GST_PAD_PROBE_INFO_DATA(probe) = gst_buffer_append(header, buffer);
        }
        size->width = width;
        size->height = height;
    }
    return GST_PAD_PROBE_OK;
}

static void iphone_mirror_y4m_rotation_install(GstElement *pipeline)
{
    GstElement *encoder = gst_bin_get_by_name(GST_BIN(pipeline), "iphone_mirror_y4m");
    if (!encoder) return; /* Only the iPhoneMirror private pipe opts in. */
    GstPad *src = gst_element_get_static_pad(encoder, "src");
    gst_pad_add_probe(src, GST_PAD_PROBE_TYPE_BUFFER, iphone_mirror_y4m_buffer,
        g_new0(IphoneMirrorY4mSize, 1), g_free);
    gst_object_unref(src);
    gst_object_unref(encoder);
}

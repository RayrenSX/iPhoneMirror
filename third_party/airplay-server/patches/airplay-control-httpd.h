/* SPDX-License-Identifier: LGPL-3.0-or-later
 * Included after httpd_remove_connection. Only the independent HTTP video
 * server opts into this transport; RAOP/mirroring keep their existing path. */
#include "airplay-control.h"
static void im_control_failure(httpd_t *server,http_connection_t *conn,int stage,int detail,int length)
{
    int reverse=server->callbacks.conn_is_reverse && server->callbacks.conn_is_reverse(conn->user_data);
    logger_log(server->logger,LOGGER_INFO,"IPHONE_MIRROR_VIDEO_DIAG 28 %d %d %d %d %d 0",stage,detail,reverse,length,conn->control_request_bytes);
}
static int im_control_send(httpd_t *server, http_connection_t *conn, const char *data, int length, uint64_t handle)
{
    unsigned char *record = NULL;
    int offset = 0, ok = 1;
    (void)server;
    if (handle) {
        if (!im_control_available()) return 0;
        record = malloc(IM_CONTROL_MAX + 20); if (!record) return 0;
    }
    while (offset < length && ok) {
        const char *wire = data + offset;
        int count = length - offset, sent = 0;
        if (count > IM_CONTROL_MAX) count = IM_CONTROL_MAX;
        offset += count;
        if (handle) {
            count = im_control_run(handle,4,wire,count,record,IM_CONTROL_MAX+20,NULL,0);
            wire = (const char *)record;
            if (count < 0) { im_control_failure(server,conn,4,count,length);ok = 0; break; }
        }
        while (sent < count) {
            int n = send(conn->socket_fd,wire+sent,count-sent,0);
            if (n <= 0) { im_control_failure(server,conn,5,n,length);ok = 0; break; }
            sent += n;
        }
    }
    free(record); return ok;
}
static int im_control_dispatch(httpd_t *server, http_connection_t *conn, uint64_t encrypted)
{
    if (server->callbacks.conn_is_reverse && server->callbacks.conn_is_reverse(conn->user_data) && http_request_is_response(conn->request)) {
        int state=-1, current=0, body_length=0;
        unsigned int sequence=0;
        if (conn->control_event_count) {
            unsigned int slot=conn->control_event_head;
            state=conn->control_event_states[slot];
            current=state && conn->control_event_items[slot]==conn->control_last_event_item;
            sequence=conn->control_event_sequences[slot];
            conn->control_event_head=(slot+1)%64;
            conn->control_event_count--;
        }
        http_request_get_data(conn->request,&body_length);
        /* FIFO correlation for ordered reverse responses. Numeric state and
           a connection-local sequence expose rejected/missing completion ACKs
           without logging protocol session identifiers, headers or bodies. */
        logger_log(server->logger,LOGGER_INFO,"IPHONE_MIRROR_VIDEO_DIAG 31 %d %d %d %u %d %d",
            http_request_get_status(conn->request),state,current,sequence,encrypted!=0,body_length);
        http_request_destroy(conn->request);conn->request=NULL;conn->control_request_bytes=0;return 1;
    }
    http_response_t *response = NULL;
    const char *url = http_request_get_url(conn->request);
    int body_length = 0, ok = 1;
    http_request_get_data(conn->request,&body_length);
    /* Fixed route IDs only: no URLs, keys, tokens or bodies in diagnostics. */
    int route = !url ? 0 : !strcmp(url,"/fp-setup") ? 4 : !strcmp(url,"/fp-setup2") ? 11 :
        !strcmp(url,"/play") ? 5 : !strcmp(url,"/reverse") ? 9 : !strcmp(url,"/pair-verify") ? 3 :
        !strcmp(url,"/playback-info") ? 14 : 10;
    logger_log(server->logger,LOGGER_INFO,"IPHONE_MIRROR_VIDEO_DIAG 22 %d %d %d 0 0 0",route,body_length,encrypted != 0);
    server->callbacks.conn_request(conn->user_data,conn->request,&response);
    http_request_destroy(conn->request); conn->request = NULL; conn->control_request_bytes = 0;
    if (response) {
        int length = 0;
        const char *data = http_response_get_data(response,&length);
        ok = im_control_send(server,conn,data,length,encrypted);
        if (http_response_get_disconnect(response)) {im_control_failure(server,conn,6,0,length);ok = 0;}
        http_response_destroy(response);
    } else {im_control_failure(server,conn,7,0,0);ok = 0;}
    return ok;
}
static int im_control_plain(httpd_t *server, http_connection_t *conn, const char *data, int length, uint64_t encrypted)
{
    int i;
    /* Parsing a byte at a time is limited to the low-volume control connection.
       It stops precisely at the upgrade boundary without losing trailing bytes. */
    for (i=0;i<length;i++) {
        if (!conn->request) conn->request = server->callbacks.conn_is_reverse && server->callbacks.conn_is_reverse(conn->user_data) ? http_request_init_response() : http_request_init();
        if (!conn->request || ++conn->control_request_bytes > 1048576) {im_control_failure(server,conn,1,0,length);return 0;}
        if (http_request_add_data(conn->request,data+i,1) != 1 || http_request_has_error(conn->request)) {
            const char *name=http_request_get_error_name(conn->request);
            int detail=!strcmp(name,"HPE_INVALID_METHOD")?1:!strcmp(name,"HPE_INVALID_CONSTANT")?2:
                !strcmp(name,"HPE_INVALID_VERSION")?3:!strcmp(name,"HPE_INVALID_HEADER_TOKEN")?4:
                !strcmp(name,"HPE_INVALID_CONTENT_LENGTH")?5:!strcmp(name,"HPE_CLOSED_CONNECTION")?6:99;
            /* Only classify the failed message prefix, never headers/body/URL.
               encrypted distinguishes authenticated plaintext from raw input;
               offset reveals trailing bytes after an earlier complete message. */
            if (conn->control_request_bytes == 1) {
                int a=(unsigned char)data[i];
                int b=i+1<length?(unsigned char)data[i+1]:-1;
                int c=i+2<length?(unsigned char)data[i+2]:-1;
                int d=i+3<length?(unsigned char)data[i+3]:-1;
                logger_log(server->logger,LOGGER_INFO,"IPHONE_MIRROR_VIDEO_DIAG 29 %d %d %d %d %d %d",encrypted!=0,i,a,b,c,d);
            }
            im_control_failure(server,conn,2,detail,length);return 0;
        }
        if (http_request_is_complete(conn->request) && !im_control_dispatch(server,conn,encrypted)) return 0;
    }
    return 1;
}
static int im_control_tick(httpd_t *server,http_connection_t *conn)
{
    char *buffer;
    int count,ok;
    if (!server->callbacks.conn_is_reverse || !server->callbacks.conn_is_reverse(conn->user_data)) return 1;
    buffer=malloc(65537);if(!buffer)return 0;
    count=server->callbacks.conn_poll(conn->user_data,buffer,65536);
    ok=count>=0 && count<=65536;
    if(count>0 && ok) {
        buffer[count]=0;
        logger_log(server->logger,LOGGER_INFO,"IPHONE_MIRROR_VIDEO_DIAG 27 %d 0 0 0 0 0",count);
        ok=im_control_send(server,conn,buffer,count,server->callbacks.conn_control(conn->user_data));
        if (ok) {
            unsigned int slot;
            int state=0;
            unsigned long long item=0;
            if (strstr(buffer,"<key>category</key><string>video</string>")) {
                if (server->callbacks.conn_event_item)
                    item=server->callbacks.conn_event_item(conn->user_data);
                state=strstr(buffer,"<string>playing</string>")?1:
                    strstr(buffer,"<string>paused</string>")?2:
                    strstr(buffer,"<string>loading</string>")?3:
                    strstr(buffer,"<string>ended</string>")?6:
                    strstr(buffer,"<string>error</string>")?5:4;
                conn->control_last_event_item=item;
            }
            if (conn->control_event_count==64) {
                /* Losing diagnostic correlation must not close a healthy
                   stream. Drop the oldest tracking record, not wire data. */
                conn->control_event_head=(conn->control_event_head+1)%64;
                conn->control_event_count--;
            }
            slot=(conn->control_event_head+conn->control_event_count++)%64;
            conn->control_event_states[slot]=(unsigned char)state;
            conn->control_event_items[slot]=item;
            conn->control_event_sequences[slot]=++conn->control_event_sequence;
        }
    }
    free(buffer);return ok;
}
static int im_control_receive(httpd_t *server, http_connection_t *conn, const char *data, int length)
{
    int offset = 0;
    while (offset < length) {
        uint64_t handle = server->callbacks.conn_control(conn->user_data);
        if (handle) {
            unsigned char *plain = malloc(IM_CONTROL_OUTPUT);
            int count, ok;
            if (!plain || !im_control_available()) { free(plain); return 0; }
            count = im_control_run(handle,3,data+offset,length-offset,plain,IM_CONTROL_OUTPUT,NULL,0);
            if(count<0) im_control_failure(server,conn,3,count,length-offset);
            ok = count >= 0 && im_control_plain(server,conn,(const char *)plain,count,handle);
            free(plain); return ok;
        }
        if (!im_control_plain(server,conn,data+offset,1,0)) return 0;
        offset++;
    }
    return 1;
}

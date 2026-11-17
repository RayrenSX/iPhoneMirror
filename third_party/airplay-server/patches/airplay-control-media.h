/* SPDX-License-Identifier: LGPL-3.0-or-later */
static int im_media_bind(airplay_conn_t *conn,http_request_t *request)
{
    const char *id = http_request_get_header(request,"X-Apple-Session-ID");
    if (!conn->control_handle || !im_control_available()) return 0;
    if (!id) return conn->media_bound;
    if (strlen(id)>128 || im_media_run(conn->control_handle,1,id,(int)strlen(id),NULL,0)<0) return 0;
    strncpy(conn->media_session_id,id,sizeof(conn->media_session_id)-1);
    conn->media_bound=1;return 1;
}
static int im_media_action(airplay_conn_t *conn,http_request_t *request)
{
    int length=0,result=-1;
    const char *body=http_request_get_data(request,&length);
    plist_t root=iphone_mirror_play_request(body,length),params,node;
    char *type=NULL,*data=NULL;
    uint64_t id=0,status=0,size=0;
    unsigned char *wire=NULL;
    if (!root || !PLIST_IS_DICT(root)) goto done;
    node=plist_dict_get_item(root,"type");if (PLIST_IS_STRING(node)) plist_get_string_val(node,&type);
    if (!type || strcmp(type,"unhandledURLResponse")) {result=0;goto done;}
    params=plist_dict_get_item(root,"params");if (!PLIST_IS_DICT(params)) goto done;
    node=plist_dict_get_item(params,"FCUP_Response_RequestID");if (!PLIST_IS_UINT(node)) goto done;plist_get_uint_val(node,&id);
    node=plist_dict_get_item(params,"FCUP_Response_StatusCode");if (!PLIST_IS_UINT(node)) goto done;plist_get_uint_val(node,&status);
    node=plist_dict_get_item(params,"FCUP_Response_Data");
    if (PLIST_IS_DATA(node)) plist_get_data_val(node,&data,&size);
    if (status>=300 && status<400) {
        plist_t headers=plist_dict_get_item(params,"FCUP_Response_Headers");
        free(data);data=NULL;size=0;
        if (PLIST_IS_DICT(headers)) {
            node=plist_dict_get_item(headers,"Location");
            if (!PLIST_IS_STRING(node)) node=plist_dict_get_item(headers,"location");
            if (PLIST_IS_STRING(node)) plist_get_string_val(node,&data);
            if (data) size=strlen(data);
        }
    }
    if (id>0xffffffffULL || status>599 || (status!=0 && status<100) || size>1048568 || (size && !data) || !im_media_bind(conn,request)) goto done;
    wire=malloc((size_t)size+8);if (!wire) goto done;
    wire[0]=(unsigned char)(id>>24);wire[1]=(unsigned char)(id>>16);wire[2]=(unsigned char)(id>>8);wire[3]=(unsigned char)id;
    wire[4]=(unsigned char)(status>>24);wire[5]=(unsigned char)(status>>16);wire[6]=(unsigned char)(status>>8);wire[7]=(unsigned char)status;
    if (size) memcpy(wire+8,data,(size_t)size);
    result=im_media_run(conn->control_handle,4,wire,(int)size+8,NULL,0);
done:
    logger_log(conn->airplay->logger,LOGGER_INFO,"IPHONE_MIRROR_VIDEO_DIAG 26 %d %d %d 0 0 0",result,(int)status,(int)size);
    free(wire);free(type);free(data);if(root)plist_free(root);return result;
}
static int im_media_reverse(void *ptr) {return ((airplay_conn_t *)ptr)->media_reverse;}
static uint64_t im_media_event_item(void *ptr) {return ((airplay_conn_t *)ptr)->media_event_command;}
static void im_media_claim(airplay_conn_t *conn,http_request_t *request)
{
    const char *id=http_request_get_header(request,"X-Apple-Session-ID");
    if (!id && conn->media_bound) id=conn->media_session_id;
    if (!id || strlen(id)>128) return;
    if (conn->control_handle) im_media_bind(conn,request);
    strncpy(conn->airplay->media_active_session_id,id,
        sizeof(conn->airplay->media_active_session_id)-1);
}
static int im_media_owns(airplay_conn_t *conn,http_request_t *request)
{
    const char *id=request ? http_request_get_header(request,"X-Apple-Session-ID") : NULL;
    if (!id && conn->media_bound) id=conn->media_session_id;
    return id && conn->airplay->media_active_session_id[0] &&
        !strcmp(id,conn->airplay->media_active_session_id);
}
static int im_media_play_state(airplay_conn_t *conn,unsigned long long *command)
{
    int state=0;
    *command=0;
    if (conn->airplay->callbacks.video_get_play_state)
        state=conn->airplay->callbacks.video_get_play_state(conn->airplay->callbacks.cls,command);
    return state>=0 && state<=6 ? state : 0;
}
static int im_media_poll(void *ptr,char *output,int capacity)
{
    airplay_conn_t *conn=ptr;
    if (!conn->media_reverse || !conn->media_bound || !im_control_available()) return 0;
    if (im_media_owns(conn,NULL) && conn->airplay->callbacks.video_get_play_info) {
        double duration=0,position=0,rate=0;
        unsigned long long command=0;
        int state=im_media_play_state(conn,&command);
        conn->airplay->callbacks.video_get_play_info(conn->airplay->callbacks.cls,&duration,&position,&rate);
        /* A zero clock rate also occurs while buffering or seeking. Only the
           renderer may declare completion; proximity to duration is no proof. */
        if (!state && command) state=rate>0 ? 1 : duration>0 ? 2 : 3;
        if (state && (state!=conn->media_event_state || command!=conn->media_event_command)) {
            const char *names[]={"", "playing", "paused", "loading", "stopped", "stopped", "stopped"};
            /* Apple TV video events carry an integer sessionID. A natural
               item completion explicitly says reason=ended; a bare stopped
               state also describes user/session stop and must not advance a
               sender's play queue. Failed decoding is a distinct reason. */
            const char *reason=state==6 ? "<key>reason</key><string>ended</string>" :
                state==5 ? "<key>reason</key><string>error</string>" : "";
            char body[384];
            /* The integer protocol session is the same receiver session used
               by FCUP resource messages. The item's command identity remains
               internal for deduplication; X-Apple-Session-ID routes peers. */
            int size=snprintf(body,sizeof(body),"<plist version=\"1.0\"><dict><key>category</key><string>video</string><key>sessionID</key><integer>1</integer><key>state</key><string>%s</string>%s</dict></plist>",names[state],reason);
            int length=snprintf(output,capacity,"POST /event HTTP/1.1\r\nContent-Type: text/x-apple-plist+xml\r\nX-Apple-Session-ID: %s\r\nContent-Length: %d\r\n\r\n%s",conn->media_session_id,size,body);
            if (length<0 || length>=capacity) return 0;
            conn->media_event_state=state;
            conn->media_event_command=command;
            logger_log(conn->airplay->logger,LOGGER_INFO,"IPHONE_MIRROR_VIDEO_DIAG 30 %d 0 0 0 0 0",state);
            return length;
        }
    }
    /* State transitions are bounded to one message. Deliver them before a
       busy resource queue so the completion needed for the next ad cannot
       be starved by playlist polling. */
    return im_media_run(conn->control_handle,2,NULL,0,output,capacity);
}

static int im_media_playback_info(airplay_conn_t *conn,http_request_t *request,char **output,int *length,
    double duration,double position,double rate)
{
    unsigned long long command=0;
    int state=im_media_play_state(conn,&command);
    if (conn->airplay->media_active_session_id[0] && !im_media_owns(conn,request)) {
        const char *not_ready="<plist version=\"1.0\"><dict><key>readyToPlay</key><false/></dict></plist>";
        *length=(int)strlen(not_ready);*output=malloc((size_t)*length+1);
        if (!*output) return 0;
        memcpy(*output,not_ready,(size_t)*length+1);return 1;
    }
    const char *loading=state==3 ? "true" : "false";
    const char *ready=state==1 || state==2 || state==4 || state==6 ? "true" : "false";
    if (!state) return 0;
    char *data=malloc(2048);
    if (!data) return 0;
    *length=snprintf(data,2048,
        "<plist version=\"1.0\"><dict><key>duration</key><real>%.6f</real>"
        "<key>position</key><real>%.6f</real><key>rate</key><real>%.6f</real>"
        "<key>readyToPlay</key><%s/><key>playbackBufferEmpty</key><%s/>"
        "<key>playbackBufferFull</key><false/><key>playbackLikelyToKeepUp</key><%s/>"
        "<key>loadedTimeRanges</key><array></array><key>seekableTimeRanges</key>"
        "<array><dict><key>start</key><real>0</real><key>duration</key><real>%.6f</real>"
        "</dict></array></dict></plist>",duration,position,rate,ready,loading,ready,duration);
    *output=data;
    return 1;
}

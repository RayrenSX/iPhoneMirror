param([Parameter(Mandatory=$true)][string]$SourceRoot)
$ErrorActionPreference = 'Stop'
$lib = Join-Path $SourceRoot 'AirPlayServerLib/lib'
foreach ($name in @('airplay-control.h','airplay-control-httpd.h','airplay-control-media.h','airplay-scrub-request.h')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination (Join-Path $lib $name) -Force
}
function Edit-Source([string]$Name, [scriptblock]$Edit) {
    $path = Join-Path $lib $Name
    $text = [IO.File]::ReadAllText($path).Replace("`r`n","`n")
    $text = & $Edit $text
    [IO.File]::WriteAllText($path,$text,[Text.UTF8Encoding]::new($false))
}
Edit-Source 'httpd.h' { param($s)
    if (!$s.Contains('conn_control')) {
        $s = $s.Replace('#include "logger.h"',"#include <stdint.h>`n#include `"logger.h`"")
        $s = $s.Replace('void  (*conn_destroy)(void *ptr);',"void  (*conn_destroy)(void *ptr);`n    uint64_t (*conn_control)(void *ptr);")
    }; $s
}
foreach ($name in @('airplay.c','raop.c')) {
    Edit-Source $name { param($s) $s.Replace('httpd_callbacks_t httpd_cbs;', 'httpd_callbacks_t httpd_cbs = {0};') }
}
Edit-Source 'httpd.c' { param($s)
    if (!$s.Contains('airplay-control-httpd.h')) {
        $s = $s.Replace('http_request_t *request;',"http_request_t *request;`n    int control_request_bytes;")
        $s = $s.Replace('connection->connected = 0;',"connection->connected = 0;`n    connection->control_request_bytes = 0;")
        $s = $s.Replace("static THREAD_RETVAL`nhttpd_thread", "#include `"airplay-control-httpd.h`"`n`nstatic THREAD_RETVAL`nhttpd_thread")
        $s = $s.Replace('/* Parse HTTP request from data read from connection */', @'
if (httpd->callbacks.conn_control) {
                if (!im_control_receive(httpd,connection,buffer,ret)) {
                    logger_log(httpd->logger,LOGGER_INFO,"IPHONE_MIRROR_VIDEO_DIAG 23 1 0 0 0 0 0");
                    httpd_remove_connection(httpd,connection);
                }
                continue;
            }
            /* Parse HTTP request from data read from connection */
'@)
    }; $s
}
Edit-Source 'httpd.c' {param($s)
    if (!$s.Contains('control_event_states[64]')) {
        $s=$s.Replace('int control_request_bytes;',@'
int control_request_bytes;
    unsigned char control_event_states[64];
    unsigned long long control_event_items[64];
    unsigned int control_event_sequences[64];
    unsigned int control_event_head, control_event_count, control_event_sequence;
    unsigned long long control_last_event_item;
'@)
        $s=$s.Replace('connection->connected = 0;',"connection->connected = 0;`n    connection->control_event_head=connection->control_event_count=connection->control_event_sequence=0;`n    connection->control_last_event_item=0;")
    };$s
}
Edit-Source 'airplay.c' { param($s)
    if (!$s.Contains('IPHONE_MIRROR_CONTROL_UPGRADE')) {
        $s = $s.Replace('#include "fairplay.h"',"#include `"fairplay.h`"`n#include `"airplay-control.h`"")
        $s = $s.Replace('pairing_session_t* pairing;',"pairing_session_t* pairing;`n    uint64_t control_handle;`n    int control_active;`n    int control_verified;")
        $start = $s.IndexOf('/* IPHONE_MIRROR_UNSUPPORTED_FP_SETUP2:')
        $end = $s.IndexOf('*response = http_response_init(protocol, 200, "OK");',$start)
        if ($start -lt 0 -or $end -lt 0) { throw 'fp-setup2 patch anchor missing' }
        $s = $s.Substring(0,$start) + @'
/* IPHONE_MIRROR_CONTROL_UPGRADE: only the independent video endpoint. */
    if (!strcmp(method,"POST") && (!strcmp(url,"/fp-setup") || !strcmp(url,"/fp-setup2"))) {
        const char *mode = getenv("IPHONE_MIRROR_AIRPLAY_MODE");
        const char *pd = http_request_get_header(request,"X-Apple-PD");
        int length = 0, result = -1, setup2 = !strcmp(url,"/fp-setup2");
        const char *body = http_request_get_data(request,&length);
        unsigned char output[142], secret[32] = {0};
        int mixed = pd && !strcmp(pd,"1");
        if (mode && !strcmp(mode,"media") && im_control_available() && !conn->control_active) {
            if (!conn->control_handle && !setup2) conn->control_handle = im_control_create();
            if (!mixed || conn->control_verified) {
                if (mixed) pairing_get_ecdh_secret_key(conn->pairing,secret);
                result = im_control_run(conn->control_handle,setup2 ? 2 : 1,body,length,output,sizeof(output),secret,mixed);
            }
        }
        SecureZeroMemory(secret,sizeof(secret));
        *response = http_response_init(protocol,result >= 0 ? 200 : 421,result >= 0 ? "OK" : "Misdirected Request");
        if (cseq) http_response_add_header(*response,"CSeq",cseq);
        http_response_add_header(*response,"Content-Type","application/octet-stream");
        if (result < 0) http_response_set_disconnect(*response,1);
        http_response_finish(*response,result > 0 ? (const char *)output : NULL,result > 0 ? result : 0);
        if (result >= 0 && setup2) conn->control_active = 1;
        logger_log(airplay->logger,LOGGER_INFO,"IPHONE_MIRROR_VIDEO_DIAG 24 %d %d %d %d 0 0",setup2,result,mixed,conn->control_verified);
        return;
    }
    
'@ + $s.Substring($end)
        $s = $s.Replace('fairplay_destroy(conn->fairplay);',"fairplay_destroy(conn->fairplay);`n    if (conn->control_handle && im_control_available()) im_control_destroy(conn->control_handle);")
        $s = $s.Replace("static void `nconn_destroy", "static uint64_t conn_control(void *ptr) {`n    airplay_conn_t *conn = ptr;`n    return conn->control_active ? conn->control_handle : 0;`n}`n`nstatic void `nconn_destroy")
        $s = $s.Replace('httpd_cbs.conn_destroy = &conn_destroy;',"httpd_cbs.conn_destroy = &conn_destroy;`n    httpd_cbs.conn_control = &conn_control;")
    }
    if (!$s.Contains('int control_mixed;')) {
        $s = $s.Replace('int control_verified;', "int control_verified;`n    int control_mixed;")
    }
    $s = $s.Replace('const char *pd = http_request_get_header(request,"X-Apple-PD");','/* Mode is negotiated on pair-verify step 1 and retained per connection. */')
    $s = $s.Replace('int mixed = pd && !strcmp(pd,"1");','int mixed = conn->control_mixed;')
    $s
}
Edit-Source 'airplay_handlers.h' { param($s)
    if (!$s.Contains('conn->control_verified = 1;')) {
        $needle = '        http_response_add_header(response, "Content-Type", "application/octet-stream");'
        if (!$s.Contains($needle)) { throw 'verified pairing anchor missing' }
        $s = $s.Replace($needle, "        conn->control_verified = 1;`n" + $needle)
    }
    if (!$s.Contains('IPHONE_MIRROR_CONTROL_PD_NEGOTIATION')) {
        $needle = '/* We can fall through these errors, the result will just be garbage... */'
        if (!$s.Contains($needle)) { throw 'pair-verify PD negotiation anchor missing' }
        $s = $s.Replace($needle, @'
/* IPHONE_MIRROR_CONTROL_PD_NEGOTIATION */
        {
            const char *pd = http_request_get_header(request,"X-Apple-PD");
            conn->control_mixed = pd && !strcmp(pd,"1");
            conn->control_verified = 0;
        }
        /* We can fall through these errors, the result will just be garbage... */
'@)
    }; $s
}

Edit-Source 'httpd.h' { param($s)
    if (!$s.Contains('conn_is_reverse')) {
        $s=$s.Replace('uint64_t (*conn_control)(void *ptr);',"uint64_t (*conn_control)(void *ptr);`n    int (*conn_is_reverse)(void *ptr);`n    int (*conn_poll)(void *ptr,char *output,int capacity);")
    };$s
}
Edit-Source 'httpd.h' {param($s)
    if (!$s.Contains('conn_event_item')) {
        $s=$s.Replace('int (*conn_poll)(void *ptr,char *output,int capacity);',"int (*conn_poll)(void *ptr,char *output,int capacity);`n    uint64_t (*conn_event_item)(void *ptr);")
    };$s
}
Edit-Source 'http_request.h' {param($s)
    if (!$s.Contains('http_request_get_status')) {$s=$s.Replace('int http_request_is_complete(http_request_t *request);',"int http_request_is_complete(http_request_t *request);`nint http_request_get_status(http_request_t *request);")}
    if (!$s.Contains('http_request_is_response')) {$s=$s.Replace('int http_request_is_complete(http_request_t *request);',"int http_request_is_complete(http_request_t *request);`nint http_request_is_response(http_request_t *request);")}
    if (!$s.Contains('http_request_init_response')) {$s=$s.Replace('http_request_t *http_request_init(void);',"http_request_t *http_request_init(void);`nhttp_request_t *http_request_init_response(void);")};$s
}
Edit-Source 'http_request.c' {param($s)
    if (!$s.Contains('int http_request_get_status')) {$s=$s.Replace("http_request_t *http_request_init_response(void)","int http_request_get_status(http_request_t *request) {return request->parser.status_code;}`nhttp_request_t *http_request_init_response(void)")}
    if (!$s.Contains('int http_request_is_response')) {$s=$s.Replace("http_request_t *http_request_init_response(void)","int http_request_is_response(http_request_t *request) {return request->parser.type == HTTP_RESPONSE;}`nhttp_request_t *http_request_init_response(void)")}
    if (!$s.Contains('http_request_init_response')) {$s=$s.Replace("void`nhttp_request_destroy",@'
http_request_t *http_request_init_response(void) {
    http_request_t *request=http_request_init();
    if(request) {http_parser_init(&request->parser,HTTP_RESPONSE);request->parser.data=request;}
    return request;
}
void
http_request_destroy
'@)};
    if (!$s.Contains('int http_request_is_response')) {$s=$s.Replace("http_request_t *http_request_init_response(void)","int http_request_is_response(http_request_t *request) {return request->parser.type == HTTP_RESPONSE;}`nhttp_request_t *http_request_init_response(void)")}
    if (!$s.Contains('int http_request_get_status')) {$s=$s.Replace("http_request_t *http_request_init_response(void)","int http_request_get_status(http_request_t *request) {return request->parser.status_code;}`nhttp_request_t *http_request_init_response(void)")}
    $s.Replace('http_parser_init(&request->parser,HTTP_RESPONSE);','http_parser_init(&request->parser,HTTP_BOTH);')
}
Edit-Source 'http_parser.c' {param($s)
    if (!$s.Contains('IPHONE_MIRROR_REVERSE_RTSP_RESPONSE')) {
        $s=$s.Replace("if (ch == 'H') {`n          UPDATE_STATE(s_res_or_resp_H);", "/* IPHONE_MIRROR_REVERSE_RTSP_RESPONSE */`n        if (ch == 'H' || ch == 'R') {`n          parser->is_rtsp = ch == 'R';`n          UPDATE_STATE(s_res_or_resp_H);")
        $s=$s.Replace('          parser->method = HTTP_HEAD;', '          parser->method = parser->is_rtsp ? HTTP_RECORD : HTTP_HEAD;')
        $s=$s.Replace("case 'H':`n            UPDATE_STATE(s_res_H);", "case 'H':`n          case 'R':`n            parser->is_rtsp = ch == 'R';`n            UPDATE_STATE(s_res_H);")
        $s=$s.Replace("case s_res_HT:`n        STRICT_CHECK(ch != 'T');", "case s_res_HT:`n        STRICT_CHECK(ch != (parser->is_rtsp ? 'S' : 'T'));")
    };
    $s.Replace("case 'H': parser->method = parser->is_rtsp ? HTTP_RECORD : HTTP_HEAD;", "case 'H': parser->method = HTTP_HEAD;")
}
Edit-Source 'httpd.c' {param($s)
    if (!$s.Contains('IPHONE_MIRROR_FCUP_POLL')) {
        $s=$s.Replace('/* Get the correct nfds value and set rfds */',@'
/* IPHONE_MIRROR_FCUP_POLL */
        if (httpd->callbacks.conn_poll) {
            tv.tv_sec=0;tv.tv_usec=50000;
            for (i=0;i<httpd->max_connections;i++) {
                http_connection_t *connection=&httpd->connections[i];
                if (connection->connected && !im_control_tick(httpd,connection)) httpd_remove_connection(httpd,connection);
            }
        }
        /* Get the correct nfds value and set rfds */
'@)
        $s=$s.Replace('if (!connection->request) {','if (!connection->request && !httpd->callbacks.conn_control) {')
    };$s
}
Edit-Source 'http_parser.c' {param($s)
    if (!$s.Contains('IPHONE_MIRROR_EVENT_RESPONSE')) {
        $s=$s.Replace("  , s_res_H`n", "  , s_res_E, s_res_EV, s_res_EVE, s_res_EVEN /* IPHONE_MIRROR_EVENT_RESPONSE */`n  , s_res_H`n")
        $s=$s.Replace("if (ch == 'H' || ch == 'R') {", "if (ch == 'E') {`n          parser->type = HTTP_RESPONSE;`n          parser->is_rtsp = 0;`n          UPDATE_STATE(s_res_E);`n          CALLBACK_NOTIFY(message_begin);`n        } else if (ch == 'H' || ch == 'R') {")
        $s=$s.Replace("switch (ch) {`n          case 'H':`n          case 'R':", "switch (ch) {`n          case 'E':`n            parser->is_rtsp = 0;`n            UPDATE_STATE(s_res_E);`n            break;`n          case 'H':`n          case 'R':")
        $s=$s.Replace('      case s_res_H:', @'
      case s_res_E:
        if (ch != 'V') { SET_ERRNO(HPE_INVALID_CONSTANT); goto error; }
        UPDATE_STATE(s_res_EV);
        break;
      case s_res_EV:
        if (ch != 'E') { SET_ERRNO(HPE_INVALID_CONSTANT); goto error; }
        UPDATE_STATE(s_res_EVE);
        break;
      case s_res_EVE:
        if (ch != 'N') { SET_ERRNO(HPE_INVALID_CONSTANT); goto error; }
        UPDATE_STATE(s_res_EVEN);
        break;
      case s_res_EVEN:
        if (ch != 'T') { SET_ERRNO(HPE_INVALID_CONSTANT); goto error; }
        UPDATE_STATE(s_res_HTTP);
        break;
      case s_res_H:
'@)
    };$s
}
Edit-Source 'airplay.c' {param($s)
    if (!$s.Contains('media_active_session_id')) {
        $s=$s.Replace('airplay_callbacks_t callbacks;',"airplay_callbacks_t callbacks;`n    char media_active_session_id[129];")
    }
    if (!$s.Contains('IPHONE_MIRROR_FCUP_ACTION')) {
        $s=$s.Replace('int control_mixed;',"int control_mixed;`n    int media_bound;`n    int media_reverse;")
        $s=$s.Replace('httpd_cbs.conn_control = &conn_control;',"httpd_cbs.conn_control = &conn_control;`n    httpd_cbs.conn_is_reverse = &im_media_reverse;`n    httpd_cbs.conn_poll = &im_media_poll;")
        $s=$s.Replace('else if (!strcmp(method, "POST") && !strcmp(url, "/reverse")) {',@'
/* IPHONE_MIRROR_FCUP_ACTION */
    else if (!strcmp(method,"POST") && !strcmp(url,"/action")) {
        im_media_action(conn,request);
    }
    else if (!strcmp(method, "POST") && !strcmp(url, "/reverse")) {
        if (im_media_bind(conn,request)) conn->media_reverse=1;
'@)
    };$s
}
Edit-Source 'airplay.c' {param($s)
    if (!$s.Contains('httpd_cbs.conn_event_item')) {
        $s=$s.Replace('httpd_cbs.conn_poll = &im_media_poll;',"httpd_cbs.conn_poll = &im_media_poll;`n    httpd_cbs.conn_event_item = &im_media_event_item;")
    };$s
}

# Keep the existing C++ SDK callback vtable intact. The host opts into the
# explicit state callback through an additional, unmangled export.
function Edit-ProjectSource([string]$Name, [scriptblock]$Edit) {
    $path = Join-Path $SourceRoot $Name
    $text = [IO.File]::ReadAllText($path).Replace("`r`n","`n")
    $text = & $Edit $text
    [IO.File]::WriteAllText($path,$text,[Text.UTF8Encoding]::new($false))
}
Edit-ProjectSource 'AirPlayServerLib/include/airplay.h' {param($s)
    if (!$s.Contains('video_get_play_state')) {
        $needle='void (*video_get_play_info)(void* cls, double* duration, double* position, double* rate);'
        if (!$s.Contains($needle)) {throw 'AirPlay playback info callback anchor missing'}
        $s=$s.Replace($needle,$needle+"`n        int (*video_get_play_state)(void* cls, unsigned long long* command);")
    };$s
}
Edit-Source 'airplay.c' {param($s)
    if (!$s.Contains('media_event_state')) {
        $s=$s.Replace('int media_reverse;', "int media_reverse;`n    int media_event_state;`n    char media_session_id[129];")
    }
    if (!$s.Contains('media_event_command')) {
        $s=$s.Replace('int media_event_state;',"int media_event_state;`n    unsigned long long media_event_command;")
    };$s
}
Edit-Source 'airplay_handlers.h' {param($s)
    $s=$s.Replace('im_media_playback_info(conn,response_data,','im_media_playback_info(conn,request,response_data,')
    if (!$s.Contains('im_media_playback_info(conn')) {
        $needle="char* data = malloc(1024);`n`tsprintf(data, PLAYBACK_INFO, duration, duration, position, rate, duration);"
        if (!$s.Contains($needle)) {throw 'AirPlay playback info response anchor missing'}
        $s=$s.Replace($needle,@'
if (im_media_playback_info(conn,request,response_data,response_datalen,duration,position,rate)) return;
    char* data = malloc(1024);
    sprintf(data, PLAYBACK_INFO, duration, duration, position, rate, duration);
'@)
    };$s
}

Edit-ProjectSource 'airplay2dll/FgAirplayServer.h' {param($s)
    if (!$s.Contains('setPlaybackStateCallback')) {
        $s=$s.Replace('float setScale(float fRatio);',@'
float setScale(float fRatio);
    void setPlaybackStateCallback(void* cls, int (*callback)(void*,unsigned long long*));
    static int ap_video_get_play_state(void* cls,unsigned long long* command);
'@)
        $s=[regex]::Replace($s,'IAirServerCallback\*\s*m_pCallback;',@'
void* m_playbackStateCls = NULL;
    int (*m_playbackStateCallback)(void*,unsigned long long*) = NULL;
    IAirServerCallback* m_pCallback;
'@)
    };$s
}
Edit-ProjectSource 'airplay2dll/src/FgAirplayServer.cpp' {param($s)
    if (!$s.Contains('ap_video_get_play_state')) {
        $s=$s.Replace('m_stAirplayCB.video_get_play_info = ap_video_get_play_info;',"m_stAirplayCB.video_get_play_info = ap_video_get_play_info;`n    m_stAirplayCB.video_get_play_state = ap_video_get_play_state;")
        $s+=@'

// IPHONE_MIRROR_EXPLICIT_PLAYBACK_STATE: optional extension, no vtable change.
void FgAirplayServer::setPlaybackStateCallback(void* cls,int (*callback)(void*,unsigned long long*)) {
    WaitForSingleObject(m_mutexMap,INFINITE);
    m_playbackStateCls=cls;m_playbackStateCallback=callback;
    ReleaseMutex(m_mutexMap);
}
int FgAirplayServer::ap_video_get_play_state(void* cls,unsigned long long* command) {
    FgAirplayServer* server=(FgAirplayServer*)cls;
    WaitForSingleObject(server->m_mutexMap,INFINITE);
    int state=server->m_playbackStateCallback ? server->m_playbackStateCallback(server->m_playbackStateCls,command) : 0;
    ReleaseMutex(server->m_mutexMap);
    return state;
}
'@
    };$s
}
Edit-ProjectSource 'airplay2dll/src/Airplay2Export.cpp' {param($s)
    if (!$s.Contains('fgServerSetPlaybackStateCallback')) {
        $s+=@'

extern "C" __declspec(dllexport) void fgServerSetPlaybackStateCallback(void* handle,void* cls,int (*callback)(void*,unsigned long long*)) {
    if (handle) ((FgAirplayServer*)handle)->setPlaybackStateCallback(cls,callback);
}
'@
    };$s
}
Edit-Source 'airplay_handlers.h' {param($s)
    if (!$s.Contains('airplay-control-media.h')) {
        $s=$s.Replace('#include <stdlib.h>',"#include <stdlib.h>`n#include `"airplay-control-media.h`"")
        $s=$s.Replace('if (conn->airplay->callbacks.video_play != NULL) {',@'
/* IPHONE_MIRROR_FCUP_PLAY */
    if (!strncmp(url,"mlhls://",8)) {
        char *local=calloc(1,65537);
        int count=-1;
        if (local && im_media_bind(conn,request)) count=im_media_run(conn->control_handle,3,url,(int)strlen(url),local,65536);
        logger_log(conn->airplay->logger,LOGGER_INFO,"IPHONE_MIRROR_VIDEO_DIAG 25 %d 1 0 0 0 0",count);
        if(count<=0) {free(local);free(url);plist_free(root_node);return;}
        free(url);url=local;
    }
    if (conn->airplay->callbacks.video_play != NULL) {
'@)
    };$s
}

Edit-Source 'airplay.c' {param($s)
    if (!$s.Contains('media_event_state')) {
        $s=$s.Replace('int media_reverse;', "int media_reverse;`n    int media_event_state;`n    char media_session_id[129];")
    };$s
}

Edit-Source 'airplay_handlers.h' {param($s)
    if (!$s.Contains('im_media_claim(conn,request)')) {
        $needle="free(url);url=local;`n    }`n    if (conn->airplay->callbacks.video_play != NULL) {"
        if (!$s.Contains($needle)) {throw 'AirPlay media session claim anchor missing'}
        $s=$s.Replace($needle,"free(url);url=local;`n    }`n    im_media_claim(conn,request);`n    if (conn->airplay->callbacks.video_play != NULL) {")
    };$s
}

# Upgrade the URL-control branch even when an earlier ControlPatch has already
# been applied. Both clear and encrypted requests dispatch through this code.
foreach ($receiver in @('airplay','raop')) {
    $receiverName = $receiver
    Edit-Source ($receiver + '.c') {
        param($s)
        if (!$s.Contains('#include "airplay-scrub-request.h"')) {
            $s = '#include "airplay-scrub-request.h"' + "`n" + $s
        }
        if (!$s.Contains('IPHONE_MIRROR_STRICT_SCRUB_REQUEST')) {
            $pattern = '(?s)else if \(!strncmp\(url, "/scrub", 6\)\) \{\s*/\* IPHONE_MIRROR_(?:MEDIA_CAST|RAOP)_SEEK_CONTROL \*/.*?"iphonemirror://seek", 0, atof\(position \+ 9\)\);\s*\}'
            if ([regex]::Matches($s,$pattern).Count -ne 1) { throw "Strict scrub route anchor missing in $receiverName.c" }
            $ownerCheck = if ($receiverName -eq 'airplay') {
                'int scrub_owned = !conn->airplay->media_active_session_id[0] || im_media_owns(conn,request);'
            } else { 'int scrub_owned = 1;' }
            $source = if ($receiverName -eq 'airplay') { 1 } else { 2 }
            $branch = @'
else if (iphone_mirror_scrub_route(url)) {
        /* IPHONE_MIRROR_STRICT_SCRUB_REQUEST */
        double scrub_position = 0;
        int scrub_result = iphone_mirror_scrub_request(method,url,&scrub_position);
        int scrub_status = 200, scrub_applied = 0;
        IM_OWNER_CHECK
        if (!scrub_owned) {
            scrub_result = IM_SCRUB_NOT_OWNER; scrub_status = 403;
        } else if (scrub_result == IM_SCRUB_READ) {
            double duration = 0, position = 0, rate = 0;
            if (conn->IM_RECEIVER->callbacks.video_get_play_info)
                conn->IM_RECEIVER->callbacks.video_get_play_info(
                    conn->IM_RECEIVER->callbacks.cls,&duration,&position,&rate);
            if (!(duration >= 0 && duration <= DBL_MAX)) duration = 0;
            if (!(position >= 0 && position <= DBL_MAX)) position = 0;
            scrub_position = position;
            response_data = malloc(1024);
            if (response_data) {
                response_datalen = snprintf(response_data,1024,
                    "duration: %.6f\r\nposition: %.6f\r\n",duration,position);
                if (response_datalen < 0 || response_datalen >= 1024) {
                    free(response_data); response_data = NULL;
                    response_datalen = 0; scrub_status = 500;
                } else http_response_add_header(*response,"Content-Type","text/parameters");
            } else scrub_status = 500;
        } else if (scrub_result == IM_SCRUB_SEEK) {
            if (conn->IM_RECEIVER->callbacks.video_play) {
                conn->IM_RECEIVER->callbacks.video_play(conn->IM_RECEIVER->callbacks.cls,
                    "iphonemirror://seek",0,scrub_position);
                scrub_applied = 1;
            } else scrub_status = 503;
        } else scrub_status = scrub_result == IM_SCRUB_METHOD ? 405 : 400;
        if (scrub_status != 200) {
            http_response_destroy(*response);
            *response = http_response_init(protocol,scrub_status,
                scrub_status == 403 ? "Forbidden" : scrub_status == 405 ? "Method Not Allowed" :
                scrub_status == 400 ? "Bad Request" : "Internal Server Error");
            if (cseq) http_response_add_header(*response,"CSeq",cseq);
            if (scrub_status == 405) http_response_add_header(*response,"Allow","GET, POST");
        }
        logger_log(conn->IM_RECEIVER->logger,LOGGER_INFO,
            "IPHONE_MIRROR_VIDEO_DIAG 32 %d %d %d %d %d IM_SOURCE",
            iphone_mirror_scrub_method(method),scrub_result,
            iphone_mirror_scrub_milliseconds(scrub_position),scrub_applied,scrub_owned);
    }
'@
            $branch = $branch.Replace('IM_OWNER_CHECK',$ownerCheck).Replace('IM_RECEIVER',$receiverName).Replace('IM_SOURCE',"$source")
            $s = [regex]::Replace($s,$pattern,[Text.RegularExpressions.MatchEvaluator]{param($m) $branch})
        }
        if (!$s.Contains('IPHONE_MIRROR_SCRUB_ERROR_LENGTH')) {
            $needle = '            if (scrub_status == 405) http_response_add_header(*response,"Allow","GET, POST");'
            if (!$s.Contains($needle)) { throw "Scrub error-length anchor missing in $receiverName.c" }
            $s = $s.Replace($needle,
                '            http_response_add_header(*response,"Content-Length","0"); /* IPHONE_MIRROR_SCRUB_ERROR_LENGTH */' + "`n" + $needle)
        }
        $s
    }
}

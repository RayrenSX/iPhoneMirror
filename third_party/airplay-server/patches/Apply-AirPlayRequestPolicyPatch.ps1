[CmdletBinding()]
param([Parameter(Mandatory)][string]$SourceRoot)
$ErrorActionPreference = 'Stop'
# Keep discovery and both runtime capability endpoints consistent with the role.
$roleHandlers = Join-Path $SourceRoot 'AirPlayServerLib/lib/raop_handlers.h'
$roleText = [IO.File]::ReadAllText($roleHandlers)
$roleText = $roleText.Replace('0x4005A7FFEF7ULL', '0x5A7FFE73ULL').Replace('0x5A7FFE77ULL', '0x5A7FFE73ULL')
if (-not $roleText.Contains('IPHONE_MIRROR_MEDIA_NO_DISPLAY')) {
    $roleText = $roleText.Replace('uint32_t encoded_len = 0;', @'
/* IPHONE_MIRROR_MEDIA_NO_DISPLAY */
    if (capability_root && iphone_mirror_media_cast)
        plist_dict_remove_item(capability_root, "displays");
    uint32_t encoded_len = 0;
'@)
}
[IO.File]::WriteAllText($roleHandlers, $roleText, [Text.UTF8Encoding]::new($false))
$roleHandlers = Join-Path $SourceRoot 'AirPlayServerLib/lib/airplay_handlers.h'
$roleText = [IO.File]::ReadAllText($roleHandlers).Replace('0x5A7FFEF7U', '0x5A7FFE73U').Replace('0x5A7FFE77U', '0x5A7FFE73U')
# Both runtime endpoints must keep combined's screen receiver out of URL-video
# handoff; the independent media receiver advertises those capabilities.
$roleText = [regex]::Replace($roleText,
    '\(!strcmp\(iphone_mirror_mode, "media"\)\s*\|\|\s*!strcmp\(iphone_mirror_mode, "combined"\)\)\s*\?',
    '!strcmp(iphone_mirror_mode, "media") ?')
[IO.File]::WriteAllText($roleHandlers, $roleText, [Text.UTF8Encoding]::new($false))
foreach ($serverFile in @('airplay.c', 'raop.c')) {
    $serverPath = Join-Path $SourceRoot "AirPlayServerLib/lib/$serverFile"
    $serverText = [IO.File]::ReadAllText($serverPath)
    if (-not $serverText.Contains('IPHONE_MIRROR_UNSUPPORTED_FP_SETUP2') -and
        -not $serverText.Contains('IPHONE_MIRROR_CONTROL_UPGRADE')) {
        $needle = '*response = http_response_init(protocol, 200, "OK");'
        if (-not $serverText.Contains($needle)) { throw 'Unsupported FairPlay response insertion point changed.' }
        $replacement = @'
/* IPHONE_MIRROR_UNSUPPORTED_FP_SETUP2: do not claim successful
       negotiation for a FairPlay variant this receiver cannot implement. */
    if (!strcmp(method, "POST") && !strcmp(url, "/fp-setup2")) {
        *response = http_response_init(protocol, 421, "Misdirected Request");
        if (cseq) http_response_add_header(*response, "CSeq", cseq);
        http_response_add_header(*response, "Content-Length", "0");
        http_response_finish(*response, NULL, 0);
        return;
    }
    *response = http_response_init(protocol, 200, "OK");
'@
        $serverText = $serverText.Replace($needle, $replacement)
        [IO.File]::WriteAllText($serverPath, $serverText, [Text.UTF8Encoding]::new($false))
    }
}
Copy-Item (Join-Path $PSScriptRoot 'airplay-play-request.h') (Join-Path $SourceRoot 'AirPlayServerLib/lib/airplay-play-request.h') -Force
foreach ($handlerFile in @('airplay_handlers.h', 'raop_handlers.h')) {
    $handlerPath = Join-Path $SourceRoot "AirPlayServerLib/lib/$handlerFile"
    $handlerText = [IO.File]::ReadAllText($handlerPath)
    if (-not $handlerText.Contains('#include "airplay-play-request.h"')) {
        $handlerText = '#include "airplay-play-request.h"' + "`n" + $handlerText
    }
    if ($handlerFile -eq 'airplay_handlers.h') {
        $handlerText = $handlerText.Replace('plist_from_memory(data, (uint32_t)datalen, &root_node);', 'root_node = iphone_mirror_play_request(data, datalen);')
    } else {
        $handlerText = $handlerText.Replace('plist_from_bin(data, datalen, &root);', 'root = iphone_mirror_play_request(data, datalen);')
    }
    [IO.File]::WriteAllText($handlerPath, $handlerText, [Text.UTF8Encoding]::new($false))
}
# Combined means screen mirroring plus a separate DLNA service. Its AirPlay
# endpoint must not invite URL-video handoff until that protocol is complete.
$handlersPath = Join-Path $SourceRoot 'AirPlayServerLib/lib/raop_handlers.h'
$handlersText = [IO.File]::ReadAllText($handlersPath)
$featurePattern = '(?s)(/\* IPHONE_MIRROR_RUNTIME_FEATURES \*/.*?int iphone_mirror_media_cast = )iphone_mirror_mode != NULL &&\s*\(!strcmp\(iphone_mirror_mode, "media"\) \|\|\s*!strcmp\(iphone_mirror_mode, "combined"\)\);'
if ([regex]::IsMatch($handlersText, $featurePattern)) {
    $handlersText = [regex]::Replace($handlersText, $featurePattern, '${1}iphone_mirror_mode != NULL && !strcmp(iphone_mirror_mode, "media"); /* IPHONE_MIRROR_NO_COMBINED_HANDOFF */')
    [IO.File]::WriteAllText($handlersPath, $handlersText, [Text.UTF8Encoding]::new($false))
} elseif (-not $handlersText.Contains('IPHONE_MIRROR_NO_COMBINED_HANDOFF')) {
    throw 'AirPlay runtime feature policy source changed.'
}
# Log only fixed route/method identifiers and byte counts, never request bodies,
# URLs, keys, device identifiers or headers supplied by the sender.
$httpdPath = Join-Path $SourceRoot 'AirPlayServerLib/lib/httpd.c'
$httpdText = [IO.File]::ReadAllText($httpdPath)
if (-not $httpdText.Contains('IPHONE_MIRROR_REQUEST_DIAGNOSTICS')) {
    $needle = 'httpd->callbacks.conn_request(connection->user_data, connection->request, &response);'
    if (-not $httpdText.Contains($needle)) { throw 'HTTP dispatch source changed.' }
    $replacement = @'
/* IPHONE_MIRROR_REQUEST_DIAGNOSTICS */
                {
                    const char *method = http_request_get_method(connection->request);
                    const char *url = http_request_get_url(connection->request);
                    const char *protocol = http_request_get_protocol(connection->request);
                    int body_length = 0;
                    http_request_get_data(connection->request, &body_length);
                    int method_id = !method ? 0 : !strcmp(method,"GET") ? 1 : !strcmp(method,"POST") ? 2 : !strcmp(method,"SETUP") ? 3 : !strcmp(method,"TEARDOWN") ? 4 : !strcmp(method,"RECORD") ? 5 : !strcmp(method,"SET_PARAMETER") ? 6 : 9;
                    int route_id = !url ? 0 : !strcmp(url,"/info") ? 1 : !strcmp(url,"/pair-setup") ? 2 : !strcmp(url,"/pair-verify") ? 3 : !strcmp(url,"/fp-setup") ? 4 : !strcmp(url,"/play") ? 5 : !strcmp(url,"/feedback") ? 6 : !strcmp(url,"/audioMode") ? 7 : !strcmp(url,"/server-info") ? 8 : !strcmp(url,"/reverse") ? 9 : 10;
                    logger_log(httpd->logger, LOGGER_INFO, "IPHONE_MIRROR_VIDEO_DIAG 20 %d %d %d %d %d 0", method_id, route_id, protocol && !strncmp(protocol,"HTTP/",5), body_length, http_request_get_header(connection->request,"CSeq") != NULL);
                }
                httpd->callbacks.conn_request(connection->user_data, connection->request, &response);
'@
    $httpdText = $httpdText.Replace($needle, $replacement)
    $needle = 'data = http_response_get_data(response, &datalen);'
    if (-not $httpdText.Contains($needle)) { throw 'HTTP response send source changed.' }
    $replacement = @'
data = http_response_get_data(response, &datalen);
                    {
                        int status = datalen >= 12 ? (data[9]-'0')*100 + (data[10]-'0')*10 + data[11]-'0' : 0;
                        logger_log(httpd->logger, LOGGER_INFO, "IPHONE_MIRROR_VIDEO_DIAG 21 %d %d %d 0 0 0", status, datalen, http_response_get_disconnect(response));
                    }
'@
    $httpdText = $httpdText.Replace($needle, $replacement)
    [IO.File]::WriteAllText($httpdPath, $httpdText, [Text.UTF8Encoding]::new($false))
}
$httpdText = [IO.File]::ReadAllText($httpdPath)
$httpdText = $httpdText.Replace('!strcmp(url,"/reverse") ? 9 : 10;', '!strcmp(url,"/reverse") ? 9 : !strcmp(url,"/fp-setup2") ? 11 : !strcmp(url,"/auth-setup") ? 12 : !strncmp(url,"/setProperty",12) ? 13 : !strcmp(url,"/playback-info") ? 14 : !strncmp(url,"/rate",5) ? 15 : 10;')
[IO.File]::WriteAllText($httpdPath, $httpdText, [Text.UTF8Encoding]::new($false))
$path = Join-Path $SourceRoot 'AirPlayServerLib/lib/raop.c'
$text = [IO.File]::ReadAllText($path)
if (-not $text.Contains('IPHONE_MIRROR_REJECT_SCREEN_ON_VIDEO')) {
    $needle = 'if (!iphone_mirror_media_cast && (!strcmp(method, "ANNOUNCE") ||'
    if (-not $text.Contains($needle)) { throw 'RAOP role check insertion point changed.' }
    $insert = @'
/* IPHONE_MIRROR_REJECT_SCREEN_ON_VIDEO */
    if (iphone_mirror_mode && !strcmp(iphone_mirror_mode, "media") && !strcmp(method, "SETUP")) {
        int role_length = 0, reject_screen = 0;
        const char *role_data = http_request_get_data(request, &role_length);
        plist_t role_root = NULL;
        if (role_data && role_length > 0) plist_from_memory(role_data, role_length, &role_root);
        if (PLIST_IS_DICT(role_root)) {
            uint8_t screen = 0;
            plist_t flag = plist_dict_get_item(role_root, "isScreenMirroringSession");
            if (PLIST_IS_BOOLEAN(flag)) plist_get_bool_val(flag, &screen);
            reject_screen = screen != 0;
            plist_t streams = plist_dict_get_item(role_root, "streams");
            if (PLIST_IS_ARRAY(streams)) {
                for (uint32_t i = 0; i < plist_array_get_size(streams); ++i) {
                    plist_t stream = plist_array_get_item(streams, i);
                    plist_t type = PLIST_IS_DICT(stream) ? plist_dict_get_item(stream, "type") : NULL;
                    uint64_t value = 0;
                    if (PLIST_IS_UINT(type)) plist_get_uint_val(type, &value);
                    if (value == 110) reject_screen = 1;
                }
            }
        }
        if (role_root) plist_free(role_root);
        if (reject_screen) {
            *response = http_response_init(protocol, 403, "Screen Mirroring Unavailable");
            if (cseq) http_response_add_header(*response, "CSeq", cseq);
            http_response_finish(*response, NULL, 0);
            return;
        }
    }
'@
    $text = $text.Replace($needle, $insert + "`n`t" + $needle)
    [IO.File]::WriteAllText($path, $text, [Text.UTF8Encoding]::new($false))
}
$marker = 'iphone_mirror_request_headers_valid'
if (-not $text.Contains($marker)) {
    $needle = 'if (!method || !url || !protocol || (!cseq && !iphone_mirror_media_control)) {'
    if (-not $text.Contains($needle)) { throw 'AirPlay request validation source changed.' }
    $text = '#include "airplay-request-policy.h"' + "`n" + $text.Replace($needle,
        'if (!iphone_mirror_request_headers_valid(method, url, protocol, cseq, iphone_mirror_media_control)) {')
    [IO.File]::WriteAllText($path, $text, [Text.UTF8Encoding]::new($false))
}
Copy-Item (Join-Path $PSScriptRoot 'airplay-request-policy.h') `
    (Join-Path $SourceRoot 'AirPlayServerLib/lib/airplay-request-policy.h') -Force

$pairing = Join-Path $SourceRoot 'AirPlayServerLib/lib/pairing.c'
$text = [IO.File]::ReadAllText($pairing)
$needle = 'if (session->status != STATUS_SETUP && session->status != STATUS_HANDSHAKE) {'
$replacement = 'if (session->status != STATUS_INITIAL && session->status != STATUS_SETUP && session->status != STATUS_HANDSHAKE) {'
if ($text.Contains($needle)) {
    $text = $text.Replace($needle, $replacement)
    [IO.File]::WriteAllText($pairing, $text, [Text.UTF8Encoding]::new($false))
} elseif (-not $text.Contains($replacement)) {
    throw 'AirPlay cached pairing status source changed.'
}

$responsePath = Join-Path $SourceRoot 'AirPlayServerLib/lib/http_response.c'
$text = [IO.File]::ReadAllText($responsePath)
if (-not $text.Contains('IPHONE_MIRROR_HTTP_EMPTY_RESPONSE')) {
    $needle = '/* Add extra end of line after headers */'
    if (-not $text.Contains($needle)) { throw 'AirPlay HTTP response framing source changed.' }
    $replacement = @'
/* IPHONE_MIRROR_HTTP_EMPTY_RESPONSE: persistent HTTP 200 responses
           require explicit zero length, including pair-verify completion.
           Leave RTSP and bodyless status codes (101/204/304) unchanged. */
        if (response->data_length >= 13 &&
            (!memcmp(response->data, "HTTP/1.1 200 ", 13) ||
             !memcmp(response->data, "HTTP/1.0 200 ", 13))) {
            http_response_add_header(response, "Content-Length", "0");
        }
        /* Add extra end of line after headers */
'@
    $text = $text.Replace($needle, $replacement)
    [IO.File]::WriteAllText($responsePath, $text, [Text.UTF8Encoding]::new($false))
}

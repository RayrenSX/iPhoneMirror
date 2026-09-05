[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$SourceRoot
)

$ErrorActionPreference = 'Stop'
$SourceRoot = (Resolve-Path -LiteralPath $SourceRoot).Path
$Encoding = [Text.UTF8Encoding]::new($false)
$LineFeed = [string][char]10
$Tab = [string][char]9
$Files = @{
    Parser = Join-Path $SourceRoot 'AirPlayServerLib\lib\http_parser.c'
    ParserHeader = Join-Path $SourceRoot 'AirPlayServerLib\lib\http_parser.h'
    Request = Join-Path $SourceRoot 'AirPlayServerLib\lib\http_request.c'
    RequestHeader = Join-Path $SourceRoot 'AirPlayServerLib\lib\http_request.h'
    AirPlay = Join-Path $SourceRoot 'AirPlayServerLib\lib\airplay.c'
    Raop = Join-Path $SourceRoot 'AirPlayServerLib\lib\raop.c'
    Pairing = Join-Path $SourceRoot 'AirPlayServerLib\lib\pairing.c'
    Httpd = Join-Path $SourceRoot 'AirPlayServerLib\lib\httpd.c'
}
foreach ($File in $Files.Values) {
    if (-not (Test-Path -LiteralPath $File -PathType Leaf)) {
        throw "AirPlay compatibility source is missing: $File"
    }
}

function Read-Source([string]$Path) {
    ([IO.File]::ReadAllText($Path, $Encoding)) -replace '\r\n', $LineFeed
}

function Write-Source([string]$Path, [string]$Content) {
    [IO.File]::WriteAllText($Path, $Content, $Encoding)
}

function Replace-Once(
    [string]$Content,
    [string]$Needle,
    [string]$Replacement,
    [string]$Description = 'AirPlay compatibility replacement'
) {
    $Count = [regex]::Matches($Content, [regex]::Escape($Needle)).Count
    if ($Count -ne 1) {
        throw "$Description changed; expected one insertion point, found $Count."
    }
    $Content.Replace($Needle, $Replacement)
}

# c788d6fe: preserve HTTP versus RTSP, compare header names case-insensitively,
# and allow the second request in pair-verify to finish the handshake.
$ParserHeader = Read-Source $Files.ParserHeader
$ParserMarker = 'IPHONE_MIRROR_AIRPLAY_PROTOCOL_COMPATIBILITY'
if (-not $ParserHeader.Contains($ParserMarker)) {
    $Needle = '  unsigned int upgrade : 1;'
    $Replacement = '  unsigned int upgrade : 1;' + $LineFeed + $LineFeed +
        '  /* IPHONE_MIRROR_AIRPLAY_PROTOCOL_COMPATIBILITY */' + $LineFeed +
        '  unsigned int is_rtsp : 1;'
    $ParserHeader = Replace-Once $ParserHeader $Needle $Replacement
    Write-Source $Files.ParserHeader $ParserHeader
}

$Parser = Read-Source $Files.Parser
if (-not $Parser.Contains($ParserMarker)) {
    $Needle = "          case 'H':$LineFeed          case 'R':$LineFeed            UPDATE_STATE(s_req_http_H);$LineFeed            break;"
    $Replacement = "          case 'H':$LineFeed" +
        '            /* IPHONE_MIRROR_AIRPLAY_PROTOCOL_COMPATIBILITY */' + $LineFeed +
        '            parser->is_rtsp = 0;' + $LineFeed +
        '            UPDATE_STATE(s_req_http_H);' + $LineFeed +
        '            break;' + $LineFeed +
        "          case 'R':$LineFeed" +
        '            parser->is_rtsp = 1;' + $LineFeed +
        '            UPDATE_STATE(s_req_http_H);' + $LineFeed +
        '            break;'
    $Parser = Replace-Once $Parser $Needle $Replacement
    Write-Source $Files.Parser $Parser
}

$RequestHeader = Read-Source $Files.RequestHeader
if (-not $RequestHeader.Contains($ParserMarker)) {
    $Needle = 'const char *http_request_get_url(http_request_t *request);'
    $Replacement = $Needle + $LineFeed +
        '/* IPHONE_MIRROR_AIRPLAY_PROTOCOL_COMPATIBILITY */' + $LineFeed +
        'const char *http_request_get_protocol(http_request_t *request);'
    $RequestHeader = Replace-Once $RequestHeader $Needle $Replacement
    Write-Source $Files.RequestHeader $RequestHeader
}

$Request = Read-Source $Files.Request
$RequestMarker = 'IPHONE_MIRROR_AIRPLAY_HEADER_CASE'
if (-not $Request.Contains($RequestMarker)) {
    $Request = $Request.Replace('#include <stdlib.h>' + $LineFeed,
        '#include <stdlib.h>' + $LineFeed + '#include <stdio.h>' + $LineFeed +
        '#include <ctype.h>' + $LineFeed)
    $Request = Replace-Once $Request ($Tab + 'char *url;') (
        $Tab + 'char *url;' + $LineFeed + $Tab + 'char protocol[32];')
    $Needle = $Tab + 'request->method = http_method_str(request->parser.method);' +
        $LineFeed + $Tab + 'request->complete = 1;'
    $Replacement = $Tab + 'request->method = http_method_str(request->parser.method);' +
        $LineFeed + $Tab + '/* IPHONE_MIRROR_AIRPLAY_PROTOCOL_COMPATIBILITY */' +
        $LineFeed + $Tab + 'snprintf(request->protocol, sizeof(request->protocol), "%s/%u.%u",' +
        $LineFeed + $Tab + $Tab + 'request->parser.is_rtsp ? "RTSP" : "HTTP",' +
        $LineFeed + $Tab + $Tab + '(unsigned int)request->parser.http_major,' +
        $LineFeed + $Tab + $Tab + '(unsigned int)request->parser.http_minor);' +
        $LineFeed + $Tab + 'request->complete = 1;'
    $Request = Replace-Once $Request $Needle $Replacement
    $Needle = 'http_request_t *' + $LineFeed + 'http_request_init(void)'
    $Helper = @'
/* IPHONE_MIRROR_AIRPLAY_HEADER_CASE */
static int
iphone_mirror_header_name_equals(const char *left, const char *right)
{
    while (*left != '\0' && *right != '\0') {
        if (tolower((unsigned char)*left) != tolower((unsigned char)*right)) {
            return 0;
        }
        ++left;
        ++right;
    }
    return *left == *right;
}

'@ -replace '\r?\n', $LineFeed
    $Request = Replace-Once $Request $Needle ($Helper + $Needle)
    $Request = $Request.Replace(
        'if (!strcmp(request->headers[i], name)) {',
        'if (iphone_mirror_header_name_equals(request->headers[i], name)) {')
    $Needle = 'const char *' + $LineFeed +
        'http_request_get_header(http_request_t *request, const char *name)'
    $Getter = @'
const char *
http_request_get_protocol(http_request_t *request)
{
    assert(request);
    return request->protocol[0] != '\0' ? request->protocol : NULL;
}

'@ -replace '\r?\n', $LineFeed
    $Request = Replace-Once $Request $Needle ($Getter + $Needle)
    Write-Source $Files.Request $Request
}

$AirPlay = Read-Source $Files.AirPlay
$AirPlayMarker = 'IPHONE_MIRROR_AIRPLAY_PROTOCOL_COMPATIBILITY'
if (-not $AirPlay.Contains($AirPlayMarker)) {
    $AirPlay = Replace-Once $AirPlay ($Tab + 'const char *method;') (
        $Tab + 'const char *method;' + $LineFeed +
        $Tab + '/* IPHONE_MIRROR_AIRPLAY_PROTOCOL_COMPATIBILITY */' + $LineFeed +
        $Tab + 'const char *protocol;') 'AirPlay request protocol declaration'
    $AirPlay = Replace-Once $AirPlay ($Tab + 'method = http_request_get_method(request);') (
        $Tab + 'method = http_request_get_method(request);' + $LineFeed +
        $Tab + 'protocol = http_request_get_protocol(request);') 'AirPlay request protocol capture'
    $AirPlay = Replace-Once $AirPlay ($Tab + 'if (!method)') (
        $Tab + 'if (!method || !url || !protocol)') 'AirPlay request validation'
    $AirPlay = Replace-Once $AirPlay '*response = http_response_init("HTTP/1.1", 200, "OK");' (
        '*response = http_response_init(protocol, 200, "OK");') 'AirPlay success response protocol'
    $AirPlay = Replace-Once $AirPlay '*response = http_response_init("HTTP/1.1", 403, "Forbidden");' (
        '*response = http_response_init(protocol, 403, "Forbidden");') 'AirPlay rejection response protocol'
    $AirPlay = Replace-Once $AirPlay '*response = http_response_init("HTTP/1.1", 101, "Switching Protocols");' (
        '*response = http_response_init(protocol, 101, "Switching Protocols");') 'AirPlay reverse response protocol'
    $AirPlay = $AirPlay.Replace(
        'http_request_get_header(request, "authorization")',
        'http_request_get_header(request, "Authorization")')
    Write-Source $Files.AirPlay $AirPlay
}

$Raop = Read-Source $Files.Raop
if (-not $Raop.Contains($AirPlayMarker)) {
    $Raop = Replace-Once $Raop ($Tab + 'const char *cseq;') (
        $Tab + 'const char *cseq;' + $LineFeed +
        $Tab + '/* IPHONE_MIRROR_AIRPLAY_PROTOCOL_COMPATIBILITY */' + $LineFeed +
        $Tab + 'const char *protocol;') 'RAOP request protocol declaration'
    $Raop = Replace-Once $Raop ($Tab + 'cseq = http_request_get_header(request, "CSeq");') (
        $Tab + 'cseq = http_request_get_header(request, "CSeq");' + $LineFeed +
        $Tab + 'protocol = http_request_get_protocol(request);') 'RAOP request protocol capture'
    $Raop = Replace-Once $Raop ($Tab + 'if (!method || (!cseq && !iphone_mirror_media_control)) {') (
        $Tab + 'if (!method || !url || !protocol || (!cseq && !iphone_mirror_media_control)) {') 'RAOP request validation'
    $Raop = Replace-Once $Raop '*response = http_response_init("RTSP/1.0", 403, "Forbidden");' (
        '*response = http_response_init(protocol, 403, "Forbidden");') 'RAOP rejection response protocol'
    $Raop = Replace-Once $Raop '*response = http_response_init("RTSP/1.0", 200, "OK");' (
        '*response = http_response_init(protocol, 200, "OK");') 'RAOP success response protocol'
    $Needle = $Tab + $Tab + 'http_response_add_header(*response, "CSeq", cseq);' + $LineFeed +
        $Tab + $Tab + 'http_response_add_header(*response, "Server", "AirTunes/220.68");'
    $Replacement = $Tab + $Tab + 'if (cseq != NULL) http_response_add_header(*response, "CSeq", cseq);' +
        $LineFeed + $Tab + $Tab + 'http_response_add_header(*response, "Server", "AirTunes/220.68");'
    $Raop = Replace-Once $Raop $Needle $Replacement 'RAOP rejection CSeq handling'
    Write-Source $Files.Raop $Raop
}

$Pairing = Read-Source $Files.Pairing
$PairingMarker = 'IPHONE_MIRROR_AIRPLAY_PAIR_VERIFY_TWO_STAGE'
if (-not $Pairing.Contains($PairingMarker)) {
    $Needle = 'int' + $LineFeed +
        'pairing_session_check_handshake_status(pairing_session_t *session)' + $LineFeed +
        '{' + $LineFeed +
        '    assert(session);' + $LineFeed +
        '    if (session->status != STATUS_SETUP) {' + $LineFeed +
        '        return -1;' + $LineFeed +
        '    }' + $LineFeed +
        '    return 0;' + $LineFeed + '}'
    $Replacement = 'int' + $LineFeed +
        'pairing_session_check_handshake_status(pairing_session_t *session)' + $LineFeed +
        '{' + $LineFeed +
        '    assert(session);' + $LineFeed +
        '    /* IPHONE_MIRROR_AIRPLAY_PAIR_VERIFY_TWO_STAGE */' + $LineFeed +
        '    if (session->status != STATUS_SETUP && session->status != STATUS_HANDSHAKE) {' +
        $LineFeed + '        return -1;' + $LineFeed + '    }' + $LineFeed +
        '    return 0;' + $LineFeed + '}'
    $Pairing = Replace-Once $Pairing $Needle $Replacement
    Write-Source $Files.Pairing $Pairing
}

$Httpd = Read-Source $Files.Httpd
$HttpdMarker = 'IPHONE_MIRROR_AIRPLAY_RECV_ERROR'
if (-not $Httpd.Contains($HttpdMarker)) {
    $Indent = $Tab + $Tab + $Tab
    $Needle = $Indent + 'if (ret == 0) {' + $LineFeed +
        $Indent + $Tab + 'logger_log(httpd->logger, LOGGER_INFO, "Connection closed for socket %d", connection->socket_fd);' +
        $LineFeed + $Indent + $Tab + 'httpd_remove_connection(httpd, connection);' + $LineFeed +
        $Indent + $Tab + 'continue;' + $LineFeed + $Indent + '}'
    $Replacement = $Needle + $LineFeed +
        $Indent + 'if (ret < 0) {' + $LineFeed +
        $Indent + $Tab + '/* IPHONE_MIRROR_AIRPLAY_RECV_ERROR */' + $LineFeed +
        $Indent + $Tab + 'logger_log(httpd->logger, LOGGER_INFO, "Error receiving data on socket %d", connection->socket_fd);' +
        $LineFeed + $Indent + $Tab + 'httpd_remove_connection(httpd, connection);' + $LineFeed +
        $Indent + $Tab + 'continue;' + $LineFeed + $Indent + '}'
    $Httpd = Replace-Once $Httpd $Needle $Replacement
    Write-Source $Files.Httpd $Httpd
}

Write-Host 'Applied upstream AirPlay connection compatibility patch c788d6fe.' -ForegroundColor Green

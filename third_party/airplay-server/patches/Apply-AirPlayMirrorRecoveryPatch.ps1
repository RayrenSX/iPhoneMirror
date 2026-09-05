[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$SourceRoot
)

$ErrorActionPreference = 'Stop'
$SourceRoot = (Resolve-Path -LiteralPath $SourceRoot).Path
$Encoding = [Text.UTF8Encoding]::new($false)
$LineFeed = [string][char]10
$File = Join-Path $SourceRoot 'AirPlayServerLib\lib\raop_rtp_mirror.c'
if (-not (Test-Path -LiteralPath $File -PathType Leaf)) {
    throw "AirPlay mirror source is missing: $File"
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
    [string]$Description
) {
    $Count = [regex]::Matches($Content, [regex]::Escape($Needle)).Count
    if ($Count -ne 1) {
        throw "$Description changed; expected one insertion point, found $Count."
    }
    $Content.Replace($Needle, $Replacement)
}

$Marker = 'IPHONE_MIRROR_AIRPLAY_MIRROR_RECOVERY'
$Text = Read-Source $File
if (-not $Text.Contains($Marker)) {
    $Text = Replace-Once $Text '#include <WinSock2.h>' (
        '#include <WinSock2.h>' + $LineFeed + '#include <mstcpip.h>') 'mirror keepalive include'
    $Needle = 'static int' + $LineFeed +
        'raop_rtp_parse_remote(raop_rtp_mirror_t *raop_rtp_mirror, const unsigned char *remote, int remotelen)'
    $Helper = @'
#define MIRROR_READ_TIMEOUT_MS 3000
#define MIRROR_MAX_PAYLOAD_SIZE (64 * 1024 * 1024)

/* IPHONE_MIRROR_AIRPLAY_MIRROR_RECOVERY: do not block forever on a
 * half-delivered TCP frame after a sender network-path change. */
static int
mirror_recv_exact(raop_rtp_mirror_t *mirror, int socket_fd,
    unsigned char *buffer, int length)
{
    int offset = 0;

    if (mirror == NULL || socket_fd == -1 || buffer == NULL || length < 0) {
        return -1;
    }
    while (offset < length) {
        fd_set rfds;
        struct timeval timeout;
        int ready;
        int received;
        int running;

        MUTEX_LOCK(mirror->run_mutex);
        running = mirror->running;
        MUTEX_UNLOCK(mirror->run_mutex);
        if (!running) {
            return -1;
        }

        FD_ZERO(&rfds);
        FD_SET(socket_fd, &rfds);
        timeout.tv_sec = MIRROR_READ_TIMEOUT_MS / 1000;
        timeout.tv_usec = (MIRROR_READ_TIMEOUT_MS % 1000) * 1000;
        ready = select(socket_fd + 1, &rfds, NULL, NULL, &timeout);
        if (ready == 0) {
            logger_log(mirror->logger, LOGGER_WARNING,
                "Mirror TCP read timed out after %d ms", MIRROR_READ_TIMEOUT_MS);
            return -1;
        }
        if (ready < 0) {
            logger_log(mirror->logger, LOGGER_WARNING,
                "Mirror TCP select failed");
            return -1;
        }

        received = recv(socket_fd, (char *)buffer + offset, length - offset, 0);
        if (received <= 0) {
            logger_log(mirror->logger, LOGGER_INFO,
                received == 0 ? "Mirror TCP socket closed" : "Mirror TCP receive failed");
            return -1;
        }
        offset += received;
    }
    return 0;
}

static void
mirror_enable_keepalive(int socket_fd)
{
    int enabled = 1;
    setsockopt(socket_fd, SOL_SOCKET, SO_KEEPALIVE,
        (const char *)&enabled, sizeof(enabled));
#ifdef WIN32
    {
        struct tcp_keepalive settings;
        DWORD bytes_returned = 0;
        settings.onoff = 1;
        settings.keepalivetime = 5000;
        settings.keepaliveinterval = 1000;
        WSAIoctl((SOCKET)socket_fd, SIO_KEEPALIVE_VALS,
            &settings, sizeof(settings), NULL, 0, &bytes_returned, NULL, NULL);
    }
#endif
}

'@ -replace '\r?\n', $LineFeed
    $Text = Replace-Once $Text $Needle ($Helper + $Needle)
        'mirror recovery helper insertion'

    $Needle = @'
            stream_fd = accept(raop_rtp_mirror->mirror_data_sock, (struct sockaddr *)&saddr, &saddrlen);
            if (stream_fd == -1) {
                /* FIXME: Error happened */
                logger_log(raop_rtp_mirror->logger, LOGGER_INFO, "Error in accept %d %s", errno, strerror(errno));
                exceptionExit = 1;
                break;
            }
'@ -replace '\r?\n', $LineFeed
    $Replacement = $Needle + ($LineFeed + @'
            mirror_enable_keepalive(stream_fd);
'@ -replace '\r?\n', $LineFeed)
    $Text = Replace-Once $Text $Needle $Replacement
        'mirror stream keepalive'

    $Needle = @'
                do {
                    // read remaining 124 bytes
                    ret = recv(stream_fd, packet + readstart, 128 - readstart, 0);
                    readstart = readstart + ret;
                } while (readstart < 128);
                int payloadsize = byteutils_get_int(packet, 0);
'@ -replace '\r?\n', $LineFeed
    $Replacement = @'
                if (mirror_recv_exact(raop_rtp_mirror, stream_fd,
                    packet + readstart, 128 - readstart) != 0) {
                    exceptionExit = 1;
                    break;
                }
                readstart = 128;
                int payloadsize = byteutils_get_int(packet, 0);
                if (payloadsize < 0 || payloadsize > MIRROR_MAX_PAYLOAD_SIZE) {
                    logger_log(raop_rtp_mirror->logger, LOGGER_WARNING,
                        "Invalid mirror payload size: %d", payloadsize);
                    exceptionExit = 1;
                    break;
                }
'@ -replace '\r?\n', $LineFeed
    $Text = Replace-Once $Text $Needle $Replacement 'mirror header exact read'

    $Needle = @'
                    do {
                        // payload data
                        ret = recv(stream_fd, payload_in + readstart, payloadsize - readstart, 0);
                        readstart = readstart + ret;
                    } while (readstart < payloadsize);
'@ -replace '\r?\n', $LineFeed
    $Replacement = @'
                    if (mirror_recv_exact(raop_rtp_mirror, stream_fd,
                        payload_in, payloadsize) != 0) {
                        free(payload_in);
                        free(payload);
                        exceptionExit = 1;
                        break;
                    }
                    readstart = payloadsize;
'@ -replace '\r?\n', $LineFeed
    $Text = Replace-Once $Text $Needle $Replacement 'encrypted mirror payload exact read'

    # Validate every decrypted length-prefixed NAL before replacing its length
    # with an Annex-B start code. A corrupt FairPlay block used to leave
    # nalu_size unchanged (infinite loop) or advance past the payload, which
    # presented as a permanent black screen instead of a recoverable stream
    # reset.
    $Needle = @'
                    int nalu_size = 0;
                    int nalu_num = 0;
                    while (nalu_size < payloadsize) {
                        int nc_len = (payload[nalu_size + 0] << 24) | (payload[nalu_size + 1] << 16) | (payload[nalu_size + 2] << 8) | (payload[nalu_size + 3]);
                        if (nc_len > 0) {
                            payload[nalu_size + 0] = 0;
                            payload[nalu_size + 1] = 0;
                            payload[nalu_size + 2] = 0;
                            payload[nalu_size + 3] = 1;
                            //int nalutype = payload[4] & 0x1f;
                            //logger_log(raop_rtp_mirror->logger, LOGGER_DEBUG, "nalutype = %d", nalutype);
                            nalu_size += nc_len + 4;
                            nalu_num++;
                        }
                    }
'@ -replace '\r?\n', $LineFeed
    $Replacement = @'
                    /* IPHONE_MIRROR_MIRROR_NAL_BOUNDS */
                    int nalu_size = 0;
                    int nalu_num = 0;
                    int valid_nalus = payloadsize == 0;
                    while (!valid_nalus && nalu_size + 4 <= payloadsize) {
                        uint32_t nc_len = ((uint32_t)payload[nalu_size + 0] << 24) |
                            ((uint32_t)payload[nalu_size + 1] << 16) |
                            ((uint32_t)payload[nalu_size + 2] << 8) |
                            (uint32_t)payload[nalu_size + 3];
                        if (nc_len == 0 || nc_len > (uint32_t)(payloadsize - nalu_size - 4))
                            break;
                        payload[nalu_size + 0] = 0;
                        payload[nalu_size + 1] = 0;
                        payload[nalu_size + 2] = 0;
                        payload[nalu_size + 3] = 1;
                        nalu_size += (int)nc_len + 4;
                        nalu_num++;
                        valid_nalus = nalu_size == payloadsize;
                    }
                    if (!valid_nalus || nalu_size != payloadsize) {
                        logger_log(raop_rtp_mirror->logger, LOGGER_WARNING,
                            "Invalid decrypted mirror NAL payload size=%d offset=%d",
                            payloadsize, nalu_size);
                        free(payload_in);
                        free(payload);
                        memset(packet, 0, 128);
                        readstart = 0;
                        continue;
                    }
'@ -replace '\r?\n', $LineFeed
    $Text = Replace-Once $Text $Needle $Replacement 'mirror NAL bounds validation'

    $Needle = @'
                    do {
                        // payload data
                        ret = recv(stream_fd, payload + readstart, payloadsize - readstart, 0);
                        readstart = readstart + ret;
                    } while (readstart < payloadsize);
                    h264codec_t h264;
'@ -replace '\r?\n', $LineFeed
    $Replacement = @'
                    if (mirror_recv_exact(raop_rtp_mirror, stream_fd,
                        payload, payloadsize) != 0) {
                        free(payload);
                        exceptionExit = 1;
                        break;
                    }
                    readstart = payloadsize;
                    h264codec_t h264;
'@ -replace '\r?\n', $LineFeed
    $Text = Replace-Once $Text $Needle $Replacement 'SPS payload exact read'

    $PayloadIndent = ' ' * 24
    $Needle = $PayloadIndent + 'do {' + $LineFeed +
        $PayloadIndent + '    ret = recv(stream_fd, payload_in + readstart, payloadsize - readstart, 0);' +
        $LineFeed + $PayloadIndent + '    readstart = readstart + ret;' + $LineFeed +
        $PayloadIndent + '} while (readstart < payloadsize);'
    $Count = [regex]::Matches($Text, [regex]::Escape($Needle)).Count
    if ($Count -ne 3) {
        throw "auxiliary mirror payload reads changed; expected three loops, found $Count."
    }
    $Replacement = @'
                    if (mirror_recv_exact(raop_rtp_mirror, stream_fd,
                        payload_in, payloadsize) != 0) {
                        free(payload_in);
                        exceptionExit = 1;
                        break;
                    }
                    readstart = payloadsize;
'@ -replace '\r?\n', $LineFeed
    $Text = $Text.Replace($Needle, $Replacement)
    Write-Source $File $Text
}

Write-Host 'Applied upstream mirror stream recovery patch 37d7fd0f.' -ForegroundColor Green

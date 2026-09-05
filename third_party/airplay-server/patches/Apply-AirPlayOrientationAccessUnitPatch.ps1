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

$Marker = 'IPHONE_MIRROR_ORIENTATION_ACCESS_UNIT'
$Text = Read-Source $File
if (-not $Text.Contains($Marker)) {
    # AirPlay delivers the new parameter sets in a type-1 packet, then the
    # matching encrypted IDR in a type-0 packet with the same NTP timestamp.
    # FFmpeg needs both in one access unit to reconfigure in-place.
    $Needle = @'
    uint64_t pts_base = 0;
    uint64_t pts = 0;
'@ -replace '\r?\n', $LineFeed
    $Replacement = @'
    uint64_t pts_base = 0;
    uint64_t pts = 0;
    /* IPHONE_MIRROR_ORIENTATION_ACCESS_UNIT: retain only the most recent
     * unencrypted SPS/PPS until the matching encrypted IDR arrives. */
    unsigned char* pending_sps_pps = NULL;
    int pending_sps_pps_len = 0;
    uint64_t pending_sps_pps_timestamp = 0;
'@ -replace '\r?\n', $LineFeed
    $Text = Replace-Once $Text $Needle $Replacement 'orientation parameter-set state'

    $Needle = @'
                    h264_decode_struct h264_data;
                    h264_data.data_len = payloadsize;
                    h264_data.data = payload;
                    h264_data.frame_type = 1;
                    h264_data.pts = pts;
                    raop_rtp_mirror->callbacks.video_process(raop_rtp_mirror->callbacks.cls, &h264_data, raop_rtp_mirror->remoteName, raop_rtp_mirror->remoteDeviceId);
                    free(payload_in);
                    free(payload);
'@ -replace '\r?\n', $LineFeed
    $Replacement = @'
                    /* A parameter-set update belongs to the first IDR with
                     * the same NTP timestamp. Passing it separately leaves
                     * some FFmpeg H.264 decoders stuck on the old geometry. */
                    int has_new_parameter_sets = 0;
                    if (pending_sps_pps != NULL) {
                        if (pending_sps_pps_timestamp == payloadntp) {
                            unsigned char* access_unit = malloc((size_t)pending_sps_pps_len + (size_t)payloadsize);
                            if (access_unit == NULL) {
                                logger_log(raop_rtp_mirror->logger, LOGGER_WARNING,
                                    "Unable to allocate rotated H.264 access unit");
                                free(pending_sps_pps);
                                pending_sps_pps = NULL;
                                pending_sps_pps_len = 0;
                            } else {
                                memcpy(access_unit, pending_sps_pps, pending_sps_pps_len);
                                memcpy(access_unit + pending_sps_pps_len, payload, payloadsize);
                                free(pending_sps_pps);
                                pending_sps_pps = NULL;
                                free(payload);
                                payload = access_unit;
                                payloadsize += pending_sps_pps_len;
                                pending_sps_pps_len = 0;
                                has_new_parameter_sets = 1;
                                logger_log(raop_rtp_mirror->logger, LOGGER_DEBUG,
                                    "IPHONE_MIRROR_ORIENTATION_ACCESS_UNIT merged SPS/PPS with IDR");
                            }
                        } else {
                            logger_log(raop_rtp_mirror->logger, LOGGER_DEBUG,
                                "Discarding unmatched H.264 SPS/PPS timestamp");
                            free(pending_sps_pps);
                            pending_sps_pps = NULL;
                            pending_sps_pps_len = 0;
                        }
                    }
                    h264_decode_struct h264_data;
                    h264_data.data_len = payloadsize;
                    h264_data.data = payload;
                    h264_data.frame_type = has_new_parameter_sets ? 0 : 1;
                    h264_data.pts = pts;
                    raop_rtp_mirror->callbacks.video_process(raop_rtp_mirror->callbacks.cls, &h264_data, raop_rtp_mirror->remoteName, raop_rtp_mirror->remoteDeviceId);
                    free(payload_in);
                    free(payload);
'@ -replace '\r?\n', $LineFeed
    $Text = Replace-Once $Text $Needle $Replacement 'orientation access-unit merge'

    $Needle = @'
                        h264_decode_struct h264_data;
                        h264_data.data_len = sps_pps_len;
                        h264_data.data = sps_pps;
                        h264_data.frame_type = 0;
                        h264_data.pts = 0;
                        raop_rtp_mirror->callbacks.video_process(raop_rtp_mirror->callbacks.cls, &h264_data, raop_rtp_mirror->remoteName, raop_rtp_mirror->remoteDeviceId);
                        free(sps_pps);
'@ -replace '\r?\n', $LineFeed
    $Replacement = @'
                        if (pending_sps_pps != NULL) {
                            free(pending_sps_pps);
                        }
                        pending_sps_pps = sps_pps;
                        pending_sps_pps_len = sps_pps_len;
                        pending_sps_pps_timestamp = byteutils_get_long(packet, 8);
                        sps_pps = NULL;
'@ -replace '\r?\n', $LineFeed
    $Text = Replace-Once $Text $Needle $Replacement 'orientation parameter-set deferral'

    $Needle = @'
    /* Close the stream file descriptor */
    if (stream_fd != -1) {
'@ -replace '\r?\n', $LineFeed
    $Replacement = @'
    if (pending_sps_pps != NULL) {
        free(pending_sps_pps);
        pending_sps_pps = NULL;
    }

    /* Close the stream file descriptor */
    if (stream_fd != -1) {
'@ -replace '\r?\n', $LineFeed
    $Text = Replace-Once $Text $Needle $Replacement 'orientation parameter-set cleanup'
    Write-Source $File $Text
}

if (-not $Text.Contains($Marker) -or
    -not $Text.Contains('pending_sps_pps_timestamp == payloadntp') -or
    -not $Text.Contains('has_new_parameter_sets ? 0 : 1')) {
    throw 'AirPlay orientation access-unit patch was not applied.'
}

Write-Host 'Applied AirPlay in-place orientation access-unit patch.' -ForegroundColor Green

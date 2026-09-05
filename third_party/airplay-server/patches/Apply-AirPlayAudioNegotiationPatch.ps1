[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$SourceRoot
)

$ErrorActionPreference = 'Stop'
$SourceRoot = (Resolve-Path -LiteralPath $SourceRoot).Path
$Utf8 = [Text.UTF8Encoding]::new($false)
$Tab = [string][char]9
$RtpHeaderFile = Join-Path $SourceRoot 'AirPlayServerLib\lib\raop_rtp.h'
$RtpFile = Join-Path $SourceRoot 'AirPlayServerLib\lib\raop_rtp.c'
$BufferHeaderFile = Join-Path $SourceRoot 'AirPlayServerLib\lib\raop_buffer.h'
$BufferFile = Join-Path $SourceRoot 'AirPlayServerLib\lib\raop_buffer.c'
$HandlersFile = Join-Path $SourceRoot 'AirPlayServerLib\lib\raop_handlers.h'
foreach ($File in @($RtpHeaderFile, $RtpFile, $BufferHeaderFile, $BufferFile,
        $HandlersFile)) {
    if (-not (Test-Path -LiteralPath $File -PathType Leaf)) {
        throw "AirPlay audio negotiation source is missing: $File"
    }
}

function Replace-Once([string]$Content, [string]$Needle,
    [string]$Replacement, [string]$Description) {
    $Count = [regex]::Matches($Content, [regex]::Escape($Needle)).Count
    if ($Count -ne 1) {
        throw "$Description changed; expected one insertion point, found $Count."
    }
    $Content.Replace($Needle, $Replacement)
}

$Marker = 'IPHONE_MIRROR_AUDIO_CODEC_NEGOTIATION'

$RtpHeader = [IO.File]::ReadAllText($RtpHeaderFile, $Utf8)
if (-not $RtpHeader.Contains($Marker) -and
    -not $RtpHeader.Contains('unsigned char codec_type,')) {
    $NewLine = if ($RtpHeader.Contains("`r`n")) { "`r`n" } else { "`n" }
    $Needle = 'raop_rtp_t *raop_rtp_init(logger_t *logger, raop_callbacks_t *callbacks, const unsigned char *local, int locallen,' +
        $NewLine + '                          const unsigned char *remote, int remotelen,'
    # Keep the existing public signature untouched; only add the negotiated
    # codec to the audio-start call below.
    $Needle = 'void raop_rtp_start_audio(raop_rtp_t *raop_rtp, int use_udp, unsigned short control_rport, unsigned short timing_rport,' +
        $NewLine + '                     unsigned short *control_lport, unsigned short *timing_lport, unsigned short *data_lport);'
    $Replacement = '/* IPHONE_MIRROR_AUDIO_CODEC_NEGOTIATION */' + $NewLine +
        'void raop_rtp_start_audio(raop_rtp_t *raop_rtp, int use_udp, unsigned short control_rport, unsigned short timing_rport,' +
        $NewLine + '                     unsigned char codec_type,' + $NewLine +
        '                     unsigned short *control_lport, unsigned short *timing_lport, unsigned short *data_lport);'
    $RtpHeader = Replace-Once $RtpHeader $Needle $Replacement 'RAOP audio codec declaration'
    [IO.File]::WriteAllText($RtpHeaderFile, $RtpHeader, $Utf8)
}

$Rtp = [IO.File]::ReadAllText($RtpFile, $Utf8)
$RtpNewLine = if ($Rtp.Contains("`r`n")) { "`r`n" } else { "`n" }
if (-not $Rtp.Contains($Marker)) {
    $Needle = '    unsigned short control_seqnum;' + $RtpNewLine + '};'
    $Replacement = '    unsigned short control_seqnum;' + $RtpNewLine +
        '    unsigned char ct; /* IPHONE_MIRROR_AUDIO_CODEC_NEGOTIATION */' + $RtpNewLine +
        '};'
    $Rtp = Replace-Once $Rtp $Needle $Replacement 'RAOP audio codec state declaration'
    $Needle = 'void' + $RtpNewLine +
        'raop_rtp_start_audio(raop_rtp_t *raop_rtp, int use_udp, unsigned short control_rport, unsigned short timing_rport,' + $RtpNewLine +
        '                     unsigned short *control_lport, unsigned short *timing_lport, unsigned short *data_lport)'
    $Replacement = 'void' + $RtpNewLine +
        'raop_rtp_start_audio(raop_rtp_t *raop_rtp, int use_udp, unsigned short control_rport, unsigned short timing_rport,' + $RtpNewLine +
        '                     unsigned char codec_type,' + $RtpNewLine +
        '                     unsigned short *control_lport, unsigned short *timing_lport, unsigned short *data_lport)'
    $Rtp = Replace-Once $Rtp $Needle $Replacement 'RAOP audio codec definition'

    $Needle = '    /* Initialize ports and sockets */' + $RtpNewLine +
        '    raop_rtp->control_rport = control_rport;'
    $Replacement = '    /* Initialize ports and sockets */' + $RtpNewLine +
        '    /* IPHONE_MIRROR_AUDIO_CODEC_NEGOTIATION */' + $RtpNewLine +
        '    raop_rtp->ct = codec_type;' + $RtpNewLine +
        '    raop_rtp->timing_rport = timing_rport ? timing_rport : raop_rtp->timing_rport;' + $RtpNewLine +
        '    logger_log(raop_rtp->logger, LOGGER_INFO,' + $RtpNewLine +
        '        "IPHONE_MIRROR_AUDIO_CODEC_NEGOTIATION codec=%u control_port=%u",' + $RtpNewLine +
        '        (unsigned int)codec_type, (unsigned int)control_rport);' + $RtpNewLine +
        '    raop_rtp->control_rport = control_rport;'
    $Rtp = Replace-Once $Rtp $Needle $Replacement 'RAOP audio codec initialization'

    $QueueNeedle = 'raop_buffer_queue(raop_rtp->buffer, packet+4, packetlen-4, &raop_rtp->callbacks);'
    $Rtp = Replace-Once $Rtp $QueueNeedle (
        'raop_buffer_queue(raop_rtp->buffer, packet+4, packetlen-4, &raop_rtp->callbacks,' +
        $RtpNewLine + '                    raop_rtp->ct);') 'RAOP control codec propagation'
    $QueueNeedle = 'buf_ret = raop_buffer_queue(raop_rtp->buffer, packet, packetlen, &raop_rtp->callbacks);'
    $Rtp = Replace-Once $Rtp $QueueNeedle (
        'buf_ret = raop_buffer_queue(raop_rtp->buffer, packet, packetlen, &raop_rtp->callbacks,' +
        $RtpNewLine + '                    raop_rtp->ct);') 'RAOP data codec propagation'
    [IO.File]::WriteAllText($RtpFile, $Rtp, $Utf8)
}

$BufferHeader = [IO.File]::ReadAllText($BufferHeaderFile, $Utf8)
if (-not $BufferHeader.Contains($Marker) -and
    -not $BufferHeader.Contains('unsigned char codec_type);')) {
    $NewLine = if ($BufferHeader.Contains("`r`n")) { "`r`n" } else { "`n" }
    $Needle = 'int raop_buffer_queue(raop_buffer_t *raop_buffer, unsigned char *data, unsigned short datalen, raop_callbacks_t *callbacks);'
    $Replacement = '/* IPHONE_MIRROR_AUDIO_CODEC_NEGOTIATION */' + $NewLine +
        'int raop_buffer_queue(raop_buffer_t *raop_buffer, unsigned char *data, unsigned short datalen, raop_callbacks_t *callbacks,' +
        $NewLine + '                          unsigned char codec_type);'
    $BufferHeader = Replace-Once $BufferHeader $Needle $Replacement 'audio buffer codec declaration'
    [IO.File]::WriteAllText($BufferHeaderFile, $BufferHeader, $Utf8)
}

$Buffer = [IO.File]::ReadAllText($BufferFile, $Utf8)
$BufferNewLine = if ($Buffer.Contains("`r`n")) { "`r`n" } else { "`n" }
if (-not $Buffer.Contains($Marker)) {
    if (-not $Buffer.Contains('unsigned char codec_type)')) {
        $Needle = 'raop_buffer_queue(raop_buffer_t *raop_buffer, unsigned char *data, unsigned short datalen, raop_callbacks_t *callbacks)'
        $Replacement = 'raop_buffer_queue(raop_buffer_t *raop_buffer, unsigned char *data, unsigned short datalen, raop_callbacks_t *callbacks,' +
            $BufferNewLine + '                   unsigned char codec_type)'
        $Buffer = Replace-Once $Buffer $Needle $Replacement 'audio buffer codec definition'
    }
    $Needle = $Tab + '// AirPlay music uses ALAC while screen mirroring uses AAC-ELD.'
    $Replacement = $Needle + $BufferNewLine +
        $Tab + '/* IPHONE_MIRROR_AUDIO_CODEC_NEGOTIATION: prefer the negotiated' + $BufferNewLine +
        $Tab + ' * compression type; retain the payload marker for legacy senders. */'
    $Buffer = Replace-Once $Buffer $Needle $Replacement 'audio codec selection marker'
    $Buffer = Replace-Once $Buffer 'if (payloadsize > 0 && packetbuf[0] == 0x20) {' 'if (codec_type == 2 || (codec_type == 0 && payloadsize > 0 && packetbuf[0] == 0x20)) {' 'audio codec selection'
    [IO.File]::WriteAllText($BufferFile, $Buffer, $Utf8)
}

$Handlers = [IO.File]::ReadAllText($HandlersFile, $Utf8)
$HandlersNewLine = if ($Handlers.Contains("`r`n")) { "`r`n" } else { "`n" }
if (-not $Handlers.Contains($Marker)) {
    $Needle = $Tab + 'plist_t stream_id_note = NULL; // exists on second call' + $HandlersNewLine +
        $Tab + 'plist_t audio_format_note = NULL; // exists on third call' + $HandlersNewLine +
        $Tab + 'uint64_t stream_type = 0; // 110 = mirror, 96 = audio'
    $Replacement = $Needle + $HandlersNewLine +
        $Tab + 'uint64_t stream_control_port = 0;' + $HandlersNewLine +
        $Tab + 'unsigned char stream_codec_type = 0; /* IPHONE_MIRROR_AUDIO_CODEC_NEGOTIATION */'
    $Handlers = Replace-Once $Handlers $Needle $Replacement 'audio stream negotiation state'

    $DoubleTab = $Tab + $Tab
    $Needle = $DoubleTab + 'stream_id_note = plist_dict_get_item(stream_note, "streamConnectionID");' + $HandlersNewLine +
        $DoubleTab + 'audio_format_note = plist_dict_get_item(stream_note, "audioFormat");'
    $Replacement = $Needle + $HandlersNewLine +
        $DoubleTab + 'plist_t control_port_note = plist_dict_get_item(stream_note, "controlPort");' + $HandlersNewLine +
        $DoubleTab + 'if (control_port_note != NULL) {' + $HandlersNewLine +
        $Tab + $Tab + $Tab + 'uint64_t value = 0;' + $HandlersNewLine +
        $Tab + $Tab + $Tab + 'plist_get_uint_val(control_port_note, &value);' + $HandlersNewLine +
        $Tab + $Tab + $Tab + 'stream_control_port = value <= 65535 ? value : 0;' + $HandlersNewLine +
        $DoubleTab + '}' + $HandlersNewLine +
        $DoubleTab + 'plist_t codec_type_note = plist_dict_get_item(stream_note, "ct");' + $HandlersNewLine +
        $DoubleTab + 'if (codec_type_note != NULL) {' + $HandlersNewLine +
        $Tab + $Tab + $Tab + 'uint64_t value = 0;' + $HandlersNewLine +
        $Tab + $Tab + $Tab + 'plist_get_uint_val(codec_type_note, &value);' + $HandlersNewLine +
        $Tab + $Tab + $Tab + 'stream_codec_type = value <= 255 ? (unsigned char)value : 0;' + $HandlersNewLine +
        $DoubleTab + '}'
    $Handlers = Replace-Once $Handlers $Needle $Replacement 'audio stream codec parsing'

    $Needle = 'raop_rtp_start_audio(conn->raop_rtp, use_udp, remote_cport, remote_tport, &cport, &tport, &dport);'
    $Replacement = 'raop_rtp_start_audio(conn->raop_rtp, use_udp, remote_cport, remote_tport,' +
        $HandlersNewLine +
        ('                stream_codec_type, &cport, &tport, &dport);')
    $Handlers = Replace-Once $Handlers $Needle $Replacement 'audio codec propagation to RTP'
    $Handlers = $Handlers.Replace(
        'remote_cport = (unsigned short) uint_val;   /* must != 0 to activate audio resend requests */',
        'remote_cport = (unsigned short) (stream_control_port != 0 ? stream_control_port : uint_val);   /* must != 0 to activate audio resend requests */')
    [IO.File]::WriteAllText($HandlersFile, $Handlers, $Utf8)
}

$VerifiedRtp = [IO.File]::ReadAllText($RtpFile, $Utf8)
$VerifiedBuffer = [IO.File]::ReadAllText($BufferFile, $Utf8)
$VerifiedHandlers = [IO.File]::ReadAllText($HandlersFile, $Utf8)
if (-not $VerifiedRtp.Contains($Marker) -or
    -not $VerifiedRtp.Contains('raop_rtp->ct = codec_type;') -or
    -not $VerifiedRtp.Contains('raop_rtp->ct);') -or
    -not $VerifiedBuffer.Contains($Marker) -or
    -not $VerifiedBuffer.Contains('codec_type == 2') -or
    -not $VerifiedHandlers.Contains('stream_codec_type') -or
    -not $VerifiedHandlers.Contains('stream_control_port')) {
    throw 'AirPlay audio codec negotiation patch verification failed.'
}
Write-Host 'Applied negotiated AirPlay audio codec and control-port handling.' -ForegroundColor Green

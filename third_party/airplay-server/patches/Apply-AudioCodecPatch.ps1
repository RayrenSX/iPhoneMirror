[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$SourceRoot
)

$ErrorActionPreference = 'Stop'
$SourceRoot = (Resolve-Path -LiteralPath $SourceRoot).Path
$BufferFile = Join-Path $SourceRoot 'AirPlayServerLib\lib\raop_buffer.c'
$RtpFile = Join-Path $SourceRoot 'AirPlayServerLib\lib\raop_rtp.c'
$ProjectFile = Join-Path $SourceRoot 'AirPlayServerLib\AirPlayLib.vcxproj'
$WrapperFile = Join-Path $SourceRoot 'airplay2dll\src\FgAirplayServer.cpp'
foreach ($File in @($BufferFile, $RtpFile, $ProjectFile, $WrapperFile)) {
    if (-not (Test-Path -LiteralPath $File -PathType Leaf)) {
        throw "AirPlay audio codec source is missing: $File"
    }
}

function Normalize-NewLines([string]$Text, [string]$NewLine) {
    return $Text -replace "`r?`n", $NewLine
}

$Buffer = [IO.File]::ReadAllText($BufferFile)
$BufferNewLine = if ($Buffer.Contains("`r`n")) { "`r`n" } else { "`n" }
$Marker = 'IPHONE_MIRROR_ALAC_AUDIO_DECODE'
if (-not $Buffer.Contains($Marker)) {
    $IncludeNeedle = Normalize-NewLines @'
#include "stream.h"
'@ $BufferNewLine
    $IncludeReplacement = Normalize-NewLines @'
#include "stream.h"
#include <libavcodec/avcodec.h>
#include <libavutil/frame.h>
#include <libavutil/mem.h>
#include <libavutil/samplefmt.h>
'@ $BufferNewLine
    if ($Buffer.IndexOf($IncludeNeedle, [StringComparison]::Ordinal) -lt 0) {
        throw 'AirPlay audio include patch point changed.'
    }
    $Buffer = $Buffer.Replace($IncludeNeedle, $IncludeReplacement)

    $StateNeedle = Normalize-NewLines @'
    HANDLE_AACDECODER phandle;
'@ $BufferNewLine
    $StateReplacement = Normalize-NewLines @'
    HANDLE_AACDECODER phandle;
    AVCodecContext *alac_context;
    AVFrame *alac_frame;
    unsigned int aac_decode_failures;
    unsigned int alac_decode_failures;
'@ $BufferNewLine
    if ($Buffer.IndexOf($StateNeedle, [StringComparison]::Ordinal) -lt 0) {
        throw 'AirPlay audio decoder state patch point changed.'
    }
    $Buffer = $Buffer.Replace($StateNeedle, $StateReplacement)

    $HelperNeedle = Normalize-NewLines @'
    return phandle;
}

void
raop_buffer_init_key_iv
'@ $BufferNewLine
    $HelperReplacement = Normalize-NewLines @'
    return phandle;
}

static AVCodecContext *
create_alac_decoder(logger_t *logger)
{
    static const unsigned char alac_extradata[36] = {
        0x00, 0x00, 0x00, 0x24, 0x61, 0x6c, 0x61, 0x63,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x60,
        0x00, 0x10, 0x28, 0x0a, 0x0e, 0x02, 0x00, 0xff,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0xac, 0x44
    };
    AVCodec *codec = avcodec_find_decoder(AV_CODEC_ID_ALAC);
    AVCodecContext *context;
    int result;
    if (!codec) {
        logger_log(logger, LOGGER_ERR,
            "IPHONE_MIRROR_ALAC_AUDIO_DECODE init_failed step=find_decoder");
        return NULL;
    }
    context = avcodec_alloc_context3(codec);
    if (!context) return NULL;
    context->extradata = av_mallocz(sizeof(alac_extradata) +
        AV_INPUT_BUFFER_PADDING_SIZE);
    if (!context->extradata) {
        avcodec_free_context(&context);
        return NULL;
    }
    memcpy(context->extradata, alac_extradata, sizeof(alac_extradata));
    context->extradata_size = sizeof(alac_extradata);
    result = avcodec_open2(context, codec, NULL);
    if (result < 0) {
        logger_log(logger, LOGGER_ERR,
            "IPHONE_MIRROR_ALAC_AUDIO_DECODE init_failed step=open code=%d", result);
        avcodec_free_context(&context);
        return NULL;
    }
    logger_log(logger, LOGGER_INFO,
        "IPHONE_MIRROR_ALAC_AUDIO_DECODE initialized rate=44100 channels=2 bits=16");
    return context;
}

static int
decode_alac_packet(raop_buffer_t *raop_buffer, raop_buffer_entry_t *entry,
                   unsigned char *packet, int packet_size)
{
    AVPacket av_packet;
    AVFrame *frame = raop_buffer->alac_frame;
    int result;
    int channels;
    int samples;
    int output_size;
    int index;
    if (!raop_buffer->alac_context) {
        raop_buffer->alac_context = create_alac_decoder(raop_buffer->logger);
        if (!raop_buffer->alac_context) return -1;
    }
    av_init_packet(&av_packet);
    av_packet.data = packet;
    av_packet.size = packet_size;
    av_frame_unref(frame);
    result = avcodec_send_packet(raop_buffer->alac_context, &av_packet);
    if (result < 0) return result;
    result = avcodec_receive_frame(raop_buffer->alac_context, frame);
    if (result < 0) return result;
    channels = frame->channels;
    samples = frame->nb_samples;
    output_size = samples * channels * (int)sizeof(short);
    if (channels < 1 || channels > 2 || samples <= 0 ||
        output_size > entry->audio_buffer_size || !frame->extended_data) {
        return -1;
    }
    if (frame->format == AV_SAMPLE_FMT_S16) {
        memcpy(entry->audio_buffer, frame->extended_data[0], output_size);
    } else if (frame->format == AV_SAMPLE_FMT_S16P) {
        short *output = (short *)entry->audio_buffer;
        for (index = 0; index < samples; ++index) {
            int channel;
            for (channel = 0; channel < channels; ++channel) {
                const short *plane = (const short *)frame->extended_data[channel];
                output[index * channels + channel] = plane[index];
            }
        }
    } else {
        return -1;
    }
    entry->audio_buffer_len = output_size;
    entry->sample_rate = frame->sample_rate > 0 ? frame->sample_rate :
        raop_buffer->alac_context->sample_rate;
    entry->channels = (uint16_t)channels;
    entry->bits_per_sample = 16;
    return 0;
}

void
raop_buffer_init_key_iv
'@ $BufferNewLine
    if ($Buffer.IndexOf($HelperNeedle, [StringComparison]::Ordinal) -lt 0) {
        throw 'AirPlay audio decoder helper patch point changed.'
    }
    $Buffer = $Buffer.Replace($HelperNeedle, $HelperReplacement)

    $InitNeedle = Normalize-NewLines @'
    raop_buffer->phandle = create_fdk_aac_decoder(logger);
    if (!raop_buffer->phandle) {
        free(raop_buffer);
        return NULL;
    }
	raop_buffer->buffer_size = audio_buffer_size * RAOP_BUFFER_LENGTH;
'@ $BufferNewLine
    $InitReplacement = Normalize-NewLines @'
    raop_buffer->phandle = create_fdk_aac_decoder(logger);
    if (!raop_buffer->phandle) {
        free(raop_buffer);
        return NULL;
    }
    raop_buffer->alac_frame = av_frame_alloc();
    if (!raop_buffer->alac_frame) {
        aacDecoder_Close(raop_buffer->phandle);
        free(raop_buffer);
        return NULL;
    }
	raop_buffer->buffer_size = audio_buffer_size * RAOP_BUFFER_LENGTH;
'@ $BufferNewLine
    if ($Buffer.IndexOf($InitNeedle, [StringComparison]::Ordinal) -lt 0) {
        throw 'AirPlay audio decoder initialization patch point changed.'
    }
    $Buffer = $Buffer.Replace($InitNeedle, $InitReplacement)

    $AllocationFailureNeedle = Normalize-NewLines @'
        if (raop_buffer->phandle) {
            free(raop_buffer->phandle);
        }
		free(raop_buffer);
'@ $BufferNewLine
    $AllocationFailureReplacement = Normalize-NewLines @'
        if (raop_buffer->phandle) {
            aacDecoder_Close(raop_buffer->phandle);
        }
        av_frame_free(&raop_buffer->alac_frame);
		free(raop_buffer);
'@ $BufferNewLine
    if ($Buffer.IndexOf($AllocationFailureNeedle, [StringComparison]::Ordinal) -lt 0) {
        throw 'AirPlay audio allocation cleanup patch point changed.'
    }
    $Buffer = $Buffer.Replace($AllocationFailureNeedle, $AllocationFailureReplacement)

    $DestroyNeedle = Normalize-NewLines @'
	    aacDecoder_Close(raop_buffer->phandle);
		free(raop_buffer->buffer);
'@ $BufferNewLine
    $DestroyReplacement = Normalize-NewLines @'
	    aacDecoder_Close(raop_buffer->phandle);
        avcodec_free_context(&raop_buffer->alac_context);
        av_frame_free(&raop_buffer->alac_frame);
		free(raop_buffer->buffer);
'@ $BufferNewLine
    if ($Buffer.IndexOf($DestroyNeedle, [StringComparison]::Ordinal) -lt 0) {
        throw 'AirPlay audio decoder cleanup patch point changed.'
    }
    $Buffer = $Buffer.Replace($DestroyNeedle, $DestroyReplacement)

    $DecodePattern = '(?s)\t// aac decode to pcm\r?\n.*?\t}\r?\n#ifdef DUMP_AUDIO'
    if ([regex]::Matches($Buffer, $DecodePattern).Count -ne 1) {
        throw 'AirPlay audio decode patch point changed.'
    }
    $DecodeReplacement = Normalize-NewLines @'
	// AirPlay music uses ALAC while screen mirroring uses AAC-ELD.
    entry->audio_buffer_len = 0;
    if (payloadsize > 0 && packetbuf[0] == 0x20) {
        int ret = decode_alac_packet(raop_buffer, entry, packetbuf, payloadsize);
        if (ret < 0) {
            raop_buffer->alac_decode_failures++;
            if (raop_buffer->alac_decode_failures <= 3) {
                logger_log(raop_buffer->logger, LOGGER_ERR,
                    "ALAC decode failed code=%d count=%u", ret,
                    raop_buffer->alac_decode_failures);
            }
        }
    } else {
        int ret = 0;
        int pkt_size = payloadsize;
        UINT valid_size = payloadsize;
        UCHAR *input_buf[1] = {packetbuf};
        CStreamInfo *streamInfo = NULL;
        ret = aacDecoder_Fill(raop_buffer->phandle, input_buf, &pkt_size, &valid_size);
        if (ret == AAC_DEC_OK) {
            const int pcm_capacity = entry->audio_buffer_size / sizeof(INT_PCM);
            ret = aacDecoder_DecodeFrame(raop_buffer->phandle,
                entry->audio_buffer, pcm_capacity, fdk_flags);
        }
        if (ret == AAC_DEC_OK) {
            streamInfo = aacDecoder_GetStreamInfo(raop_buffer->phandle);
        }
        if (streamInfo != NULL && streamInfo->numChannels > 0 &&
            streamInfo->frameSize > 0) {
            const int decoded_size = streamInfo->frameSize *
                streamInfo->numChannels * sizeof(INT_PCM);
            if (decoded_size <= entry->audio_buffer_size) {
                entry->audio_buffer_len = decoded_size;
                entry->sample_rate = streamInfo->sampleRate;
                entry->channels = (uint16_t)streamInfo->numChannels;
                entry->bits_per_sample = sizeof(INT_PCM) * 8;
            }
        }
        if (entry->audio_buffer_len == 0) {
            raop_buffer->aac_decode_failures++;
            if (raop_buffer->aac_decode_failures <= 3) {
                logger_log(raop_buffer->logger, LOGGER_ERR,
                    "AAC-ELD decode failed code=0x%x count=%u", ret,
                    raop_buffer->aac_decode_failures);
            }
        }
    }
#ifdef DUMP_AUDIO
'@ $BufferNewLine
    $Buffer = [regex]::Replace($Buffer, $DecodePattern, $DecodeReplacement)
    [IO.File]::WriteAllText($BufferFile, $Buffer, [Text.Encoding]::Unicode)
}

$Buffer = [IO.File]::ReadAllText($BufferFile)
$BufferNewLine = if ($Buffer.Contains("`r`n")) { "`r`n" } else { "`n" }
$ConcealmentMarker = 'IPHONE_MIRROR_AUDIO_CONCEALMENT'
if (-not $Buffer.Contains($ConcealmentMarker)) {
    $StateNeedle = Normalize-NewLines @'
    unsigned int alac_decode_failures;
'@ $BufferNewLine
    $StateReplacement = Normalize-NewLines @'
    unsigned int alac_decode_failures;
    /* IPHONE_MIRROR_AUDIO_CONCEALMENT */
    int concealment_buffer_len;
    uint32_t concealment_sample_rate;
    uint16_t concealment_channels;
    uint16_t concealment_bits_per_sample;
'@ $BufferNewLine
    if ([regex]::Matches($Buffer,
            [regex]::Escape($StateNeedle)).Count -ne 1) {
        throw 'AirPlay concealment state patch point changed.'
    }
    $Buffer = $Buffer.Replace($StateNeedle, $StateReplacement)

    $DecodeTailNeedle = Normalize-NewLines @'
        }
    }
#ifdef DUMP_AUDIO
    if (file_pcm != NULL) {
'@ $BufferNewLine
    $DecodeTailReplacement = Normalize-NewLines @'
        }
    }
    if (entry->audio_buffer_len > 0) {
        raop_buffer->concealment_buffer_len = entry->audio_buffer_len;
        raop_buffer->concealment_sample_rate = entry->sample_rate;
        raop_buffer->concealment_channels = entry->channels;
        raop_buffer->concealment_bits_per_sample = entry->bits_per_sample;
    }
#ifdef DUMP_AUDIO
    if (file_pcm != NULL) {
'@ $BufferNewLine
    if ([regex]::Matches($Buffer,
            [regex]::Escape($DecodeTailNeedle)).Count -ne 1) {
        throw 'AirPlay concealment format patch point changed.'
    }
    $Buffer = $Buffer.Replace($DecodeTailNeedle, $DecodeTailReplacement)

    $MissingNeedle = Normalize-NewLines @'
	if (!entry->available) {
		/* Return an empty audio buffer to skip audio */
		*length = entry->audio_buffer_size;
		memset(entry->audio_buffer, 0, *length);
		return entry->audio_buffer;
	}
'@ $BufferNewLine
    $MissingReplacement = Normalize-NewLines @'
	if (!entry->available) {
		/* Conceal an unrecovered packet without changing the PCM clock. */
		*length = raop_buffer->concealment_buffer_len;
        if (*length <= 0 || *length > entry->audio_buffer_size) return NULL;
		memset(entry->audio_buffer, 0, *length);
        *pts = 0;
        *sample_rate = raop_buffer->concealment_sample_rate;
        *channels = raop_buffer->concealment_channels;
        *bits_per_sample = raop_buffer->concealment_bits_per_sample;
		return entry->audio_buffer;
	}
'@ $BufferNewLine
    if ([regex]::Matches($Buffer,
            [regex]::Escape($MissingNeedle)).Count -ne 1) {
        throw 'AirPlay missing-packet concealment patch point changed.'
    }
    $Buffer = $Buffer.Replace($MissingNeedle, $MissingReplacement)
    [IO.File]::WriteAllText($BufferFile, $Buffer, [Text.Encoding]::Unicode)
}

$Rtp = [IO.File]::ReadAllText($RtpFile)
$RtpNewLine = if ($Rtp.Contains("`r`n")) { "`r`n" } else { "`n" }
$RtpMarker = 'IPHONE_MIRROR_SKIP_INVALID_AUDIO'
if (-not $Rtp.Contains($RtpMarker)) {
    $RtpNeedle = Normalize-NewLines @'
                while ((audiobuf = raop_buffer_dequeue(raop_rtp->buffer, &audiobuflen, &pts, no_resend, &sample_rate, &channels, &bits_per_sample))) {
                    pcm_data_struct pcm_data;
'@ $RtpNewLine
    $RtpReplacement = Normalize-NewLines @'
                while ((audiobuf = raop_buffer_dequeue(raop_rtp->buffer, &audiobuflen, &pts, no_resend, &sample_rate, &channels, &bits_per_sample))) {
                    /* IPHONE_MIRROR_SKIP_INVALID_AUDIO */
                    if (audiobuflen <= 0 || sample_rate == 0 || channels == 0 ||
                        bits_per_sample == 0) continue;
                    pcm_data_struct pcm_data;
'@ $RtpNewLine
    if ($Rtp.IndexOf($RtpNeedle, [StringComparison]::Ordinal) -lt 0) {
        throw 'AirPlay invalid audio suppression patch point changed.'
    }
    $Rtp = $Rtp.Replace($RtpNeedle, $RtpReplacement)
}

$ResendMarker = 'IPHONE_MIRROR_RAOP_PACKET_RESEND'
if (-not $Rtp.Contains($ResendMarker)) {
    $NoResendPattern =
        '(?m)^(\s*)int no_resend = 1;[^\r\n]*(?=\r?$)'
    if ([regex]::Matches($Rtp, $NoResendPattern).Count -ne 1) {
        throw 'AirPlay packet-resend patch point changed.'
    }
    $Rtp = [regex]::Replace($Rtp, $NoResendPattern,
        '$1/* IPHONE_MIRROR_RAOP_PACKET_RESEND */' + $RtpNewLine +
        '$1int no_resend = raop_rtp->control_rport == 0;')
}
$JitterWaitMarker = 'IPHONE_MIRROR_RAOP_JITTER_WAIT'
if (-not $Buffer.Contains($JitterWaitMarker)) {
    $JitterWaitPattern =
        '(?m)^(\s*)if \(buflen < 4\) \{[^\r\n]*(?=\r?$)'
    if ([regex]::Matches($Buffer, $JitterWaitPattern).Count -ne 1) {
        throw 'AirPlay jitter-wait patch point changed.'
    }
    $Buffer = [regex]::Replace($Buffer, $JitterWaitPattern,
        '$1/* IPHONE_MIRROR_RAOP_JITTER_WAIT */' + $BufferNewLine +
        '$1if (buflen < 16) {')
    [IO.File]::WriteAllText($BufferFile, $Buffer, [Text.Encoding]::Unicode)
}
[IO.File]::WriteAllText($RtpFile, $Rtp, [Text.Encoding]::Unicode)

$Wrapper = [IO.File]::ReadAllText($WrapperFile)
$WrapperNewLine = if ($Wrapper.Contains("`r`n")) { "`r`n" } else { "`n" }
$LoggingMarker = 'IPHONE_MIRROR_AIRPLAY_INFO_LOGGING'
if (-not $Wrapper.Contains($LoggingMarker)) {
    $AirPlayLogNeedle =
        "`t`tairplay_set_log_level(m_pAirplay, RAOP_LOG_DEBUG);"
    $RaopLogNeedle = "`t`traop_set_log_level(m_pRaop, RAOP_LOG_DEBUG);"
    if ([regex]::Matches($Wrapper,
            [regex]::Escape($AirPlayLogNeedle)).Count -ne 1 -or
        [regex]::Matches($Wrapper,
            [regex]::Escape($RaopLogNeedle)).Count -ne 1) {
        throw 'AirPlay runtime log-level patch point changed.'
    }
    $Wrapper = $Wrapper.Replace($AirPlayLogNeedle,
        "`t`t/* IPHONE_MIRROR_AIRPLAY_INFO_LOGGING */" + $WrapperNewLine +
        "`t`tairplay_set_log_level(m_pAirplay, RAOP_LOG_INFO);")
    $Wrapper = $Wrapper.Replace($RaopLogNeedle,
        "`t`traop_set_log_level(m_pRaop, RAOP_LOG_INFO);")
    [IO.File]::WriteAllText($WrapperFile, $Wrapper,
        [Text.UTF8Encoding]::new($true))
}

$Project = [IO.File]::ReadAllText($ProjectFile)
$FfmpegInclude = '$(SolutionDir)external\ffmpeg\include;'
if (-not $Project.Contains($FfmpegInclude)) {
    $ProjectNeedle = '$(SolutionDir)external;'
    $ProjectMatches = [regex]::Matches($Project,
        [regex]::Escape($ProjectNeedle)).Count
    if ($ProjectMatches -ne 4) {
        throw "AirPlay project include patch point changed: $ProjectMatches"
    }
    $Project = $Project.Replace($ProjectNeedle,
        $ProjectNeedle + $FfmpegInclude)
    [IO.File]::WriteAllText($ProjectFile, $Project,
        [Text.UTF8Encoding]::new($true))
}

$VerifiedBuffer = [IO.File]::ReadAllText($BufferFile)
$VerifiedRtp = [IO.File]::ReadAllText($RtpFile)
$VerifiedProject = [IO.File]::ReadAllText($ProjectFile)
$VerifiedWrapper = [IO.File]::ReadAllText($WrapperFile)
if (-not $VerifiedBuffer.Contains($Marker) -or
    -not $VerifiedBuffer.Contains('AV_CODEC_ID_ALAC') -or
    -not $VerifiedBuffer.Contains('AV_SAMPLE_FMT_S16P') -or
    -not $VerifiedBuffer.Contains($ConcealmentMarker) -or
    -not $VerifiedBuffer.Contains($JitterWaitMarker) -or
    -not $VerifiedBuffer.Contains('if (buflen < 16)') -or
    -not $VerifiedRtp.Contains($RtpMarker) -or
    -not $VerifiedRtp.Contains($ResendMarker) -or
    -not $VerifiedRtp.Contains(
        'int no_resend = raop_rtp->control_rport == 0;') -or
    -not $VerifiedWrapper.Contains($LoggingMarker) -or
    -not $VerifiedWrapper.Contains(
        'airplay_set_log_level(m_pAirplay, RAOP_LOG_INFO);') -or
    -not $VerifiedWrapper.Contains(
        'raop_set_log_level(m_pRaop, RAOP_LOG_INFO);') -or
    -not $VerifiedProject.Contains($FfmpegInclude)) {
    throw 'AirPlay ALAC audio codec patch verification failed.'
}

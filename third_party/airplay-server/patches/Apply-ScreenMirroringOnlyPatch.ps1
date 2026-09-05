[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$SourceRoot
)

$ErrorActionPreference = 'Stop'
$SourceRoot = (Resolve-Path -LiteralPath $SourceRoot).Path
$DnsSdFile = Join-Path $SourceRoot 'AirPlayServerLib\lib\dnssd.c'
$AirPlayFile = Join-Path $SourceRoot 'AirPlayServerLib\lib\airplay.c'
$AirPlayHandlersFile = Join-Path $SourceRoot 'AirPlayServerLib\lib\airplay_handlers.h'
$RaopFile = Join-Path $SourceRoot 'AirPlayServerLib\lib\raop_handlers.h'
$RaopHeaderFile = Join-Path $SourceRoot 'AirPlayServerLib\include\raop.h'
$RaopRouterFile = Join-Path $SourceRoot 'AirPlayServerLib\lib\raop.c'
$PairingFile = Join-Path $SourceRoot 'AirPlayServerLib\lib\pairing.c'
$GlobalFile = Join-Path $SourceRoot 'AirPlayServerLib\lib\global.h'
$MirrorBufferFile = Join-Path $SourceRoot 'AirPlayServerLib\lib\mirror_buffer.c'
$WrapperServerFile = Join-Path $SourceRoot 'airplay2dll\src\FgAirplayServer.cpp'
$ChannelFile = Join-Path $SourceRoot 'airplay2dll\FgAirplayChannel.cpp'
foreach ($File in @($DnsSdFile, $AirPlayFile, $AirPlayHandlersFile,
        $RaopFile, $RaopHeaderFile, $RaopRouterFile, $PairingFile, $GlobalFile,
        $MirrorBufferFile, $WrapperServerFile, $ChannelFile)) {
    if (-not (Test-Path -LiteralPath $File)) {
        throw "AirPlayServer media capability source is missing: $File"
    }
}

$Encoding = [Text.UTF8Encoding]::new($false)
$MirrorBuffer = [IO.File]::ReadAllText($MirrorBufferFile, $Encoding)
$MirrorCommentMarker = 'IPHONE_MIRROR_ASCII_DECRYPT_COMMENT'
if (-not $MirrorBuffer.Contains($MirrorCommentMarker)) {
    $Pattern = '(?m)^(\s*mirror_buffer->nextDecryptCount = 16 - restlen;)[^\r\n]*(?=\r?$)'
    if ([regex]::Matches($MirrorBuffer, $Pattern).Count -ne 1) {
        throw 'AirPlay mirror-buffer comment repair point changed.'
    }
    # The upstream UTF-8 Chinese comment contains a byte that CP936 interprets
    # as a trailing backslash. MSVC then line-splices away the closing brace.
    $MirrorBuffer = [regex]::Replace($MirrorBuffer, $Pattern,
        '$1 // IPHONE_MIRROR_ASCII_DECRYPT_COMMENT')
    [IO.File]::WriteAllText($MirrorBufferFile, $MirrorBuffer, $Encoding)
}
$Global = [IO.File]::ReadAllText($GlobalFile, $Encoding)
$Global = $Global.Replace('"AppleTV14,1"', '"AppleTV3,2"')
$Global = $Global.Replace('"Kodi,1"', '"AppleTV3,2"')
$Global = $Global.Replace('"845.5.1"', '"220.68"')
$GlobalNewLine = if ($Global.Contains("`r`n")) { "`r`n" } else { "`n" }
$Global = $Global.Replace('#define GLOBAL_FEATURES 0x7',
    '#define GLOBAL_FEATURES_1 0x5A7FFEE6U' + $GlobalNewLine +
    '#define GLOBAL_FEATURES_2 0x00000000U')
if (-not $Global.Contains('"AppleTV3,2"') -or
    -not $Global.Contains('"220.68"') -or
    -not $Global.Contains('#define GLOBAL_FEATURES_1 0x5A7FFEE6U') -or
    -not $Global.Contains('#define GLOBAL_FEATURES_2 0x00000000U')) {
    throw 'UxPlay-compatible AirPlay model/version/features were not applied.'
}
[IO.File]::WriteAllText($GlobalFile, $Global, $Encoding)

$Pairing = [IO.File]::ReadAllText($PairingFile, $Encoding)
$PairingMarker = 'IPHONE_MIRROR_STABLE_PAIRING_IDENTITY'
if (-not $Pairing.Contains($PairingMarker)) {
    $PairingNewLine = if ($Pairing.Contains("`r`n")) { "`r`n" } else { "`n" }
    $Needle = @'
pairing_t *
pairing_init_generate()
{
	unsigned char seed[32];

	if (ed25519_create_seed(seed)) {
		return NULL;
	}
	return pairing_init_seed(seed);
}
'@ -replace "`r?`n", $PairingNewLine
    $Replacement = @'
/* IPHONE_MIRROR_STABLE_PAIRING_IDENTITY: AirPlay HTTP and RAOP are separate
 * listeners in this upstream library, but must expose one persistent Ed25519
 * receiver identity.  The host supplies a per-machine 32-byte seed. */
static int
iphone_mirror_hex_nibble(char value)
{
	if (value >= '0' && value <= '9') return value - '0';
	if (value >= 'a' && value <= 'f') return value - 'a' + 10;
	return -1;
}

pairing_t *
pairing_init_generate()
{
	unsigned char seed[32];
	const char *seed_text = getenv("IPHONE_MIRROR_AIRPLAY_PAIRING_SEED");
	int stable = seed_text != NULL && strlen(seed_text) == 64;
	for (int index = 0; stable && index < 32; ++index) {
		int high = iphone_mirror_hex_nibble(seed_text[index * 2]);
		int low = iphone_mirror_hex_nibble(seed_text[index * 2 + 1]);
		if (high < 0 || low < 0) stable = 0;
		else seed[index] = (unsigned char)((high << 4) | low);
	}
	if (!stable && ed25519_create_seed(seed)) return NULL;

	pairing_t *pairing = pairing_init_seed(seed);
	if (pairing != NULL) {
		static const char hex[] = "0123456789abcdef";
		char public_key[65];
		for (int index = 0; index < 32; ++index) {
			public_key[index * 2] = hex[pairing->ed_public[index] >> 4];
			public_key[index * 2 + 1] = hex[pairing->ed_public[index] & 15];
		}
		public_key[64] = '\0';
#ifdef _WIN32
		_putenv_s("IPHONE_MIRROR_AIRPLAY_PUBLIC_KEY", public_key);
#else
		setenv("IPHONE_MIRROR_AIRPLAY_PUBLIC_KEY", public_key, 1);
#endif
	}
	return pairing;
}
'@ -replace "`r?`n", $PairingNewLine
    if ([regex]::Matches($Pairing, [regex]::Escape($Needle)).Count -ne 1) {
        throw 'AirPlay pairing generator changed; stable identity patch did not apply.'
    }
    $Pairing = $Pairing.Replace($Needle, $Replacement)
    [IO.File]::WriteAllText($PairingFile, $Pairing, $Encoding)
}

$DnsSd = [IO.File]::ReadAllText($DnsSdFile, $Encoding)
$FeatureFormatter =
    'snprintf(features, sizeof(features), "0x%X,0x%X", GLOBAL_FEATURES_1, GLOBAL_FEATURES_2);'
$RaopFeatureRecord =
    'TXTRecordSetValue(&txtRecord, "ft", strlen(features), features);'
$AirPlayFeatureRecord =
    'TXTRecordSetValue(&txtRecord, "features", strlen(features), features);'
if ([regex]::Matches($DnsSd, [regex]::Escape($FeatureFormatter)).Count -ne 2 -or
    [regex]::Matches($DnsSd, [regex]::Escape($RaopFeatureRecord)).Count -ne 1 -or
    [regex]::Matches($DnsSd, [regex]::Escape($AirPlayFeatureRecord)).Count -ne 1) {
    throw 'AirPlayServer v1.1.2 DNS-SD feature/ft declarations changed.'
}
Write-Host 'Verified matching upstream RAOP and AirPlay DNS-SD capabilities.'

$AirPlay = [IO.File]::ReadAllText($AirPlayFile, $Encoding)
$AirPlay = $AirPlay.Replace(
    '*response = http_response_init("RTSP/1.0", 200, "OK");',
    '*response = http_response_init("HTTP/1.1", 200, "OK");')
$AirPlay = $AirPlay.Replace('AirTunes/845.5.1', 'AirTunes/220.68')
$AirPlay = $AirPlay.Replace('<integer>119</integer>', '<integer>1518337766</integer>')
$AirPlay = $AirPlay.Replace('<integer>64</integer>', '<integer>1518337766</integer>')
$AirPlay = $AirPlay.Replace('<integer>55</integer>', '<integer>1518337766</integer>')
$AirPlay = $AirPlay.Replace('<integer>639</integer>', '<integer>1518337766</integer>')
$AirPlay = $AirPlay.Replace('<integer>1518337783</integer>',
    '<integer>1518337766</integer>')
$AirPlay = $AirPlay.Replace('<string>Kodi,1</string>',
    '<string>AppleTV3,2</string>')
$AirPlay = $AirPlay.Replace('<string>AppleTV14,1</string>',
    '<string>AppleTV3,2</string>')
if (-not $AirPlay.Contains('<integer>%u</integer>') -and
    -not $AirPlay.Contains('<integer>1518337766</integer>')) {
    throw 'AirPlay server-info capability was not applied.'
}
if (-not $AirPlay.Contains('GLOBAL_MODEL') -and
    -not $AirPlay.Contains('<string>AppleTV3,2</string>')) {
    throw 'AirPlay media-cast server-info model was not applied.'
}
if (-not $AirPlay.Contains(
        '*response = http_response_init("HTTP/1.1", 200, "OK");')) {
    throw 'AirPlay media HTTP response protocol was not applied.'
}

$NewLine = if ($AirPlay.Contains("`r`n")) { "`r`n" } else { "`n" }

# Video-app casting connects to the dedicated AirPlay HTTP port, not the RAOP
# port used by screen mirroring. Upstream only routes /fp-setup on RAOP, so the
# media port previously returned an empty 200 response and iOS disconnected
# immediately after pairing.
$AirPlayHandlers = [IO.File]::ReadAllText($AirPlayHandlersFile, $Encoding)
# These headers are compiled as C. Upstream's C++-style `auto` declarations
# become 32-bit integers under MSVC C rules and truncate callback pointers on
# x64, which can make an otherwise valid video route disappear or hang.
$AirPlayHandlers = [regex]::Replace($AirPlayHandlers,
    '(?m)^\s*auto video_play = conn->airplay->callbacks\.video_play;\r?\n', '')
$AirPlayHandlers = $AirPlayHandlers.Replace('if (video_play != NULL) {',
    'if (conn->airplay->callbacks.video_play != NULL) {')
$AirPlayHandlers = [regex]::Replace($AirPlayHandlers,
    '(?m)^\s*auto video_get_play_info = conn->airplay->callbacks\.video_get_play_info;\r?\n', '')
$AirPlayHandlers = $AirPlayHandlers.Replace('if (video_get_play_info != NULL) {',
    'if (conn->airplay->callbacks.video_get_play_info != NULL) {')
if ($AirPlayHandlers.Contains('auto video_play =') -or
    $AirPlayHandlers.Contains('auto video_get_play_info =')) {
    throw 'AirPlay media callback pointer fix was not applied.'
}
$ServerInfoFeaturesMarker = 'IPHONE_MIRROR_SERVER_INFO_FEATURES'
if (-not $AirPlayHandlers.Contains($ServerInfoFeaturesMarker)) {
    $HandlerNewLine = if ($AirPlayHandlers.Contains("`r`n")) { "`r`n" } else { "`n" }
    $Needle = "`tsprintf(data, SERVER_INFO, deviceid, GLOBAL_FEATURES_1);"
    $Replacement = @'
	/* IPHONE_MIRROR_SERVER_INFO_FEATURES */
	const char *iphone_mirror_mode = getenv("IPHONE_MIRROR_AIRPLAY_MODE");
	unsigned int iphone_mirror_features = iphone_mirror_mode != NULL &&
		(!strcmp(iphone_mirror_mode, "media") ||
		 !strcmp(iphone_mirror_mode, "combined")) ?
		0x5A7FFEF7U : GLOBAL_FEATURES_1;
	sprintf(data, SERVER_INFO, deviceid, iphone_mirror_features);
'@ -replace "`r?`n", $HandlerNewLine
    if ([regex]::Matches($AirPlayHandlers, [regex]::Escape($Needle)).Count -ne 1) {
        throw 'AirPlay server-info feature insertion point changed.'
    }
    $AirPlayHandlers = $AirPlayHandlers.Replace($Needle,
        $Replacement.TrimEnd("`r", "`n"))
}
if ($AirPlay.Contains('<integer>%u</integer>') -and
    -not $AirPlayHandlers.Contains($ServerInfoFeaturesMarker)) {
    throw 'AirPlay server-info does not use the runtime receiver feature mask.'
}
$ServerInfoIdentityMarker = 'IPHONE_MIRROR_SERVER_INFO_IDENTITY'
if (-not $AirPlayHandlers.Contains($ServerInfoIdentityMarker)) {
    $HandlerNewLine = if ($AirPlayHandlers.Contains("`r`n")) { "`r`n" } else { "`n" }
    $Needle = "`tsprintf(data, SERVER_INFO, deviceid, iphone_mirror_features);"
    $Replacement = @'
	/* IPHONE_MIRROR_SERVER_INFO_IDENTITY */
	const char *receiver_device_id =
		getenv("IPHONE_MIRROR_AIRPLAY_DEVICE_ID");
	const char *server_device_id = receiver_device_id != NULL &&
		strlen(receiver_device_id) == 17 ? receiver_device_id : deviceid;
	sprintf(data, SERVER_INFO, server_device_id, iphone_mirror_features);
'@ -replace "`r?`n", $HandlerNewLine
    if ([regex]::Matches($AirPlayHandlers, [regex]::Escape($Needle)).Count -ne 1) {
        throw 'AirPlay server-info identity insertion point changed.'
    }
    $AirPlayHandlers = $AirPlayHandlers.Replace($Needle,
        $Replacement.TrimEnd("`r", "`n"))
}
if (-not $AirPlayHandlers.Contains($ServerInfoIdentityMarker)) {
    throw 'AirPlay server-info does not use the runtime receiver identity.'
}
[IO.File]::WriteAllText($AirPlayHandlersFile, $AirPlayHandlers, $Encoding)
$FairPlayMarker = 'IPHONE_MIRROR_MEDIA_CAST_FAIRPLAY'
if (-not $AirPlayHandlers.Contains($FairPlayMarker)) {
    $HandlerNewLine = if ($AirPlayHandlers.Contains(
            "static void`r`nairplay_handler_serverinfo")) { "`r`n" } else { "`n" }
    $Needle = 'static void' + $HandlerNewLine + 'airplay_handler_serverinfo'
    $Handler = @'
/* IPHONE_MIRROR_MEDIA_CAST_FAIRPLAY */
static void
airplay_handler_fpsetup(airplay_conn_t *conn,
	http_request_t *request, http_response_t *response,
	char **response_data, int *response_datalen)
{
	const unsigned char *data;
	int datalen;

	data = (const unsigned char *)http_request_get_data(request, &datalen);
	if (datalen == 16 && data[4] == 3 && data[14] < 4) {
		*response_data = malloc(142);
		if (*response_data != NULL &&
			!fairplay_setup(conn->fairplay, data,
				(unsigned char *)*response_data)) {
			http_response_add_header(response, "Content-Type",
				"application/octet-stream");
			*response_datalen = 142;
			return;
		}
	}
	else if (datalen == 164 && data[4] == 3) {
		*response_data = malloc(32);
		if (*response_data != NULL &&
			!fairplay_handshake(conn->fairplay, data,
				(unsigned char *)*response_data)) {
			http_response_add_header(response, "Content-Type",
				"application/octet-stream");
			*response_datalen = 32;
			return;
		}
	}

	free(*response_data);
	*response_data = NULL;
	*response_datalen = 0;
	logger_log(conn->airplay->logger, LOGGER_ERR,
		"IPHONE_MIRROR_MEDIA_CAST_FAIRPLAY invalid request length=%d", datalen);
}

static void
airplay_handler_serverinfo
'@ -replace "`r?`n", $HandlerNewLine
    if ([regex]::Matches($AirPlayHandlers, [regex]::Escape($Needle)).Count -ne 1) {
        throw 'AirPlay FairPlay handler insertion point changed.'
    }
    $AirPlayHandlers = $AirPlayHandlers.Replace($Needle,
        $Handler.TrimEnd("`r", "`n"))
    [IO.File]::WriteAllText($AirPlayHandlersFile, $AirPlayHandlers, $Encoding)
}
$LegacyBlock = @'
	if (url != NULL && (
		!strcmp(url, "/play") || !strcmp(url, "/playback-info") ||
		!strncmp(url, "/rate", strlen("/rate")) ||
		!strncmp(url, "/setProperty", strlen("/setProperty")) ||
		!strncmp(url, "/photo", strlen("/photo")) ||
		!strncmp(url, "/slideshow", strlen("/slideshow")) ||
		!strncmp(url, "/scrub", strlen("/scrub")) ||
		!strcmp(url, "/stop") || !strcmp(url, "/reverse"))) {
		logger_log(conn->airplay->logger, LOGGER_INFO,
			"IPHONE_MIRROR_MEDIA_CAST_BLOCKED method=%s url=%s", method, url);
		http_response_destroy(*response);
		*response = http_response_init("HTTP/1.1", 403, "Forbidden");
		http_response_add_header(*response, "Connection", "close");
		http_response_set_disconnect(*response, 1);
		http_response_finish(*response, NULL, 0);
		return;
	}
'@ -replace "`r?`n", $NewLine
$ConditionalBlock = @'
	/* IPHONE_MIRROR_MEDIA_CAST_MODE: the dedicated media receiver accepts
	 * URL-video controls; the screen-mirroring receiver still rejects them. */
	const char *iphone_mirror_mode = getenv("IPHONE_MIRROR_AIRPLAY_MODE");
	int iphone_mirror_media_cast = iphone_mirror_mode != NULL &&
		(!strcmp(iphone_mirror_mode, "media") ||
		 !strcmp(iphone_mirror_mode, "combined"));
	if (!iphone_mirror_media_cast && url != NULL && (
		!strcmp(url, "/play") || !strcmp(url, "/playback-info") ||
		!strncmp(url, "/rate", strlen("/rate")) ||
		!strncmp(url, "/setProperty", strlen("/setProperty")) ||
		!strncmp(url, "/photo", strlen("/photo")) ||
		!strncmp(url, "/slideshow", strlen("/slideshow")) ||
		!strncmp(url, "/scrub", strlen("/scrub")) ||
		!strcmp(url, "/stop") || !strcmp(url, "/reverse"))) {
		logger_log(conn->airplay->logger, LOGGER_INFO,
			"IPHONE_MIRROR_MEDIA_CAST_BLOCKED method=%s url=%s", method, url);
		http_response_destroy(*response);
		*response = http_response_init("HTTP/1.1", 403, "Forbidden");
		http_response_add_header(*response, "Connection", "close");
		http_response_set_disconnect(*response, 1);
		http_response_finish(*response, NULL, 0);
		return;
	}
'@ -replace "`r?`n", $NewLine
if ($AirPlay.Contains($LegacyBlock)) {
    $AirPlay = $AirPlay.Replace($LegacyBlock, $ConditionalBlock)
}
$LegacyModeCheck = @'
	int iphone_mirror_media_cast = iphone_mirror_mode != NULL &&
		!strcmp(iphone_mirror_mode, "media");
'@ -replace "`r?`n", $NewLine
$CombinedModeCheck = @'
	int iphone_mirror_media_cast = iphone_mirror_mode != NULL &&
		(!strcmp(iphone_mirror_mode, "media") ||
		 !strcmp(iphone_mirror_mode, "combined"));
'@ -replace "`r?`n", $NewLine
if ($AirPlay.Contains($LegacyModeCheck)) {
    $AirPlay = $AirPlay.Replace($LegacyModeCheck, $CombinedModeCheck)
}

$FairPlayRouteMarker = 'IPHONE_MIRROR_MEDIA_CAST_FAIRPLAY_ROUTE'
if (-not $AirPlay.Contains($FairPlayRouteMarker)) {
    $Needle = @(
        "`telse if (!strcmp(method, `"POST`") && !strcmp(url, `"/pair-verify`")) {",
        "`t`thandler = &airplay_handler_pairverify;",
        "`t}"
    ) -join $NewLine
    $Replacement = @'
	else if (!strcmp(method, "POST") && !strcmp(url, "/pair-verify")) {
		handler = &airplay_handler_pairverify;
	}
	else if (iphone_mirror_media_cast && !strcmp(method, "POST") &&
		!strcmp(url, "/fp-setup")) {
		/* IPHONE_MIRROR_MEDIA_CAST_FAIRPLAY_ROUTE */
		handler = &airplay_handler_fpsetup;
	}
'@ -replace "`r?`n", $NewLine
    if ([regex]::Matches($AirPlay, [regex]::Escape($Needle)).Count -ne 1) {
        throw 'AirPlay FairPlay route insertion point changed.'
    }
    $AirPlay = $AirPlay.Replace($Needle, $Replacement.TrimEnd("`r", "`n"))
}
if (-not $AirPlay.Contains('IPHONE_MIRROR_MEDIA_CAST_MODE')) {
    $Needle = "`tlogger_log(conn->airplay->logger, LOGGER_DEBUG, `"[AirPlay] Handling request %s with URL %s`", method, url);"
    $Replacement = $ConditionalBlock + $NewLine + $Needle
    if ([regex]::Matches($AirPlay, [regex]::Escape($Needle)).Count -ne 1) {
        throw 'AirPlay request router changed.'
    }
    $AirPlay = $AirPlay.Replace($Needle, $Replacement)
}

# Some upstream tags do not contain the legacy blocking stanza above. Ensure
# the mode flag used by the FairPlay and playback-control routes is declared
# independently of which historical patch shape was found.
if (-not $AirPlay.Contains(
        'const char *iphone_mirror_mode = getenv("IPHONE_MIRROR_AIRPLAY_MODE");')) {
    $Needle = "`tlogger_log(conn->airplay->logger, LOGGER_DEBUG, `"[AirPlay] Handling request %s with URL %s`", method, url);"
    $ModeDeclaration = @'
	/* IPHONE_MIRROR_MEDIA_CAST_MODE */
	const char *iphone_mirror_mode = getenv("IPHONE_MIRROR_AIRPLAY_MODE");
	int iphone_mirror_media_cast = iphone_mirror_mode != NULL &&
		(!strcmp(iphone_mirror_mode, "media") ||
		 !strcmp(iphone_mirror_mode, "combined"));
'@ -replace "`r?`n", $NewLine
    if ([regex]::Matches($AirPlay, [regex]::Escape($Needle)).Count -ne 1) {
        throw 'AirPlay media-mode declaration insertion point changed.'
    }
    $AirPlay = $AirPlay.Replace($Needle,
        $ModeDeclaration.TrimEnd("`r", "`n") + $NewLine + $Needle)
}

# Older revisions of this patch could leave the mode declaration in place
# without the non-media request guard. Repair that partial state as well as
# applying cleanly to an untouched upstream checkout.
if (-not $AirPlay.Contains('IPHONE_MIRROR_MEDIA_CAST_BLOCKED')) {
    $Needle = "`tlogger_log(conn->airplay->logger, LOGGER_DEBUG, `"[AirPlay] Handling request %s with URL %s`", method, url);"
    $BlockedRoutes = @'
	if (!iphone_mirror_media_cast && url != NULL && (
		!strcmp(url, "/play") || !strcmp(url, "/playback-info") ||
		!strncmp(url, "/rate", strlen("/rate")) ||
		!strncmp(url, "/setProperty", strlen("/setProperty")) ||
		!strncmp(url, "/photo", strlen("/photo")) ||
		!strncmp(url, "/slideshow", strlen("/slideshow")) ||
		!strncmp(url, "/scrub", strlen("/scrub")) ||
		!strcmp(url, "/stop") || !strcmp(url, "/reverse"))) {
		logger_log(conn->airplay->logger, LOGGER_INFO,
			"IPHONE_MIRROR_MEDIA_CAST_BLOCKED method=%s url=%s", method, url);
		http_response_destroy(*response);
		*response = http_response_init("HTTP/1.1", 403, "Forbidden");
		http_response_add_header(*response, "Connection", "close");
		http_response_set_disconnect(*response, 1);
		http_response_finish(*response, NULL, 0);
		return;
	}
'@ -replace "`r?`n", $NewLine
    if ([regex]::Matches($AirPlay, [regex]::Escape($Needle)).Count -ne 1) {
        throw 'AirPlay media-cast request guard insertion point changed.'
    }
    $AirPlay = $AirPlay.Replace($Needle,
        $BlockedRoutes.TrimEnd("`r", "`n") + $NewLine + $Needle)
}

$LegacyRateRoute = @'
	// POST /rate?value=1.000000 HTTP/1.1
	else if (!strcmp(method, "POST") && !strncmp(url, "/rate", strlen("/rate"))) {
//		handler = &raop_handler_pairsetup;
	}
'@ -replace "`r?`n", $NewLine
$MediaRateRoute = @'
	// POST /rate?value=1.000000 HTTP/1.1
	else if (!strcmp(method, "POST") &&
		!strncmp(url, "/rate", strlen("/rate"))) {
		/* IPHONE_MIRROR_MEDIA_CAST_RATE_CONTROL */
		const char *value = strstr(url, "value=");
		if (conn->airplay->callbacks.video_play != NULL)
			conn->airplay->callbacks.video_play(conn->airplay->callbacks.cls,
				value != NULL && atof(value + 6) == 0 ?
				"iphonemirror://pause" : "iphonemirror://resume", 0, 0);
	}
'@ -replace "`r?`n", $NewLine
if ($AirPlay.Contains($LegacyRateRoute)) {
    $AirPlay = $AirPlay.Replace($LegacyRateRoute, $MediaRateRoute)
}

$StopMarker = 'IPHONE_MIRROR_MEDIA_CAST_STOP'
$LegacyStopCallback = @'
		auto video_play = conn->airplay->callbacks.video_play;
		if (video_play != NULL)
			video_play(conn->airplay->callbacks.cls, NULL, 0, 0);
'@ -replace "`r?`n", $NewLine
$DirectStopCallback = @'
		if (conn->airplay->callbacks.video_play != NULL)
			conn->airplay->callbacks.video_play(
				conn->airplay->callbacks.cls, NULL, 0, 0);
'@ -replace "`r?`n", $NewLine
$AirPlay = $AirPlay.Replace($LegacyStopCallback, $DirectStopCallback)
if (-not $AirPlay.Contains($StopMarker)) {
    $Needle = @(
        "`telse if (!strcmp(method, `"GET`") && !strcmp(url, `"/playback-info`")) {",
        "`t`thandler = &airplay_handler_playbackinfo;",
        "`t}",
        "`telse if (!strcmp(method, `"POST`") && !strcmp(url, `"/reverse`")) {"
    ) -join $NewLine
    $Replacement = @'
	else if (!strcmp(method, "GET") && !strcmp(url, "/playback-info")) {
		handler = &airplay_handler_playbackinfo;
	}
	else if (!strcmp(method, "POST") && !strcmp(url, "/stop")) {
		/* IPHONE_MIRROR_MEDIA_CAST_STOP */
		if (conn->airplay->callbacks.video_play != NULL)
			conn->airplay->callbacks.video_play(
				conn->airplay->callbacks.cls, NULL, 0, 0);
	}
	else if (!strncmp(url, "/scrub", 6)) {
		/* IPHONE_MIRROR_MEDIA_CAST_SEEK_CONTROL */
		const char *position = strstr(url, "position=");
		if (position != NULL && conn->airplay->callbacks.video_play != NULL)
			conn->airplay->callbacks.video_play(conn->airplay->callbacks.cls,
				"iphonemirror://seek", 0, atof(position + 9));
	}
	else if (!strcmp(method, "POST") && !strcmp(url, "/reverse")) {
'@ -replace "`r?`n", $NewLine
    if ([regex]::Matches($AirPlay, [regex]::Escape($Needle)).Count -ne 1) {
        throw 'AirPlay stop-control insertion point changed.'
    }
    $AirPlay = $AirPlay.Replace($Needle, $Replacement.TrimEnd("`r", "`n"))
}
$AirPlay = [regex]::Replace($AirPlay,
    'else if \(iphone_mirror_media_cast && !strcmp\(method, "POST"\) &&\r?\n\s*!strncmp\(url, "/rate", strlen\("/rate"\)\)\) \{',
    'else if (!strcmp(method, "POST") &&' + $NewLine +
    "`t`t!strncmp(url, `"/rate`", strlen(`"/rate`"))) {")
$AirPlay = $AirPlay.Replace(
    'else if (iphone_mirror_media_cast && !strncmp(url, "/scrub", 6)) {',
    'else if (!strncmp(url, "/scrub", 6)) {')
[IO.File]::WriteAllText($AirPlayFile, $AirPlay, $Encoding)

$RaopHeader = [IO.File]::ReadAllText($RaopHeaderFile, $Encoding)
$RaopMediaCallbacksMarker = 'IPHONE_MIRROR_RAOP_MEDIA_CALLBACKS'
if (-not $RaopHeader.Contains($RaopMediaCallbacksMarker)) {
    $HeaderNewLine = if ($RaopHeader.Contains("`r`n")) { "`r`n" } else { "`n" }
    $Needle = "    void  (*video_process)(void *cls, h264_decode_struct *data, const char* remoteName, const char* remoteDeviceId);"
    $Replacement = @'
    void  (*video_process)(void *cls, h264_decode_struct *data, const char* remoteName, const char* remoteDeviceId);

	/* IPHONE_MIRROR_RAOP_MEDIA_CALLBACKS: URL-video controls may arrive on
	 * the RAOP service selected by third-party video apps. */
	void (*video_play)(void* cls, char* url, double volume, double start_pos);
	void (*video_get_play_info)(void* cls, double* duration, double* position, double* rate);
'@ -replace "`r?`n", $HeaderNewLine
    if ([regex]::Matches($RaopHeader, [regex]::Escape($Needle)).Count -ne 1) {
        throw 'AirPlay RAOP media callback insertion point changed.'
    }
    $RaopHeader = $RaopHeader.Replace($Needle, $Replacement.TrimEnd("`r", "`n"))
    [IO.File]::WriteAllText($RaopHeaderFile, $RaopHeader, $Encoding)
}

$RaopEncoding = [Text.Encoding]::Unicode
$Raop = [IO.File]::ReadAllText($RaopFile, $RaopEncoding)
$Raop = $Raop.Replace('0x1A7FFEC0ULL', '0x5A7FFEE6ULL')
$Raop = $Raop.Replace('0x5A7FFEC0ULL', '0x5A7FFEE6ULL')
$Raop = $Raop.Replace('"AppleTV14,1"', '"AppleTV3,2"')
$Raop = $Raop.Replace('"845.5.1"', '"220.68"')
$NewLine = if ($Raop.Contains("`r`n")) { "`r`n" } else { "`n" }
$InfoHexHelper = @'
/* IPHONE_MIRROR_INFO_HEX_NIBBLE */
static int
iphone_mirror_info_hex_nibble(char value)
{
	if (value >= '0' && value <= '9') return value - '0';
	if (value >= 'a' && value <= 'f') return value - 'a' + 10;
	return -1;
}

'@ -replace "`r?`n", $NewLine
while ([regex]::Matches($Raop,
        [regex]::Escape($InfoHexHelper)).Count -gt 1) {
    $Index = $Raop.IndexOf($InfoHexHelper, [StringComparison]::Ordinal)
    $Raop = $Raop.Remove($Index, $InfoHexHelper.Length)
}
$LegacyInfoBlock = @'
	/* IPHONE_MIRROR_MIRRORING_ONLY_FEATURES: media bits are intentionally absent. */
	if (capability_root)
		plist_dict_set_item(capability_root, "features",
			plist_new_uint(0x5A7FFEE6ULL));
'@ -replace "`r?`n", $NewLine
$RuntimeInfoBlock = @'
	/* IPHONE_MIRROR_RUNTIME_FEATURES */
	const char *iphone_mirror_mode = getenv("IPHONE_MIRROR_AIRPLAY_MODE");
	unsigned long long iphone_mirror_features = iphone_mirror_mode != NULL &&
		(!strcmp(iphone_mirror_mode, "media") ||
		 !strcmp(iphone_mirror_mode, "combined")) ?
		0x5A7FFEF7ULL : 0x5A7FFEE6ULL;
	if (capability_root)
		plist_dict_set_item(capability_root, "features",
			plist_new_uint(iphone_mirror_features));
'@ -replace "`r?`n", $NewLine
if ($Raop.Contains($LegacyInfoBlock)) {
    $Raop = $Raop.Replace($LegacyInfoBlock, $RuntimeInfoBlock)
}
$InfoMarker = 'IPHONE_MIRROR_RUNTIME_FEATURES'
if (-not $Raop.Contains($InfoMarker)) {
    $Needle = "`tif (capability_root && receiver_name && *receiver_name)"
    $Replacement = $RuntimeInfoBlock + $NewLine +
        "`tif (capability_root && receiver_name && *receiver_name)"
    $Matches = [regex]::Matches($Raop, [regex]::Escape($Needle)).Count
    if ($Matches -ne 1) {
        throw "AirPlay /info capability response changed (expected one insertion point, found $Matches)."
    }
    $Raop = $Raop.Replace($Needle, $Replacement.TrimEnd("`r", "`n"))
}
if (-not $Raop.Contains('0x5A7FFEE6ULL') -or
    -not $Raop.Contains('0x5A7FFEF7ULL') -or
    $Raop.Contains('0x5A7FFEC0ULL')) {
    throw 'AirPlay /info feature declaration was not applied consistently.'
}
$InfoIdentityMarker = 'IPHONE_MIRROR_RUNTIME_IDENTITY'
if (-not $Raop.Contains($InfoIdentityMarker)) {
    $Needle = "`tif (capability_root && receiver_name && *receiver_name)"
    $Replacement = @'
	/* IPHONE_MIRROR_RUNTIME_IDENTITY */
	const char *receiver_device_id =
		getenv("IPHONE_MIRROR_AIRPLAY_DEVICE_ID");
	if (capability_root && receiver_device_id && *receiver_device_id) {
		plist_dict_set_item(capability_root, "deviceID",
			plist_new_string(receiver_device_id));
		plist_dict_set_item(capability_root, "macAddress",
			plist_new_string(receiver_device_id));
	}
	if (capability_root) {
		plist_dict_set_item(capability_root, "model",
			plist_new_string("AppleTV3,2"));
		plist_dict_set_item(capability_root, "sourceVersion",
			plist_new_string("220.68"));
	}
	if (capability_root && receiver_name && *receiver_name)
'@ -replace "`r?`n", $NewLine
    if ([regex]::Matches($Raop, [regex]::Escape($Needle)).Count -ne 1) {
        throw 'AirPlay /info runtime identity insertion point changed.'
    }
    $Raop = $Raop.Replace($Needle, $Replacement.TrimEnd("`r", "`n"))
}

$InfoPairingMarker = 'IPHONE_MIRROR_RUNTIME_PAIRING_IDENTITY'
if (-not $Raop.Contains($InfoPairingMarker)) {
    if (-not $Raop.Contains('IPHONE_MIRROR_INFO_HEX_NIBBLE')) {
        $HelperNeedle = 'static void' + $NewLine + 'raop_handler_info'
        $Helper = @'
/* IPHONE_MIRROR_INFO_HEX_NIBBLE */
static int
iphone_mirror_info_hex_nibble(char value)
{
	if (value >= '0' && value <= '9') return value - '0';
	if (value >= 'a' && value <= 'f') return value - 'a' + 10;
	return -1;
}

static void
raop_handler_info
'@ -replace "`r?`n", $NewLine
        if ([regex]::Matches($Raop, [regex]::Escape($HelperNeedle)).Count -ne 1) {
            throw 'AirPlay /info public-key helper insertion point changed.'
        }
        $Raop = $Raop.Replace($HelperNeedle, $Helper.TrimEnd("`r", "`n"))
    }
    $Needle = "`tif (capability_root && receiver_name && *receiver_name)"
    $Replacement = @'
	/* IPHONE_MIRROR_RUNTIME_PAIRING_IDENTITY */
	const char *receiver_public_key =
		getenv("IPHONE_MIRROR_AIRPLAY_PUBLIC_KEY");
	if (capability_root && receiver_public_key != NULL &&
		strlen(receiver_public_key) == 64) {
		unsigned char public_key[32];
		int valid = 1;
		for (int index = 0; valid && index < 32; ++index) {
			int high = iphone_mirror_info_hex_nibble(receiver_public_key[index * 2]);
			int low = iphone_mirror_info_hex_nibble(receiver_public_key[index * 2 + 1]);
			if (high < 0 || low < 0) valid = 0;
			else public_key[index] = (unsigned char)((high << 4) | low);
		}
		if (valid) plist_dict_set_item(capability_root, "pk",
			plist_new_data((const char *)public_key, sizeof(public_key)));
	}
	if (capability_root) plist_dict_set_item(capability_root, "pi",
		plist_new_string("2e388006-13ba-4041-9a67-25dd4a43d536"));
	if (capability_root && receiver_name && *receiver_name)
'@ -replace "`r?`n", $NewLine
    if ([regex]::Matches($Raop, [regex]::Escape($Needle)).Count -ne 1) {
        throw 'AirPlay /info pairing identity insertion point changed.'
    }
    $Raop = $Raop.Replace($Needle, $Replacement.TrimEnd("`r", "`n"))
}

$RaopMediaHandlersMarker = 'IPHONE_MIRROR_RAOP_MEDIA_HANDLERS'
if (-not $Raop.Contains($RaopMediaHandlersMarker)) {
    $RaopNewLine = if ($Raop.Contains("`r`n")) { "`r`n" } else { "`n" }
    $Needle = 'static void' + $RaopNewLine + 'raop_handler_options'
    $Replacement = @'
/* IPHONE_MIRROR_RAOP_MEDIA_HANDLERS */
static void
iphone_mirror_raop_handler_play(raop_conn_t *conn,
	http_request_t *request, http_response_t *response,
	char **response_data, int *response_datalen)
{
	const char *data;
	int datalen = 0;
	plist_t root = NULL;
	char *url = NULL;
	double start_seconds = 0;
	double start_fraction = 0;
	double volume = 1;

	data = http_request_get_data(request, &datalen);
	if (data == NULL || datalen <= 0) return;
	plist_from_bin(data, datalen, &root);
	if (root == NULL) return;
	plist_get_string_val(plist_dict_get_item(root, "Content-Location"), &url);
	plist_get_real_val(plist_dict_get_item(root, "Start-Position-Seconds"),
		&start_seconds);
	plist_get_real_val(plist_dict_get_item(root, "Start-Position"),
		&start_fraction);
	plist_get_real_val(plist_dict_get_item(root, "volume"), &volume);
	if (url != NULL && conn->raop->callbacks.video_play != NULL)
		conn->raop->callbacks.video_play(conn->raop->callbacks.cls, url,
			volume, start_seconds > 0 ? start_seconds : start_fraction);
	free(url);
	plist_free(root);
}

static void
iphone_mirror_raop_handler_playbackinfo(raop_conn_t *conn,
	http_request_t *request, http_response_t *response,
	char **response_data, int *response_datalen)
{
	double duration = 0;
	double position = 0;
	double rate = 0;
	if (conn->raop->callbacks.video_get_play_info != NULL)
		conn->raop->callbacks.video_get_play_info(conn->raop->callbacks.cls,
			&duration, &position, &rate);
	*response_data = malloc(1024);
	if (*response_data == NULL) return;
	*response_datalen = snprintf(*response_data, 1024,
		"<?xml version=\"1.0\" encoding=\"UTF-8\"?>\r\n"
		"<plist version=\"1.0\"><dict>"
		"<key>duration</key><real>%f</real>"
		"<key>position</key><real>%f</real>"
		"<key>rate</key><real>%f</real>"
		"<key>readyToPlay</key><true/>"
		"</dict></plist>\r\n", duration, position, rate);
	http_response_add_header(response, "Content-Type", "text/x-apple-plist+xml");
}

static void
raop_handler_options
'@ -replace "`r?`n", $RaopNewLine
    if ([regex]::Matches($Raop, [regex]::Escape($Needle)).Count -ne 1) {
        throw 'AirPlay RAOP media handler insertion point changed.'
    }
    $Raop = $Raop.Replace($Needle, $Replacement.TrimEnd("`r", "`n"))
}

$MirrorStartedMarker = 'IPHONE_MIRROR_MARK_MIRROR_STARTED'
if (-not $Raop.Contains($MirrorStartedMarker)) {
    $NewLine = if ($Raop.Contains("`r`n")) { "`r`n" } else { "`n" }
    $Needle = "`t`t`traop_rtp_start_mirror(conn->raop_rtp_mirror, use_udp, remote_tport, &tport, &dport);"
    $Replacement = $Needle + $NewLine +
        "`t`t`tconn->mirror_started = 1; /* $MirrorStartedMarker */"
    if ([regex]::Matches($Raop, [regex]::Escape($Needle)).Count -ne 1) {
        throw 'AirPlay mirror stream start point changed.'
    }
    $Raop = $Raop.Replace($Needle, $Replacement)

    $Needle = "`t`traop_rtp_mirror_stop(conn->raop_rtp_mirror);"
    $Replacement = $Needle + $NewLine + "`t`tconn->mirror_started = 0;"
    if ([regex]::Matches($Raop, [regex]::Escape($Needle)).Count -ne 1) {
        throw 'AirPlay mirror stream stop point changed.'
    }
    $Raop = $Raop.Replace($Needle, $Replacement)
}
[IO.File]::WriteAllText($RaopFile, $Raop, $RaopEncoding)

$RaopRouter = [IO.File]::ReadAllText($RaopRouterFile, $Encoding)
$RaopRouter = $RaopRouter.Replace('AirTunes/845.5.1', 'AirTunes/220.68')
$RaopRouterMarker = 'IPHONE_MIRROR_RAOP_MEDIA_CAST_BLOCKED'
if (-not $RaopRouter.Contains($RaopRouterMarker)) {
    $NewLine = if ($RaopRouter.Contains("`r`n")) { "`r`n" } else { "`n" }
    $Needle = @(
        "`tpairing_session_t *pairing;",
        '',
        "`tunsigned char *local;"
    ) -join $NewLine
    $Replacement = @'
	pairing_session_t *pairing;
	int mirror_session_requested;
	int mirror_started;

	unsigned char *local;
'@ -replace "`r?`n", $NewLine
    if ([regex]::Matches($RaopRouter, [regex]::Escape($Needle)).Count -ne 1) {
        throw 'AirPlay RAOP connection state declaration changed.'
    }
    $RaopRouter = $RaopRouter.Replace($Needle, $Replacement.TrimEnd("`r", "`n"))

    $Needle = '#include "raop_handlers.h"'
    $Replacement = @'
#include "raop_handlers.h"

/* IPHONE_MIRROR_RAOP_MEDIA_CAST_BLOCKED: allow SETUP only for a real
 * screen-mirroring session. Media-only AirPlay audio uses the same RAOP
 * transport but never requests stream type 110. */
static int
iphone_mirror_reject_media_setup(raop_conn_t *conn, http_request_t *request)
{
	const char *data;
	int datalen = 0;
	int reject = 0;
	plist_t root = NULL;
	plist_t eiv;
	plist_t streams;
	plist_t stream;
	uint64_t stream_type = 0;

	data = http_request_get_data(request, &datalen);
	if (data == NULL || datalen <= 0) return 0;
	plist_from_bin(data, datalen, &root);
	if (root == NULL) return 0;

	eiv = plist_dict_get_item(root, "eiv");
	if (eiv != NULL) {
		uint8_t is_mirroring = 0;
		plist_t flag = plist_dict_get_item(root, "isScreenMirroringSession");
		/* IPHONE_MIRROR_MIRROR_FLAG_OPTIONAL: older and some newer senders
		 * omit this advisory flag and identify mirroring in SETUP type 110. */
		if (flag != NULL) {
			plist_get_bool_val(flag, &is_mirroring);
			if (is_mirroring) conn->mirror_session_requested = 1;
			else reject = 1;
		}
	}
	else {
		streams = plist_dict_get_item(root, "streams");
		stream = streams ? plist_array_get_item(streams, 0) : NULL;
		if (stream != NULL) {
			plist_t type = plist_dict_get_item(stream, "type");
			if (type != NULL) plist_get_uint_val(type, &stream_type);
		}
		if (stream_type == 110) conn->mirror_session_requested = 1;
		else if (stream_type == 96 &&
			(!conn->mirror_session_requested || !conn->mirror_started)) reject = 1;
	}

	plist_free(root);
	return reject;
}
'@ -replace "`r?`n", $NewLine
    if ([regex]::Matches($RaopRouter, [regex]::Escape($Needle)).Count -ne 1) {
        throw 'AirPlay RAOP media filter insertion point changed.'
    }
    $RaopRouter = $RaopRouter.Replace($Needle, $Replacement.TrimEnd("`r", "`n"))

    $Needle = @(
        "`tif (!method || !cseq) {",
        "`t`treturn;",
        "`t}",
        '',
        "`t*response = http_response_init(`"RTSP/1.0`", 200, `"OK`");"
    ) -join $NewLine
    $Replacement = @'
	if (!method || !cseq) {
		return;
	}
	const char *iphone_mirror_mode = getenv("IPHONE_MIRROR_AIRPLAY_MODE");
	int iphone_mirror_media_cast = iphone_mirror_mode != NULL &&
		(!strcmp(iphone_mirror_mode, "media") ||
		 !strcmp(iphone_mirror_mode, "combined"));
	if (!iphone_mirror_media_cast && (!strcmp(method, "ANNOUNCE") ||
		(!strcmp(method, "SETUP") &&
		 iphone_mirror_reject_media_setup(conn, request)))) {
		logger_log(conn->raop->logger, LOGGER_INFO,
			"IPHONE_MIRROR_RAOP_MEDIA_CAST_BLOCKED method=%s url=%s",
			method, url ? url : "");
		*response = http_response_init("RTSP/1.0", 403, "Forbidden");
		http_response_add_header(*response, "CSeq", cseq);
		http_response_add_header(*response, "Server", "AirTunes/220.68");
		http_response_set_disconnect(*response, 1);
		http_response_finish(*response, NULL, 0);
		return;
	}

	*response = http_response_init("RTSP/1.0", 200, "OK");
'@ -replace "`r?`n", $NewLine
    if ([regex]::Matches($RaopRouter, [regex]::Escape($Needle)).Count -ne 1) {
        throw 'AirPlay RAOP request router changed.'
    }
    $RaopRouter = $RaopRouter.Replace($Needle, $Replacement.TrimEnd("`r", "`n"))
    [IO.File]::WriteAllText($RaopRouterFile, $RaopRouter, $Encoding)
}

$LegacyMediaFilter = @'
	if (!strcmp(method, "ANNOUNCE") ||
		(!strcmp(method, "SETUP") &&
		 iphone_mirror_reject_media_setup(conn, request))) {
'@ -replace "`r?`n", $NewLine
$ModeAwareMediaFilter = @'
	const char *iphone_mirror_mode = getenv("IPHONE_MIRROR_AIRPLAY_MODE");
	int iphone_mirror_media_cast = iphone_mirror_mode != NULL &&
		(!strcmp(iphone_mirror_mode, "media") ||
		 !strcmp(iphone_mirror_mode, "combined"));
	if (!iphone_mirror_media_cast && (!strcmp(method, "ANNOUNCE") ||
		(!strcmp(method, "SETUP") &&
		 iphone_mirror_reject_media_setup(conn, request)))) {
'@ -replace "`r?`n", $NewLine
if ($RaopRouter.Contains($LegacyMediaFilter)) {
    $RaopRouter = $RaopRouter.Replace($LegacyMediaFilter, $ModeAwareMediaFilter)
    [IO.File]::WriteAllText($RaopRouterFile, $RaopRouter, $Encoding)
}
if (-not $RaopRouter.Contains('int iphone_mirror_media_cast =')) {
    throw 'AirPlay RAOP media-mode policy was not applied.'
}

$RouterNewLine = if ($RaopRouter.Contains("`r`n")) { "`r`n" } else { "`n" }
$LegacyRequestPreamble = @'
	method = http_request_get_method(request);
	url = http_request_get_url(request);
	cseq = http_request_get_header(request, "CSeq");
	if (!method || !cseq) {
		return;
	}
	const char *iphone_mirror_mode = getenv("IPHONE_MIRROR_AIRPLAY_MODE");
	int iphone_mirror_media_cast = iphone_mirror_mode != NULL &&
		(!strcmp(iphone_mirror_mode, "media") ||
		 !strcmp(iphone_mirror_mode, "combined"));
'@ -replace "`r?`n", $RouterNewLine
$MediaRequestPreamble = @'
	method = http_request_get_method(request);
	url = http_request_get_url(request);
	cseq = http_request_get_header(request, "CSeq");
	const char *iphone_mirror_mode = getenv("IPHONE_MIRROR_AIRPLAY_MODE");
	int iphone_mirror_media_cast = iphone_mirror_mode != NULL &&
		(!strcmp(iphone_mirror_mode, "media") ||
		 !strcmp(iphone_mirror_mode, "combined"));
	int iphone_mirror_media_control = iphone_mirror_media_cast && url != NULL && (
		!strcmp(url, "/play") || !strcmp(url, "/playback-info") ||
		!strcmp(url, "/stop") || !strncmp(url, "/rate", strlen("/rate")) ||
		!strncmp(url, "/scrub", strlen("/scrub")));
	if (!method || (!cseq && !iphone_mirror_media_control)) {
		return;
	}
'@ -replace "`r?`n", $RouterNewLine
if ($RaopRouter.Contains($LegacyRequestPreamble)) {
    $RaopRouter = $RaopRouter.Replace($LegacyRequestPreamble, $MediaRequestPreamble)
}

$LegacySuccessResponse = @'
	*response = http_response_init("RTSP/1.0", 200, "OK");

	http_response_add_header(*response, "CSeq", cseq);
'@ -replace "`r?`n", $RouterNewLine
$MediaSuccessResponse = @'
	*response = http_response_init("RTSP/1.0", 200, "OK");

	if (cseq != NULL) http_response_add_header(*response, "CSeq", cseq);
'@ -replace "`r?`n", $RouterNewLine
if ($RaopRouter.Contains($LegacySuccessResponse)) {
    $RaopRouter = $RaopRouter.Replace($LegacySuccessResponse, $MediaSuccessResponse)
}

$LegacyHandlerStart = @'
	raop_handler_t handler = NULL;
	if (!strcmp(method, "GET") && !strcmp(url, "/info")) {
'@ -replace "`r?`n", $RouterNewLine
$MediaHandlerStart = @'
	raop_handler_t handler = NULL;
	if (iphone_mirror_media_cast && !strcmp(method, "POST") &&
		!strcmp(url, "/play")) {
		handler = &iphone_mirror_raop_handler_play;
	} else if (iphone_mirror_media_cast && !strcmp(method, "GET") &&
		!strcmp(url, "/playback-info")) {
		handler = &iphone_mirror_raop_handler_playbackinfo;
	} else if (iphone_mirror_media_cast && !strcmp(method, "POST") &&
		!strcmp(url, "/stop")) {
		if (conn->raop->callbacks.video_play != NULL)
			conn->raop->callbacks.video_play(
				conn->raop->callbacks.cls, NULL, 0, 0);
	} else if (!strncmp(url, "/rate", 5)) {
		/* IPHONE_MIRROR_RAOP_RATE_CONTROL */
		const char *value = strstr(url, "value=");
		if (conn->raop->callbacks.video_play != NULL)
			conn->raop->callbacks.video_play(conn->raop->callbacks.cls,
				value != NULL && atof(value + 6) == 0 ?
				"iphonemirror://pause" : "iphonemirror://resume", 0, 0);
	} else if (!strncmp(url, "/scrub", 6)) {
		/* IPHONE_MIRROR_RAOP_SEEK_CONTROL */
		const char *position = strstr(url, "position=");
		if (position != NULL && conn->raop->callbacks.video_play != NULL)
			conn->raop->callbacks.video_play(conn->raop->callbacks.cls,
				"iphonemirror://seek", 0, atof(position + 9));
	} else if (!strcmp(method, "GET") && !strcmp(url, "/info")) {
'@ -replace "`r?`n", $RouterNewLine
if ($RaopRouter.Contains($LegacyHandlerStart)) {
    $RaopRouter = $RaopRouter.Replace($LegacyHandlerStart, $MediaHandlerStart)
}
$RaopRouter = $RaopRouter.Replace(
    '} else if (iphone_mirror_media_cast && !strncmp(url, "/rate", 5)) {',
    '} else if (!strncmp(url, "/rate", 5)) {')
$RaopRouter = $RaopRouter.Replace(
    '} else if (iphone_mirror_media_cast && !strncmp(url, "/scrub", 6)) {',
    '} else if (!strncmp(url, "/scrub", 6)) {')
if (-not $RaopRouter.Contains('iphone_mirror_media_control =')) {
    throw 'AirPlay unified RAOP media-control route was not applied.'
}
$LegacyModeCheck = @'
	int iphone_mirror_media_cast = iphone_mirror_mode != NULL &&
		!strcmp(iphone_mirror_mode, "media");
'@ -replace "`r?`n", $RouterNewLine
$CombinedModeCheck = @'
	int iphone_mirror_media_cast = iphone_mirror_mode != NULL &&
		(!strcmp(iphone_mirror_mode, "media") ||
		 !strcmp(iphone_mirror_mode, "combined"));
'@ -replace "`r?`n", $RouterNewLine
if ($RaopRouter.Contains($LegacyModeCheck)) {
    $RaopRouter = $RaopRouter.Replace($LegacyModeCheck, $CombinedModeCheck)
}
if (-not $RaopRouter.Contains('!strcmp(iphone_mirror_mode, "combined")')) {
    throw 'AirPlay combined receiver mode was not applied.'
}
[IO.File]::WriteAllText($RaopRouterFile, $RaopRouter, $Encoding)

# Make every AirPlay endpoint use the stable, locally administered device ID
# supplied by WirelessHost. Recomputing it inside the DLL would diverge for
# non-ASCII computer names and using the adapter MAC would make /server-info
# disagree with Bonjour and /info.
$Wrapper = [IO.File]::ReadAllText($WrapperServerFile, $Encoding)
$WrapperNewLine = if ($Wrapper.Contains("`r`n")) { "`r`n" } else { "`n" }
$EnvironmentSyncMarker = 'IPHONE_MIRROR_DLL_ENVIRONMENT_SYNC'
if (-not $Wrapper.Contains($EnvironmentSyncMarker)) {
    $Wrapper = $Wrapper.Replace('#include <thread>',
        '#include <thread>' + $WrapperNewLine + '#include <cstdlib>' +
        $WrapperNewLine + '#include <string>')
    $Needle = 'BOOL GetMacAddress(char strMac[6]);'
    $Replacement = @'
BOOL GetMacAddress(char strMac[6]);

/* IPHONE_MIRROR_DLL_ENVIRONMENT_SYNC: the host and this DLL use separate CRT
 * environment caches. Copy host-supplied Win32 values into this DLL's UCRT
 * before any AirPlayServer getenv() call. */
static bool
iphone_mirror_sync_environment_value(const wchar_t* wide_name,
	const char* narrow_name)
{
	SetLastError(ERROR_SUCCESS);
	DWORD required = GetEnvironmentVariableW(wide_name, NULL, 0);
	if (required == 0)
		return GetLastError() == ERROR_ENVVAR_NOT_FOUND;

	std::wstring wide_value(required, L'\0');
	DWORD length = GetEnvironmentVariableW(
		wide_name, &wide_value[0], required);
	if (length == 0 || length >= required) return false;
	wide_value.resize(length);

	int utf8_length = WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS,
		wide_value.data(), (int)wide_value.size(), NULL, 0, NULL, NULL);
	if (utf8_length <= 0) return false;
	std::string utf8_value((size_t)utf8_length, '\0');
	if (WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS,
		wide_value.data(), (int)wide_value.size(), &utf8_value[0],
		utf8_length, NULL, NULL) != utf8_length) return false;
	if (_putenv_s(narrow_name, utf8_value.c_str()) != 0) return false;
	/* _putenv_s may rewrite the process value through the active ANSI code
	 * page. Restore the original wide value for the DNS-SD shim. */
	return SetEnvironmentVariableW(wide_name, wide_value.c_str()) != FALSE;
}

static bool
iphone_mirror_sync_environment()
{
	struct environment_entry {
		const wchar_t* wide_name;
		const char* narrow_name;
	};
	static const environment_entry entries[] = {
		{ L"IPHONE_MIRROR_AIRPLAY_WIDTH", "IPHONE_MIRROR_AIRPLAY_WIDTH" },
		{ L"IPHONE_MIRROR_AIRPLAY_HEIGHT", "IPHONE_MIRROR_AIRPLAY_HEIGHT" },
		{ L"IPHONE_MIRROR_AIRPLAY_FPS", "IPHONE_MIRROR_AIRPLAY_FPS" },
		{ L"IPHONE_MIRROR_AIRPLAY_MODE", "IPHONE_MIRROR_AIRPLAY_MODE" },
		{ L"IPHONE_MIRROR_AIRPLAY_NAME", "IPHONE_MIRROR_AIRPLAY_NAME" },
		{ L"IPHONE_MIRROR_AIRPLAY_DEVICE_ID", "IPHONE_MIRROR_AIRPLAY_DEVICE_ID" },
		{ L"IPHONE_MIRROR_AIRPLAY_PAIRING_SEED", "IPHONE_MIRROR_AIRPLAY_PAIRING_SEED" },
	};
	for (size_t index = 0; index < sizeof(entries) / sizeof(entries[0]); ++index)
		if (!iphone_mirror_sync_environment_value(
				entries[index].wide_name, entries[index].narrow_name))
			return false;
	return true;
}
'@ -replace "`r?`n", $WrapperNewLine
    if ([regex]::Matches($Wrapper, [regex]::Escape($Needle)).Count -ne 1) {
        throw 'AirPlay wrapper environment-sync insertion point changed.'
    }
    $Wrapper = $Wrapper.Replace($Needle,
        $Replacement.TrimEnd("`r", "`n"))
}
# Upgrade source trees patched by an earlier revision that used const data()
# pointers with this upstream project's pre-C++17 toolchain.
$Wrapper = $Wrapper.Replace(
    'wide_name, wide_value.data(), required);',
    'wide_name, &wide_value[0], required);')
$Wrapper = $Wrapper.Replace(
    'wide_value.data(), (int)wide_value.size(), utf8_value.data(),',
    'wide_value.data(), (int)wide_value.size(), &utf8_value[0],')
if (-not $Wrapper.Contains('wide_name, &wide_value[0], required);') -or
    -not $Wrapper.Contains(
        'wide_value.data(), (int)wide_value.size(), &utf8_value[0],')) {
    throw 'AirPlay wrapper environment-sync writable buffers were not applied.'
}
$EnvironmentSyncCall = @'
	m_pCallback = callback;
	if (!iphone_mirror_sync_environment() && m_pCallback != NULL)
		m_pCallback->log(3, "IPHONE_MIRROR_DLL_ENVIRONMENT_SYNC failed");
'@ -replace "`r?`n", $WrapperNewLine
if (-not $Wrapper.Contains('if (!iphone_mirror_sync_environment()')) {
    $Needle = "`tm_pCallback = callback;"
    if ([regex]::Matches($Wrapper, [regex]::Escape($Needle)).Count -ne 1) {
        throw 'AirPlay wrapper environment-sync call point changed.'
    }
    $Wrapper = $Wrapper.Replace($Needle,
        $EnvironmentSyncCall.TrimEnd("`r", "`n"))
}
if (-not $Wrapper.Contains($EnvironmentSyncMarker) -or
    -not $Wrapper.Contains('if (!iphone_mirror_sync_environment()')) {
    throw 'AirPlay wrapper CRT environment synchronization was not applied.'
}
$DeviceIdParserMarker = 'IPHONE_MIRROR_DEVICE_ID_PARSER'
if (-not $Wrapper.Contains($DeviceIdParserMarker)) {
    $Needle = 'static bool' + $WrapperNewLine +
        'iphone_mirror_sync_environment()'
    $Replacement = @'
/* IPHONE_MIRROR_DEVICE_ID_PARSER */
static int
iphone_mirror_hex_nibble(char value)
{
	if (value >= '0' && value <= '9') return value - '0';
	if (value >= 'a' && value <= 'f') return value - 'a' + 10;
	if (value >= 'A' && value <= 'F') return value - 'A' + 10;
	return -1;
}

static bool
iphone_mirror_parse_device_id(const char* value, char* bytes, size_t length)
{
	if (value == NULL || bytes == NULL || length != 6 || strlen(value) != 17)
		return false;
	for (size_t index = 0; index < length; ++index) {
		size_t offset = index * 3;
		if (index != 0 && value[offset - 1] != ':') return false;
		int high = iphone_mirror_hex_nibble(value[offset]);
		int low = iphone_mirror_hex_nibble(value[offset + 1]);
		if (high < 0 || low < 0) return false;
		bytes[index] = (char)((high << 4) | low);
	}
	return true;
}

static bool
iphone_mirror_sync_environment()
'@ -replace "`r?`n", $WrapperNewLine
    if ([regex]::Matches($Wrapper, [regex]::Escape($Needle)).Count -ne 1) {
        throw 'AirPlay wrapper device-ID parser insertion point changed.'
    }
    $Wrapper = $Wrapper.Replace($Needle,
        $Replacement.TrimEnd("`r", "`n"))
}
$RaopWrapperCallbacksMarker = 'IPHONE_MIRROR_RAOP_WRAPPER_CALLBACKS'
if (-not $Wrapper.Contains($RaopWrapperCallbacksMarker)) {
    $WrapperNewLine = if ($Wrapper.Contains("`r`n")) { "`r`n" } else { "`n" }
    $Needle = "`tm_stRaopCB.video_process = video_process;"
    $Replacement = @'
	m_stRaopCB.video_process = video_process;
	/* IPHONE_MIRROR_RAOP_WRAPPER_CALLBACKS */
	m_stRaopCB.video_play = ap_video_play;
	m_stRaopCB.video_get_play_info = ap_video_get_play_info;
'@ -replace "`r?`n", $WrapperNewLine
    if ([regex]::Matches($Wrapper, [regex]::Escape($Needle)).Count -ne 1) {
        throw 'AirPlay wrapper RAOP callback insertion point changed.'
    }
    $Wrapper = $Wrapper.Replace($Needle, $Replacement.TrimEnd("`r", "`n"))
}
$LegacyIdentityPattern =
    '(?s)\r?\n\t\t/\* IPHONE_MIRROR_MEDIA_CAST_IDENTITY \*/.*?' +
    '(?=\r?\n\r?\n\t\tm_pAirplay = airplay_init)'
$LegacyIdentityMatches = [regex]::Matches($Wrapper, $LegacyIdentityPattern).Count
if ($LegacyIdentityMatches -gt 1) {
    throw 'AirPlay wrapper contains duplicate legacy identity blocks.'
}
if ($LegacyIdentityMatches -eq 1) {
    $Wrapper = [regex]::Replace($Wrapper, $LegacyIdentityPattern, '')
}
$RuntimeDeviceIdMarker = 'IPHONE_MIRROR_RUNTIME_DEVICE_ID'
if (-not $Wrapper.Contains($RuntimeDeviceIdMarker)) {
    $Needle = "`t`tGetMacAddress(hwaddr);"
    $Replacement = @'
		GetMacAddress(hwaddr);
		/* IPHONE_MIRROR_RUNTIME_DEVICE_ID */
		char receiver_device_id[18] = {};
		DWORD receiver_device_id_length = GetEnvironmentVariableA(
			"IPHONE_MIRROR_AIRPLAY_DEVICE_ID", receiver_device_id,
			sizeof(receiver_device_id));
		if (receiver_device_id_length != 0 &&
			(receiver_device_id_length != 17 ||
			 !iphone_mirror_parse_device_id(
				receiver_device_id, hwaddr, sizeof(hwaddr))) &&
			m_pCallback != NULL)
			m_pCallback->log(3, "IPHONE_MIRROR_RUNTIME_DEVICE_ID invalid");
'@ -replace "`r?`n", $WrapperNewLine
    if ([regex]::Matches($Wrapper, [regex]::Escape($Needle)).Count -ne 1) {
        throw 'AirPlay runtime device-ID insertion point changed.'
    }
    $Wrapper = $Wrapper.Replace($Needle,
        $Replacement.TrimEnd("`r", "`n"))
}
$LegacyRuntimeDeviceId = @'
		/* IPHONE_MIRROR_RUNTIME_DEVICE_ID */
		const char* receiver_device_id =
			getenv("IPHONE_MIRROR_AIRPLAY_DEVICE_ID");
		if (!iphone_mirror_parse_device_id(
				receiver_device_id, hwaddr, sizeof(hwaddr)) &&
			receiver_device_id != NULL && m_pCallback != NULL)
			m_pCallback->log(3, "IPHONE_MIRROR_RUNTIME_DEVICE_ID invalid");
'@ -replace "`r?`n", $WrapperNewLine
$RuntimeDeviceId = @'
		/* IPHONE_MIRROR_RUNTIME_DEVICE_ID */
		char receiver_device_id[18] = {};
		DWORD receiver_device_id_length = GetEnvironmentVariableA(
			"IPHONE_MIRROR_AIRPLAY_DEVICE_ID", receiver_device_id,
			sizeof(receiver_device_id));
		if (receiver_device_id_length != 0 &&
			(receiver_device_id_length != 17 ||
			 !iphone_mirror_parse_device_id(
				receiver_device_id, hwaddr, sizeof(hwaddr))) &&
			m_pCallback != NULL)
			m_pCallback->log(3, "IPHONE_MIRROR_RUNTIME_DEVICE_ID invalid");
'@ -replace "`r?`n", $WrapperNewLine
$Wrapper = $Wrapper.Replace(
    $LegacyRuntimeDeviceId.TrimEnd("`r", "`n"),
    $RuntimeDeviceId.TrimEnd("`r", "`n"))
if (-not $Wrapper.Contains($DeviceIdParserMarker) -or
    -not $Wrapper.Contains($RuntimeDeviceIdMarker) -or
    $Wrapper.Contains('IPHONE_MIRROR_MEDIA_CAST_IDENTITY') -or
    $Wrapper.Contains('GetComputerNameA(computer') -or
    $Wrapper.Contains('"video-cast-v2"')) {
    throw 'AirPlay receiver endpoint identity was not unified.'
}
[IO.File]::WriteAllText($WrapperServerFile, $Wrapper, $Encoding)

# The original wrapper opens FFmpeg with the Annex-B SPS/PPS bytes as
# AVCodecContext extradata and only attempts initialization for a packet marked
# as a key frame. Some senders provide the codec packet in a form that FFmpeg
# rejects as extradata, leaving every subsequent frame silently dropped. Feed
# the codec packet through the normal H.264 parser instead and open the
# decoder before handling either packet type.
$Channel = [IO.File]::ReadAllText($ChannelFile, $Encoding)
$ChannelMarker = 'IPHONE_MIRROR_H264_DECODER_RECOVERY'
if (-not $Channel.Contains($ChannelMarker)) {
    $ChannelNewLine = if ($Channel.Contains("`r`n")) { "`r`n" } else { "`n" }
    $InitPattern = '(?s)int FgAirplayChannel::initFFmpeg\(.*?\r?\n\}\r?\n\r?\nvoid FgAirplayChannel::unInitFFmpeg'
    if ([regex]::Matches($Channel, $InitPattern).Count -ne 1) {
        throw 'AirPlay H.264 decoder initialization patch point changed.'
    }
    $InitReplacement = @'
int FgAirplayChannel::initFFmpeg(const void* privatedata, int privatedatalen) {
	/* IPHONE_MIRROR_H264_DECODER_RECOVERY: SPS/PPS is Annex-B input, not
	 * avcC extradata. Let the H.264 parser consume it as a regular packet. */
	(void)privatedata;
	(void)privatedatalen;
	if (m_bCodecOpened) return 0;
	m_pCodec = avcodec_find_decoder(AV_CODEC_ID_H264);
	if (m_pCodec == NULL) {
		printf("IPHONE_MIRROR_H264_DECODER_RECOVERY decoder_missing\n");
		return -1;
	}
	m_pCodecCtx = avcodec_alloc_context3(m_pCodec);
	if (m_pCodecCtx == NULL) {
		printf("IPHONE_MIRROR_H264_DECODER_RECOVERY context_alloc_failed\n");
		return -1;
	}
	m_pCodecCtx->pix_fmt = AV_PIX_FMT_YUV420P;
	m_pCodecCtx->flags |= AV_CODEC_FLAG_LOW_DELAY;
	m_pCodecCtx->thread_count = 4;
	m_pCodecCtx->thread_type = FF_THREAD_SLICE;
	int res = avcodec_open2(m_pCodecCtx, m_pCodec, NULL);
	if (res < 0) {
		printf("IPHONE_MIRROR_H264_DECODER_RECOVERY open_failed=%d\n", res);
		avcodec_free_context(&m_pCodecCtx);
		m_pCodec = NULL;
		return res;
	}
	m_bCodecOpened = true;
	return 0;
}

void FgAirplayChannel::unInitFFmpeg
'@ -replace '\r?\n', $ChannelNewLine
    $Channel = [regex]::Replace($Channel, $InitPattern, $InitReplacement)
    $Condition = 'if (data->is_key && !m_bCodecOpened) {'
    if ([regex]::Matches($Channel, [regex]::Escape($Condition)).Count -ne 1) {
        throw 'AirPlay H.264 decoder open condition changed.'
    }
    $Channel = $Channel.Replace($Condition, 'if (!m_bCodecOpened) {')
    [IO.File]::WriteAllText($ChannelFile, $Channel, $Encoding)
}
if (-not $Channel.Contains($ChannelMarker) -or
    -not $Channel.Contains('avcodec_open2(m_pCodecCtx, m_pCodec, NULL)')) {
    throw 'AirPlay H.264 decoder recovery patch was not applied.'
}

# Rotation sends a new SPS/PPS followed by an IDR. The protocol layer combines
# those into one access unit before this decoder sees it. Drain each packet,
# but do not flush merely because it contains an IDR: ordinary keyframes are
# common at original quality and flushing them can discard the new geometry.
$RotationMarker = 'IPHONE_MIRROR_H264_ROTATION_RECOVERY'
if (-not $Channel.Contains($RotationMarker)) {
    $ChannelNewLine = if ($Channel.Contains("`r`n")) { "`r`n" } else { "`n" }
    $DecodePattern = '(?s)int FgAirplayChannel::decodeH264Data\(.*?\r?\n\}\r?\n\r?\nint FgAirplayChannel::scaleH264Data'
    if ([regex]::Matches($Channel, $DecodePattern).Count -ne 1) {
        throw 'AirPlay H.264 decode replacement point changed.'
    }
    $DecodeReplacement = @'
int FgAirplayChannel::decodeH264Data(SFgH264Data* data, const char* remoteName, const char* remoteDeviceId) {
	/* IPHONE_MIRROR_H264_ROTATION_RECOVERY */
	if (data == NULL || data->data == NULL || data->size <= 0) return -1;
	CAutoLock oLock(m_mutexVideo, "decodeH264Data");
	int ret = 0;
	if (!m_bCodecOpened) {
		ret = initFFmpeg(NULL, 0);
		if (ret < 0) return ret;
	}
	AVPacket packet;
	av_init_packet(&packet);
	packet.data = data->data;
	packet.size = data->size;
	packet.pts = data->pts;
	ret = avcodec_send_packet(m_pCodecCtx, &packet);
	if (ret < 0 && ret != AVERROR(EAGAIN)) {
		printf("IPHONE_MIRROR_H264_ROTATION_RECOVERY send_failed=%d\\n", ret);
		return ret;
	}

	/* One packet can produce more than one frame, especially while the
	 * decoder drains frames queued immediately before an orientation change. */
	for (;;) {
		AVFrame* pFrame = av_frame_alloc();
		if (pFrame == NULL) return -1;
		ret = avcodec_receive_frame(m_pCodecCtx, pFrame);
		if (ret == AVERROR(EAGAIN) || ret == AVERROR_EOF) {
			av_frame_free(&pFrame);
			break;
		}
		if (ret < 0) {
			printf("IPHONE_MIRROR_H264_ROTATION_RECOVERY receive_failed=%d\\n", ret);
			av_frame_free(&pFrame);
			break;
		}
		if (pFrame->width <= 0 || pFrame->height <= 0 ||
			pFrame->data[0] == NULL || pFrame->data[1] == NULL ||
			pFrame->data[2] == NULL) {
			av_frame_free(&pFrame);
			continue;
		}

		const int chromaHeight = (pFrame->height + 1) >> 1;
		const int ySize = pFrame->linesize[0] * pFrame->height;
		const int uSize = pFrame->linesize[1] * chromaHeight;
		const int vSize = pFrame->linesize[2] * chromaHeight;
		const int totalSize = ySize + uSize + vSize;
		if (ySize <= 0 || uSize <= 0 || vSize <= 0 || totalSize <= 0) {
			av_frame_free(&pFrame);
			continue;
		}
		if (m_sVideoFrameOri.data == NULL ||
			m_sVideoFrameOri.width != pFrame->width ||
			m_sVideoFrameOri.height != pFrame->height ||
			m_sVideoFrameOri.dataTotalLen < totalSize) {
			delete[] m_sVideoFrameOri.data;
			m_sVideoFrameOri.data = new uint8_t[totalSize];
		}
		m_sVideoFrameOri.width = pFrame->width;
		m_sVideoFrameOri.height = pFrame->height;
		m_sVideoFrameOri.pts = pFrame->pts;
		m_sVideoFrameOri.isKey = pFrame->key_frame;
		m_sVideoFrameOri.dataTotalLen = totalSize;
		m_sVideoFrameOri.dataLen[0] = ySize;
		m_sVideoFrameOri.dataLen[1] = uSize;
		m_sVideoFrameOri.dataLen[2] = vSize;
		m_sVideoFrameOri.pitch[0] = pFrame->linesize[0];
		m_sVideoFrameOri.pitch[1] = pFrame->linesize[1];
		m_sVideoFrameOri.pitch[2] = pFrame->linesize[2];
		memcpy(m_sVideoFrameOri.data, pFrame->data[0], ySize);
		memcpy(m_sVideoFrameOri.data + ySize, pFrame->data[1], uSize);
		memcpy(m_sVideoFrameOri.data + ySize + uSize, pFrame->data[2], vSize);

		if (m_pCallback != NULL) {
			if (m_fScaleRatio < 0.9999f || m_fScaleRatio > 1.0001f) {
				scaleH264Data(&m_sVideoFrameOri);
				m_pCallback->outputVideo(&m_sVideoFrameScale, remoteName, remoteDeviceId);
			} else {
				m_pCallback->outputVideo(&m_sVideoFrameOri, remoteName, remoteDeviceId);
			}
		}
		av_frame_free(&pFrame);
	}
	return 0;
}

int FgAirplayChannel::scaleH264Data
'@ -replace '\r?\n', $ChannelNewLine
    $Channel = [regex]::Replace($Channel, $DecodePattern, $DecodeReplacement)
    [IO.File]::WriteAllText($ChannelFile, $Channel, $Encoding)
}
if (-not $Channel.Contains($RotationMarker) -or
    -not $Channel.Contains('avcodec_receive_frame(m_pCodecCtx, pFrame)')) {
    throw 'AirPlay H.264 rotation recovery patch was not applied.'
}
Write-Host 'Applied combined AirPlay mirror and URL-video request policy.'

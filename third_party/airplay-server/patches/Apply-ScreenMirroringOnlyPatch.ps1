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
$WrapperServerFile = Join-Path $SourceRoot 'airplay2dll\src\FgAirplayServer.cpp'
foreach ($File in @($DnsSdFile, $AirPlayFile, $AirPlayHandlersFile,
        $RaopFile, $RaopHeaderFile, $RaopRouterFile, $PairingFile, $GlobalFile,
        $WrapperServerFile)) {
    if (-not (Test-Path -LiteralPath $File)) {
        throw "AirPlayServer media capability source is missing: $File"
    }
}

$Encoding = [Text.UTF8Encoding]::new($false)
$Global = [IO.File]::ReadAllText($GlobalFile, $Encoding)
$Global = $Global.Replace('"AppleTV14,1"', '"AppleTV3,2"')
$Global = $Global.Replace('"Kodi,1"', '"AppleTV3,2"')
$Global = $Global.Replace('"845.5.1"', '"220.68"')
if (-not $Global.Contains('"AppleTV3,2"') -or
    -not $Global.Contains('"220.68"')) {
    throw 'UxPlay-compatible AirPlay model/version metadata was not applied.'
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
$LegacyFeatures = @(
    '"0x5A7FFFF7, 0x1E"',
    '"0x5A7FFFF7,0x1E"',
    '"0x5A7FFFC0,0x1E"',
    '"0x484051C0,0x0"',
    '"0x1A7FFEC0,0x0"'
)
$MirroringOnlyFeatures = '"0x5A7FFEC0,0x0"'
if ([regex]::Matches($DnsSd, [regex]::Escape($MirroringOnlyFeatures)).Count -ne 2) {
    foreach ($Legacy in $LegacyFeatures) {
        $DnsSd = $DnsSd.Replace($Legacy, $MirroringOnlyFeatures)
    }
    $Matches = [regex]::Matches($DnsSd, [regex]::Escape($MirroringOnlyFeatures)).Count
    if ($Matches -ne 2) {
        throw "AirPlay DNS-SD feature declaration changed (expected 2 replacements, found $Matches)."
    }
    [IO.File]::WriteAllText($DnsSdFile, $DnsSd, $Encoding)
    Write-Host 'Disabled AirPlay media/photo/HLS capability advertisement.'
}
else {
    Write-Host 'AirPlay mirroring-only DNS-SD capabilities are already applied.'
}

$AirPlay = [IO.File]::ReadAllText($AirPlayFile, $Encoding)
$AirPlay = $AirPlay.Replace(
    '*response = http_response_init("RTSP/1.0", 200, "OK");',
    '*response = http_response_init("HTTP/1.1", 200, "OK");')
$AirPlay = $AirPlay.Replace('AirTunes/845.5.1', 'AirTunes/220.68')
$AirPlay = $AirPlay.Replace('<integer>119</integer>', '<integer>1518337783</integer>')
$AirPlay = $AirPlay.Replace('<integer>64</integer>', '<integer>1518337783</integer>')
$AirPlay = $AirPlay.Replace('<integer>55</integer>', '<integer>1518337783</integer>')
$AirPlay = $AirPlay.Replace('<integer>639</integer>', '<integer>1518337783</integer>')
$AirPlay = $AirPlay.Replace('<string>Kodi,1</string>',
    '<string>AppleTV3,2</string>')
$AirPlay = $AirPlay.Replace('<string>AppleTV14,1</string>',
    '<string>AppleTV3,2</string>')
if (-not $AirPlay.Contains('<integer>1518337783</integer>')) {
    throw 'AirPlay media-cast server-info capability was not applied.'
}
if (-not $AirPlay.Contains('<string>AppleTV3,2</string>')) {
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
elseif (-not $AirPlay.Contains('IPHONE_MIRROR_MEDIA_CAST_MODE')) {
    $Needle = "`tlogger_log(conn->airplay->logger, LOGGER_DEBUG, `"[AirPlay] Handling request %s with URL %s`", method, url);"
    $Replacement = $ConditionalBlock + $NewLine + $Needle
    if ([regex]::Matches($AirPlay, [regex]::Escape($Needle)).Count -ne 1) {
        throw 'AirPlay request router changed.'
    }
    $AirPlay = $AirPlay.Replace($Needle, $Replacement)
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
	else if (!strcmp(method, "POST") && !strcmp(url, "/reverse")) {
'@ -replace "`r?`n", $NewLine
    if ([regex]::Matches($AirPlay, [regex]::Escape($Needle)).Count -ne 1) {
        throw 'AirPlay stop-control insertion point changed.'
    }
    $AirPlay = $AirPlay.Replace($Needle, $Replacement.TrimEnd("`r", "`n"))
}
$AirPlay = $AirPlay.Replace(
    'conn->airplay->callbacks.video_play(conn->airplay->callbacks.cls, url, volume, start_pos);',
    'conn->airplay->callbacks.video_play(conn->airplay->callbacks.cls, url, volume, start_pos_sec > 0 ? start_pos_sec : start_pos);')
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
$Raop = $Raop.Replace('0x1A7FFEC0ULL', '0x5A7FFEC0ULL')
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
			plist_new_uint(0x5A7FFEC0ULL));
'@ -replace "`r?`n", $NewLine
$RuntimeInfoBlock = @'
	/* IPHONE_MIRROR_RUNTIME_FEATURES */
	const char *iphone_mirror_mode = getenv("IPHONE_MIRROR_AIRPLAY_MODE");
	unsigned long long iphone_mirror_features = iphone_mirror_mode != NULL &&
		(!strcmp(iphone_mirror_mode, "media") ||
		 !strcmp(iphone_mirror_mode, "combined"))
		? 0x5A7FFEF7ULL : 0x5A7FFEC0ULL;
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
if (-not $Raop.Contains('0x5A7FFEF7ULL') -or
    -not $Raop.Contains('0x5A7FFEC0ULL')) {
    throw 'AirPlay /info runtime feature declaration was not applied.'
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
		if (flag != NULL) plist_get_bool_val(flag, &is_mirroring);
		if (is_mirroring) conn->mirror_session_requested = 1;
		else reject = 1;
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
	} else if (!strcmp(method, "GET") && !strcmp(url, "/info")) {
'@ -replace "`r?`n", $RouterNewLine
if ($RaopRouter.Contains($LegacyHandlerStart)) {
    $RaopRouter = $RaopRouter.Replace($LegacyHandlerStart, $MediaHandlerStart)
}
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

# Give the media process a stable, locally administered device ID distinct
# from the mirror process. iOS otherwise merges both _airplay services because
# the two server instances obtain the same physical adapter MAC address.
$Wrapper = [IO.File]::ReadAllText($WrapperServerFile, $Encoding)
$WrapperNewLine = if ($Wrapper.Contains("`r`n")) { "`r`n" } else { "`n" }
$LegacyIdentityCondition =
    'if \(GetEnvironmentVariableA\("IPHONE_MIRROR_AIRPLAY_MODE",\s*' +
    'iphone_mirror_mode, sizeof\(iphone_mirror_mode\)\) == 5 &&\s*' +
    '!strcmp\(iphone_mirror_mode, "media"\)\) \{'
$CombinedIdentityCondition = @'
		DWORD iphone_mirror_mode_length = GetEnvironmentVariableA(
			"IPHONE_MIRROR_AIRPLAY_MODE", iphone_mirror_mode,
			sizeof(iphone_mirror_mode));
		if ((iphone_mirror_mode_length == 5 &&
			 !strcmp(iphone_mirror_mode, "media")) ||
			(iphone_mirror_mode_length == 8 &&
			 !strcmp(iphone_mirror_mode, "combined"))) {
'@ -replace "`r?`n", $WrapperNewLine
$Wrapper = [regex]::Replace($Wrapper, $LegacyIdentityCondition,
    $CombinedIdentityCondition.TrimEnd("`r", "`n"))
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
$MediaIdentityMarker = 'IPHONE_MIRROR_MEDIA_CAST_IDENTITY'
if (-not $Wrapper.Contains($MediaIdentityMarker)) {
    $WrapperNewLine = if ($Wrapper.Contains("`r`n")) { "`r`n" } else { "`n" }
    $Needle = "`t`tGetMacAddress(hwaddr);"
    $Replacement = @'
		GetMacAddress(hwaddr);
		/* IPHONE_MIRROR_MEDIA_CAST_IDENTITY */
		char iphone_mirror_mode[16] = {};
		DWORD iphone_mirror_mode_length = GetEnvironmentVariableA(
			"IPHONE_MIRROR_AIRPLAY_MODE", iphone_mirror_mode,
			sizeof(iphone_mirror_mode));
		if ((iphone_mirror_mode_length == 5 &&
			 !strcmp(iphone_mirror_mode, "media")) ||
			(iphone_mirror_mode_length == 8 &&
			 !strcmp(iphone_mirror_mode, "combined"))) {
			char computer[MAX_COMPUTERNAME_LENGTH + 1] = "iPhoneMirror";
			DWORD computer_length = sizeof(computer);
			if (!GetComputerNameA(computer, &computer_length))
				computer_length = (DWORD)strlen(computer);
			unsigned long long hash = 1469598103934665603ULL;
			for (DWORD index = 0; index < computer_length; ++index) {
				hash ^= (unsigned char)computer[index];
				hash *= 1099511628211ULL;
			}
			hwaddr[0] = 0x02;
			for (int index = 1; index < 6; ++index)
				hwaddr[index] = (char)(hash >> ((index - 1) * 8));
		}
'@ -replace "`r?`n", $WrapperNewLine
    if ([regex]::Matches($Wrapper, [regex]::Escape($Needle)).Count -ne 1) {
        throw 'AirPlay media receiver identity insertion point changed.'
    }
    $Wrapper = $Wrapper.Replace($Needle, $Replacement.TrimEnd("`r", "`n"))
}
$MediaIdentityV2Marker = 'IPHONE_MIRROR_MEDIA_CAST_IDENTITY_V2'
if (-not $Wrapper.Contains($MediaIdentityV2Marker)) {
    $WrapperNewLine = if ($Wrapper.Contains("`r`n")) { "`r`n" } else { "`n" }
    $Needle = @'
			for (DWORD index = 0; index < computer_length; ++index) {
				hash ^= (unsigned char)computer[index];
				hash *= 1099511628211ULL;
			}
			hwaddr[0] = 0x02;
'@ -replace "`r?`n", $WrapperNewLine
    $Replacement = @'
			for (DWORD index = 0; index < computer_length; ++index) {
				hash ^= (unsigned char)computer[index];
				hash *= 1099511628211ULL;
			}
			/* IPHONE_MIRROR_MEDIA_CAST_IDENTITY_V2 */
			const char profile[] = "video-cast-v2";
			for (size_t index = 0; index + 1 < sizeof(profile); ++index) {
				hash ^= (unsigned char)profile[index];
				hash *= 1099511628211ULL;
			}
			hwaddr[0] = 0x02;
'@ -replace "`r?`n", $WrapperNewLine
    $MatchedNeedle = $Needle
    $MatchedReplacement = $Replacement
    if (-not $Wrapper.Contains($MatchedNeedle)) {
        $MatchedNeedle = $Needle.Replace("`r`n", "`n")
        $MatchedReplacement = $Replacement.Replace("`r`n", "`n")
    }
    if ([regex]::Matches($Wrapper, [regex]::Escape($MatchedNeedle)).Count -ne 1) {
        throw 'AirPlay media receiver v2 identity insertion point changed.'
    }
    $Wrapper = $Wrapper.Replace($MatchedNeedle,
        $MatchedReplacement.TrimEnd("`r", "`n"))
}
[IO.File]::WriteAllText($WrapperServerFile, $Wrapper, $Encoding)
Write-Host 'Applied combined AirPlay mirror and URL-video request policy.'

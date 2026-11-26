[CmdletBinding()]
param([Parameter(Mandatory)][string]$SourceRoot)
$ErrorActionPreference = 'Stop'
$source = (Resolve-Path -LiteralPath $SourceRoot).Path
$utf8 = [Text.UTF8Encoding]::new($false)

function Replace-ExactlyOnce([string]$Text, [string]$Before, [string]$After, [string]$Name) {
    $oldCount = [regex]::Matches($Text, [regex]::Escape($Before)).Count
    $newCount = [regex]::Matches($Text, [regex]::Escape($After)).Count
    if ($newCount -eq 1 -and
        [regex]::Matches($Text.Replace($After, ''), [regex]::Escape($Before)).Count -eq 0) { return $Text }
    if ($oldCount -ne 1 -or $newCount -ne 0) {
        throw "Diagnostic mirror codec patch context changed: $Name (old=$oldCount new=$newCount)"
    }
    return $Text.Replace($Before, $After)
}

# This is deliberately the final recipe, after the pinned HEVC/role/control
# patches. Their exact preimage hashes and default behavior stay unchanged.
$edits = @()
$globalPath = Join-Path $source 'AirPlayServerLib/lib/global.h'
$global = [IO.File]::ReadAllText($globalPath).Replace("`r`n", "`n")
$globalBefore = '#define GLOBAL_FEATURES_2 0x00000400U'
$globalAfter = @'
/* IPHONE_MIRROR_DIAGNOSTIC_MIRROR_CODEC_POLICY */
#include "airplay-mirror-codec-policy.h"
#undef GLOBAL_FEATURES_1
#define GLOBAL_FEATURES_1 (iphone_mirror_mirror_features_low())
#define GLOBAL_FEATURES_2 (iphone_mirror_mirror_features_high())
'@ -replace '\r\n', "`n"
$global = Replace-ExactlyOnce $global $globalBefore $globalAfter.TrimEnd("`n") 'global mirror feature word'
$edits += @{ Path = $globalPath; Text = $global }

$infoPath = Join-Path $source 'AirPlayServerLib/lib/raop_handlers.h'
$info = [IO.File]::ReadAllText($infoPath).Replace("`r`n", "`n")
$infoBefore = '0x5A7FFE73ULL : 0x4005A7FFEE6ULL;'
$infoAfter = @'
0x5A7FFE73ULL : (((unsigned long long)GLOBAL_FEATURES_2 << 32) | GLOBAL_FEATURES_1);
'@ -replace '\r\n', "`n"
$info = Replace-ExactlyOnce $info $infoBefore $infoAfter.TrimEnd("`n") '/info mode-specific features'
if (-not $info.Contains('IPHONE_MIRROR_NO_COMBINED_HANDOFF') -or
    -not $info.Contains('IPHONE_MIRROR_MEDIA_NO_DISPLAY')) {
    throw 'Apply the complete AirPlay role/control recipes before diagnostic codec policy.'
}
$infoBefore = @'
	const char *data;
	int datalen;
	data = http_request_get_data(request, &datalen);

	// Binary plist with display capabilities
'@
$infoAfter = @'
	(void)conn;
	(void)request;

	// Binary plist with display capabilities
'@
$info = Replace-ExactlyOnce $info $infoBefore $infoAfter '/info reuse on event channel'
$infoBefore = "`tif (capability_root) plist_to_bin(capability_root, response_data, &encoded_len);"
$infoAfter = @'
	if (capability_root && iphone_mirror_diagnostic_video_only_capabilities()) {
		plist_dict_remove_item(capability_root, "audioFormats");
		plist_dict_remove_item(capability_root, "audioLatencies");
	}
	if (capability_root) plist_to_bin(capability_root, response_data, &encoded_len);
'@
$info = Replace-ExactlyOnce $info $infoBefore $infoAfter 'video-only /info audio capability removal'
$setup = "static void`nraop_handler_setup("
$info = Replace-ExactlyOnce $info $setup ('#include "airplay-audio-event-test.h"' + "`n`n" + $setup) 'audio event functions'
$eventSetupBefore = @'
		if (deviceId != NULL) {
			free(deviceId); deviceId = NULL;
		}
	}
	else if (stream_type == 110) { // mirror stream setup
'@
$eventSetupAfter = @'
		if (deviceId != NULL) {
			free(deviceId); deviceId = NULL;
		}
		if (conn->raop_rtp_mirror && iphone_mirror_diagnostic_mirror_audio_event()) {
			const unsigned short event_port = iphone_mirror_event_start(conn);
			if (event_port) {
				plist_t setup = plist_new_dict();
				plist_dict_set_item(setup, "eventPort", plist_new_uint(event_port));
				uint32_t encoded_length = 0;
				plist_to_bin(setup, response_data, &encoded_length);
				plist_free(setup);
				if (*response_data) {
					*response_datalen = (int)encoded_length;
					http_response_add_header(response, "Content-Type", "application/x-apple-binary-plist");
				}
			}
		}
	}
	else if (stream_type == 110) { // mirror stream setup
'@
$info = Replace-ExactlyOnce $info $eventSetupBefore $eventSetupAfter 'initial audio event port'
$legacyEventPort = 'plist_t event_port_node = plist_new_uint(conn->raop->port);'
$diagnosticEventPort = 'plist_t event_port_node = plist_new_uint(iphone_mirror_diagnostic_mirror_audio_event() ? iphone_mirror_event_start(conn) : conn->raop->port);'
$info = Replace-ExactlyOnce $info $legacyEventPort $diagnosticEventPort 'mirror event port'
$teardownBefore = "`tif (type == 96) {"
$teardownAfter = @'
	if (iphone_mirror_diagnostic_mirror_audio_event())
		logger_log(conn->raop->logger, LOGGER_INFO,
			"IPHONE_MIRROR_VIDEO_DIAG 40 %d 5 %d 0 0 0",
			iphone_mirror_diagnostic_mirror_audio_event(), (int)type);
	if (type == 96) {
'@
$info = Replace-ExactlyOnce $info $teardownBefore $teardownAfter 'audio/video teardown observation'
$edits += @{ Path = $infoPath; Text = $info }

$raopPath = Join-Path $source 'AirPlayServerLib/lib/raop.c'
$raop = [IO.File]::ReadAllText($raopPath).Replace("`r`n", "`n")
$raop = Replace-ExactlyOnce $raop 'int mirror_started;' "int mirror_started;`n`tstruct iphone_mirror_event_s *audio_event;" 'per-connection audio event state'
$raop = Replace-ExactlyOnce $raop 'free(conn->local);' "iphone_mirror_event_stop(conn);`n`tfree(conn->local);" 'stop and join audio event listener'
$edits += @{ Path = $raopPath; Text = $raop }

$wrapperHeaderPath = Join-Path $source 'airplay2dll/FgAirplayServer.h'
$wrapperHeader = [IO.File]::ReadAllText($wrapperHeaderPath).Replace("`r`n", "`n")
$wrapperHeader = Replace-ExactlyOnce $wrapperHeader 'void stop();' @'
void stop();
	bool setAudioDiscoveryWithdrawn(bool withdrawn);
'@ 'audio discovery API'
$wrapperHeader = Replace-ExactlyOnce $wrapperHeader 'dnssd_t*' @'
std::string discoveryName;
	unsigned short discoveryRaopPort{};
	unsigned short discoveryAirplayPort{};
	char discoveryAddress[6]{};
	dnssd_t*
'@ 'audio discovery snapshot'
$edits += @{ Path = $wrapperHeaderPath; Text = $wrapperHeader }

$wrapperPath = Join-Path $source 'airplay2dll/src/FgAirplayServer.cpp'
$wrapper = [IO.File]::ReadAllText($wrapperPath).Replace("`r`n", "`n")
$wrapper = Replace-ExactlyOnce $wrapper 'm_pDnsSd = dnssd_init(&ret);' @'
discoveryName = serverName;
		discoveryRaopPort = raop_port;
		discoveryAirplayPort = airplay_port;
		memcpy(discoveryAddress, hwaddr, sizeof(discoveryAddress));
		m_pDnsSd = dnssd_init(&ret);
'@ 'save DNS-SD registration values'
$stopSignature = "void FgAirplayServer::stop()"
$discoveryMethod = @'
// Called by the host's receiver-control loop, alongside start/stop.
bool FgAirplayServer::setAudioDiscoveryWithdrawn(bool withdrawn)
{
	if (!m_pDnsSd || discoveryName.empty()) return false;
	using RecordsReleased = BOOL (WINAPI*)();
	const auto shim = GetModuleHandleW(L"dnssd.dll");
	const auto released = shim ? reinterpret_cast<RecordsReleased>(
		GetProcAddress(shim, "IPhoneMirrorAirPlayRecordsReleased")) : nullptr;
	if (!released) return false;

	dnssd_unregister_airplay(m_pDnsSd);
	dnssd_unregister_raop(m_pDnsSd);
	const auto started = GetTickCount64();
	while (!released() && GetTickCount64() - started < 1500) Sleep(15);
	const bool drained = released() != FALSE;
	const auto apply = drained && SetEnvironmentVariableW(
		L"IPHONE_MIRROR_DIAGNOSTIC_RAOP_WITHDRAWN", withdrawn ? L"1" : nullptr);
	if (!apply) SetEnvironmentVariableW(L"IPHONE_MIRROR_DIAGNOSTIC_RAOP_WITHDRAWN", nullptr);
	const bool publishAudio = !apply || !withdrawn;
	const bool audio = !publishAudio || dnssd_register_raop(m_pDnsSd, discoveryName.c_str(),
		discoveryRaopPort, discoveryAddress, sizeof(discoveryAddress), 0) > 0;
	const auto video = dnssd_register_airplay(m_pDnsSd, discoveryName.c_str(),
		discoveryAirplayPort, discoveryAddress, sizeof(discoveryAddress));
	return apply && audio && video == 0;
}

'@
$wrapper = Replace-ExactlyOnce $wrapper $stopSignature ($discoveryMethod + "`n" + $stopSignature) 'audio discovery withdrawal and restore'
$edits += @{ Path = $wrapperPath; Text = $wrapper }

$exportsPath = Join-Path $source 'airplay2dll/src/Airplay2Export.cpp'
$exports = [IO.File]::ReadAllText($exportsPath).Replace("`r`n", "`n")
$exportsAnchor = 'float fgServerScale(void* handle, float fRatio)'
$exports = Replace-ExactlyOnce $exports $exportsAnchor (@'
extern "C" __declspec(dllexport) int fgServerSetAudioDiscoveryWithdrawn(void* handle, int withdrawn)
{
	return handle && static_cast<FgAirplayServer*>(handle)->setAudioDiscoveryWithdrawn(withdrawn != 0);
}

'@ + "`n" + $exportsAnchor) 'export audio discovery update'
$edits += @{ Path = $exportsPath; Text = $exports }

$mirrorPath = Join-Path $source 'AirPlayServerLib/lib/raop_rtp_mirror.c'
$mirror = [IO.File]::ReadAllText($mirrorPath).Replace("`r`n", "`n")
$mirror = Replace-ExactlyOnce $mirror '#include "airplay-hevc-config.h"' `
    "#include `"airplay-hevc-config.h`"`n#include `"airplay-mirror-codec-policy.h`"" 'mirror codec policy include'
$mirrorBefore = '                    hevc = next_hevc;'
$mirrorAfter = @'
                    hevc = next_hevc;
                    /* IPHONE_MIRROR_DIAGNOSTIC_CODEC_SELECTION: only fixed
                     * codec/flag values and geometry; no payload or identity. */
                    logger_log(mirror->logger, LOGGER_INFO,
                        "IPHONE_MIRROR_VIDEO_DIAG 33 %d %d %d %d %d %d",
                        hevc, iphone_mirror_diagnostic_mirror_avc_only(),
                        isfinite(sw) && sw > 0 && sw <= 16384 ? (int)sw : 0,
                        isfinite(sh) && sh > 0 && sh <= 16384 ? (int)sh : 0,
                        isfinite(ow) && ow > 0 && ow <= 16384 ? (int)ow : 0,
                        isfinite(oh) && oh > 0 && oh <= 16384 ? (int)oh : 0);
'@ -replace '\r\n', "`n"
$mirror = Replace-ExactlyOnce $mirror $mirrorBefore $mirrorAfter.TrimEnd("`n") 'accepted codec diagnostic'
$cadenceBefore = '    int have_pts = 0, invalid_frames = 0, hevc = 0;'
$cadenceAfter = @'
    int have_pts = 0, invalid_frames = 0, hevc = 0;
    uint64_t cadence_start = 0, cadence_media_start = 0, cadence_previous = 0;
    uint64_t cadence_processing_us = 0, cadence_peak_us = 0;
    int cadence_frames = 0, cadence_long_gaps = 0;
'@ -replace '\r\n', "`n"
$mirror = Replace-ExactlyOnce $mirror $cadenceBefore $cadenceAfter.TrimEnd("`n") 'cadence state'
$processBefore = @'
            if (mirror->callbacks.video_process)
                mirror->callbacks.video_process(mirror->callbacks.cls, &frame, mirror->remoteName, mirror->remoteDeviceId);
'@ -replace '\r\n', "`n"
$processAfter = @'
            /* Observe delivery and synchronous decode separately. Sender time
             * is the raw unsigned 32.32 timestamp; only relative gaps leave
             * this thread. Reset on out-of-order timestamps and reconnects. */
            const uint64_t arrival = now_us();
            if (!cadence_start || arrival < cadence_start || timestamp < cadence_previous) {
                cadence_start = arrival;
                cadence_media_start = timestamp;
                cadence_previous = timestamp;
                cadence_processing_us = cadence_peak_us = 0;
                cadence_frames = cadence_long_gaps = 0;
            }
            if (timestamp - cadence_previous > 0x06666666ULL) ++cadence_long_gaps; /* 25 ms */
            cadence_previous = timestamp;
            const uint64_t wall_us = arrival - cadence_start;
            if (wall_us >= 1000000) {
                const uint64_t media_delta = timestamp - cadence_media_start;
                const uint64_t media_ms = (media_delta >> 32) * 1000 +
                    ((media_delta & 0xffffffffULL) * 1000 >> 32);
                logger_log(mirror->logger, LOGGER_INFO,
                    "IPHONE_MIRROR_VIDEO_DIAG 34 %d %d %d %d %d %d",
                    cadence_frames, (int)(wall_us > 60000000 ? 60000 : wall_us / 1000),
                    (int)(media_ms > 60000 ? 60000 : media_ms), cadence_long_gaps,
                    (int)(cadence_processing_us > 60000000 ? 60000 : cadence_processing_us / 1000),
                    (int)(cadence_peak_us > 60000000 ? 60000 : cadence_peak_us / 1000));
                cadence_start = arrival;
                cadence_media_start = timestamp;
                cadence_processing_us = cadence_peak_us = 0;
                cadence_frames = cadence_long_gaps = 0;
            }
            const uint64_t process_start = now_us();
            if (mirror->callbacks.video_process)
                mirror->callbacks.video_process(mirror->callbacks.cls, &frame, mirror->remoteName, mirror->remoteDeviceId);
            const uint64_t done = now_us();
            const uint64_t processing_us = done >= process_start ? done - process_start : 0;
            cadence_processing_us += processing_us;
            if (processing_us > cadence_peak_us) cadence_peak_us = processing_us;
            ++cadence_frames;
'@ -replace '\r\n', "`n"
$mirror = Replace-ExactlyOnce $mirror $processBefore.TrimEnd("`n") $processAfter.TrimEnd("`n") 'encoded cadence diagnostic'
$mirror = Replace-ExactlyOnce $mirror '        have_pts = 0; pts_base = 0;' `
    "        have_pts = 0; pts_base = 0;`n        cadence_start = 0;" 'cadence reconnect reset'
$edits += @{ Path = $mirrorPath; Text = $mirror }

# Validate every preimage first; a changed later source must not leave the
# earlier files only partly upgraded. Restore all bytes on any write failure.
$backups = @($edits | ForEach-Object { @{ Path = $_.Path; Bytes = [IO.File]::ReadAllBytes($_.Path) } })
$headers = @('airplay-mirror-codec-policy.h', 'airplay-audio-event-test.h') | ForEach-Object {
    $path = Join-Path $source "AirPlayServerLib/lib/$_"
    @{ Name = $_; Path = $path; Backup = if (Test-Path -LiteralPath $path) { [IO.File]::ReadAllBytes($path) } else { $null } }
}
try {
    foreach ($header in $headers) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $header.Name) -Destination $header.Path -Force
    }
    foreach ($edit in $edits) { [IO.File]::WriteAllText($edit.Path, $edit.Text, $utf8) }
} catch {
    foreach ($backup in $backups) { [IO.File]::WriteAllBytes($backup.Path, $backup.Bytes) }
    foreach ($header in $headers) {
        if ($null -ne $header.Backup) { [IO.File]::WriteAllBytes($header.Path, $header.Backup) }
        else { Remove-Item -LiteralPath $header.Path -ErrorAction SilentlyContinue }
    }
    throw
}
Write-Host 'Applied opt-in mirror AVC-only and no-audio capability diagnostics.'

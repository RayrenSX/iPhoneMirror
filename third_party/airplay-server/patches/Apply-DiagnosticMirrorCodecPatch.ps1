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
$edits += @{ Path = $infoPath; Text = $info }

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
$headerPath = Join-Path $source 'AirPlayServerLib/lib/airplay-mirror-codec-policy.h'
$headerExisted = Test-Path -LiteralPath $headerPath
$headerBackup = if ($headerExisted) { [IO.File]::ReadAllBytes($headerPath) } else { $null }
try {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'airplay-mirror-codec-policy.h') -Destination $headerPath -Force
    foreach ($edit in $edits) { [IO.File]::WriteAllText($edit.Path, $edit.Text, $utf8) }
} catch {
    foreach ($backup in $backups) { [IO.File]::WriteAllBytes($backup.Path, $backup.Bytes) }
    if ($headerExisted) { [IO.File]::WriteAllBytes($headerPath, $headerBackup) }
    else { Remove-Item -LiteralPath $headerPath -ErrorAction SilentlyContinue }
    throw
}
Write-Host 'Applied diagnostic-only mirror AVC capability policy; default and media profiles unchanged.'

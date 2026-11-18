[CmdletBinding()]
param([Parameter(Mandatory)][string]$SourceRoot)
$ErrorActionPreference = 'Stop'
$path = Join-Path $SourceRoot 'AirPlayServerLib/lib/raop_rtp_mirror.c'
$text = [IO.File]::ReadAllText($path).Replace("`r`n", "`n")
$upstreamGrace = '#define MIRROR_RECONNECT_GRACE_MS 120000'
$localGrace = '#define MIRROR_RECONNECT_GRACE_MS 2000'
if ($text.Contains($localGrace) -and $text.Contains('Mirror TCP keepalive configured')) { return }
function Replace-Once([string]$Original, [string]$Replacement) {
    $Original = $Original.Replace("`r`n", "`n")
    $Replacement = $Replacement.Replace("`r`n", "`n")
    if ([regex]::Matches($script:text, [regex]::Escape($Original)).Count -ne 1) {
        throw "AirPlay mirror source anchor no longer matches: $Original"
    }
    $script:text = $script:text.Replace($Original, $Replacement)
}
Replace-Once $upstreamGrace $localGrace
Replace-Once '#include <mstcpip.h>' "#include <mstcpip.h>`n#include <ws2tcpip.h>"
Replace-Once 'mirror_enable_keepalive(mirror_socket_t socket_fd)' `
    'mirror_enable_keepalive(mirror_socket_t socket_fd, logger_t *logger)'
Replace-Once 'mirror_enable_keepalive(stream);' 'mirror_enable_keepalive(stream, mirror->logger);'
Replace-Once @'
        settings.keepalivetime = 5000;
        settings.keepaliveinterval = 1000;
        WSAIoctl((SOCKET)socket_fd, SIO_KEEPALIVE_VALS,
            &settings, sizeof(settings), NULL, 0, &bytes_returned, NULL, NULL);
'@ @'
        const DWORD idle_seconds = 2, interval_seconds = 1, probe_count = 3;
        if (setsockopt(socket_fd, IPPROTO_TCP, TCP_KEEPIDLE,
                (const char *)&idle_seconds, sizeof(idle_seconds)) == 0 &&
            setsockopt(socket_fd, IPPROTO_TCP, TCP_KEEPINTVL,
                (const char *)&interval_seconds, sizeof(interval_seconds)) == 0 &&
            setsockopt(socket_fd, IPPROTO_TCP, TCP_KEEPCNT,
                (const char *)&probe_count, sizeof(probe_count)) == 0) {
            logger_log(logger, LOGGER_INFO, "Mirror TCP keepalive configured");
        } else {
            settings.keepalivetime = idle_seconds * 1000;
            settings.keepaliveinterval = interval_seconds * 1000;
            WSAIoctl(socket_fd, SIO_KEEPALIVE_VALS,
                &settings, sizeof(settings), NULL, 0, &bytes_returned, NULL, NULL);
            logger_log(logger, LOGGER_INFO, "Mirror TCP keepalive fallback");
        }
'@
[IO.File]::WriteAllText($path, $text, [Text.UTF8Encoding]::new($true))

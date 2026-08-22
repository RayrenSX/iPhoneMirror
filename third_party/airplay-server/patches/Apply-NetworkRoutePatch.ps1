[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$SourceRoot
)

$ErrorActionPreference = 'Stop'
$SourceRoot = (Resolve-Path -LiteralPath $SourceRoot).Path
$Encoding = [Text.UTF8Encoding]::new($false)
$Files = @{
    NetUtilsHeader = Join-Path $SourceRoot 'AirPlayServerLib\lib\netutils.h'
    NetUtils = Join-Path $SourceRoot 'AirPlayServerLib\lib\netutils.c'
    Httpd = Join-Path $SourceRoot 'AirPlayServerLib\lib\httpd.c'
    RaopHeader = Join-Path $SourceRoot 'AirPlayServerLib\lib\raop_rtp.h'
    Raop = Join-Path $SourceRoot 'AirPlayServerLib\lib\raop_rtp.c'
    MirrorHeader = Join-Path $SourceRoot 'AirPlayServerLib\lib\raop_rtp_mirror.h'
    Mirror = Join-Path $SourceRoot 'AirPlayServerLib\lib\raop_rtp_mirror.c'
    Handlers = Join-Path $SourceRoot 'AirPlayServerLib\lib\raop_handlers.h'
}
foreach ($File in $Files.Values) {
    if (-not (Test-Path -LiteralPath $File -PathType Leaf)) {
        throw "AirPlayServer network-route source is missing: $File"
    }
}

function Read-Source([string]$Path) {
    [IO.File]::ReadAllText($Path, $Encoding)
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

$Marker = 'IPHONE_MIRROR_BOUND_MEDIA_SOCKETS'
$NetUtilsHeader = Read-Source $Files.NetUtilsHeader
if (-not $NetUtilsHeader.Contains($Marker)) {
    $NewLine = if ($NetUtilsHeader.Contains("`r`n")) { "`r`n" } else { "`n" }
    $Needle = 'int netutils_init_socket(unsigned short *port, int use_ipv6, int use_udp);'
    $Replacement = '/* IPHONE_MIRROR_BOUND_MEDIA_SOCKETS */' + $NewLine +
        'int netutils_init_socket(unsigned short *port, int use_ipv6, int use_udp,' +
        $NewLine + '                         const unsigned char *local, int locallen);'
    $NetUtilsHeader = Replace-Once $NetUtilsHeader $Needle $Replacement `
        'AirPlay socket helper declaration'
    Write-Source $Files.NetUtilsHeader $NetUtilsHeader
}

$NetUtils = Read-Source $Files.NetUtils
if (-not $NetUtils.Contains($Marker)) {
    $NewLine = if ($NetUtils.Contains("`r`n")) { "`r`n" } else { "`n" }
    $Signature = @'
int
netutils_init_socket(unsigned short *port, int use_ipv6, int use_udp)
'@ -replace "`r?`n", $NewLine
    $BoundSignature = @'
/* IPHONE_MIRROR_BOUND_MEDIA_SOCKETS: secondary AirPlay sockets use the local
 * address of the accepted control connection so proxy/TUN routes cannot move
 * their listeners or outbound timing packets onto a virtual adapter. */
int
netutils_init_socket(unsigned short *port, int use_ipv6, int use_udp,
                     const unsigned char *local, int locallen)
'@ -replace "`r?`n", $NewLine
    $NetUtils = Replace-Once $NetUtils $Signature $BoundSignature `
        'AirPlay socket helper definition'

    $Ipv6Any = "`t`tsin6ptr->sin6_addr = in6addr_any;"
    $Ipv6Bound = @'
		if (local != NULL && locallen == sizeof(sin6ptr->sin6_addr))
			memcpy(&sin6ptr->sin6_addr, local, sizeof(sin6ptr->sin6_addr));
		else
			sin6ptr->sin6_addr = in6addr_any;
'@ -replace "`r?`n", $NewLine
    $NetUtils = Replace-Once $NetUtils $Ipv6Any $Ipv6Bound.TrimEnd("`r", "`n") `
        'AirPlay IPv6 bind address'

    $Ipv4Any = "`t`tsinptr->sin_addr.s_addr = INADDR_ANY;"
    $Ipv4Bound = @'
		if (local != NULL && locallen == sizeof(sinptr->sin_addr.s_addr))
			memcpy(&sinptr->sin_addr.s_addr, local, sizeof(sinptr->sin_addr.s_addr));
		else
			sinptr->sin_addr.s_addr = INADDR_ANY;
'@ -replace "`r?`n", $NewLine
    $NetUtils = Replace-Once $NetUtils $Ipv4Any $Ipv4Bound.TrimEnd("`r", "`n") `
        'AirPlay IPv4 bind address'
    Write-Source $Files.NetUtils $NetUtils
}

$Httpd = Read-Source $Files.Httpd
if (-not $Httpd.Contains('netutils_init_socket(port, 0, 0, NULL, 0)')) {
    $Httpd = Replace-Once $Httpd 'netutils_init_socket(port, 0, 0)' `
        'netutils_init_socket(port, 0, 0, NULL, 0)' `
        'AirPlay HTTP wildcard listener'
    # This listener is disabled upstream but must still compile with the new signature.
    $Httpd = Replace-Once $Httpd 'netutils_init_socket(port, 1, 0)' `
        'netutils_init_socket(port, 1, 0, NULL, 0)' `
        'AirPlay HTTP IPv6 wildcard listener'
    Write-Source $Files.Httpd $Httpd
}

$RaopHeader = Read-Source $Files.RaopHeader
if (-not $RaopHeader.Contains('const unsigned char *local, int locallen')) {
    $NewLine = if ($RaopHeader.Contains("`r`n")) { "`r`n" } else { "`n" }
    $Needle = 'raop_rtp_t *raop_rtp_init(logger_t *logger, raop_callbacks_t *callbacks, const unsigned char *remote, int remotelen,'
    $Replacement = 'raop_rtp_t *raop_rtp_init(logger_t *logger, raop_callbacks_t *callbacks, const unsigned char *local, int locallen,' +
        $NewLine + '                          const unsigned char *remote, int remotelen,'
    $RaopHeader = Replace-Once $RaopHeader $Needle $Replacement `
        'RAOP local-address declaration'
    Write-Source $Files.RaopHeader $RaopHeader
}

$Raop = Read-Source $Files.Raop
if (-not $Raop.Contains($Marker)) {
    $NewLine = if ($Raop.Contains("`r`n")) { "`r`n" } else { "`n" }
    $Fields = "    /* Remote address as sockaddr */${NewLine}    struct sockaddr_storage remote_saddr;"
    $BoundFields = @'
    /* Local address selected by the accepted AirPlay control connection. */
    unsigned char local_address[16];
    int local_address_len;

    /* Remote address as sockaddr */
    struct sockaddr_storage remote_saddr;
'@ -replace "`r?`n", $NewLine
    $Raop = Replace-Once $Raop $Fields $BoundFields.TrimEnd("`r", "`n") `
        'RAOP local-address storage'

    $Signature = 'raop_rtp_init(logger_t *logger, raop_callbacks_t *callbacks, const unsigned char *remote, int remotelen,'
    $BoundSignature = 'raop_rtp_init(logger_t *logger, raop_callbacks_t *callbacks, const unsigned char *local, int locallen,' +
        $NewLine + '              const unsigned char *remote, int remotelen,'
    $Raop = Replace-Once $Raop $Signature $BoundSignature `
        'RAOP local-address definition'

    $InitNeedle = "    raop_rtp->timing_rport = timing_rport;${NewLine}"
    $InitReplacement = @'
    raop_rtp->timing_rport = timing_rport;
    if (local == NULL || (locallen != 4 && locallen != 16)) {
        free(raop_rtp);
        return NULL;
    }
    memcpy(raop_rtp->local_address, local, locallen);
    raop_rtp->local_address_len = locallen;

'@ -replace "`r?`n", $NewLine
    $Raop = Replace-Once $Raop $InitNeedle $InitReplacement `
        'RAOP local-address initialization'

    foreach ($Socket in @('csock', 'tsock', 'dsock')) {
        $Port = @{ csock = 'cport'; tsock = 'tport'; dsock = 'dport' }[$Socket]
        $Needle = "    $Socket = netutils_init_socket(&$Port, use_ipv6, 1);"
        $Replacement = "    $Socket = netutils_init_socket(&$Port, use_ipv6, 1,${NewLine}" +
            '        raop_rtp->local_address, raop_rtp->local_address_len);'
        $Raop = Replace-Once $Raop $Needle $Replacement "RAOP $Socket binding"
    }
    $MarkerPoint = '    if (csock == -1 || tsock == -1 || dsock == -1) {'
    $MarkerText = "    logger_log(raop_rtp->logger, LOGGER_INFO,${NewLine}" +
        '        "IPHONE_MIRROR_BOUND_MEDIA_SOCKETS kind=raop local_bytes=%d",' +
        $NewLine +
        "        raop_rtp->local_address_len);${NewLine}${NewLine}" + $MarkerPoint
    $Raop = Replace-Once $Raop $MarkerPoint $MarkerText `
        'RAOP bound-socket marker'
    Write-Source $Files.Raop $Raop
}

$MirrorHeader = Read-Source $Files.MirrorHeader
if (-not $MirrorHeader.Contains('const unsigned char *local, int locallen')) {
    $NewLine = if ($MirrorHeader.Contains("`r`n")) { "`r`n" } else { "`n" }
    $Needle = 'raop_rtp_mirror_t *raop_rtp_mirror_init(logger_t *logger, raop_callbacks_t *callbacks, const unsigned char *remote, int remotelen,'
    $Replacement = 'raop_rtp_mirror_t *raop_rtp_mirror_init(logger_t *logger, raop_callbacks_t *callbacks, const unsigned char *local, int locallen,' +
        $NewLine + "`tconst unsigned char *remote, int remotelen,"
    $MirrorHeader = Replace-Once $MirrorHeader $Needle $Replacement `
        'mirror local-address declaration'
    Write-Source $Files.MirrorHeader $MirrorHeader
}

$Mirror = Read-Source $Files.Mirror
if (-not $Mirror.Contains($Marker)) {
    $NewLine = if ($Mirror.Contains("`r`n")) { "`r`n" } else { "`n" }
    $Fields = "    /* Remote address as sockaddr */${NewLine}    struct sockaddr_storage remote_saddr;"
    $BoundFields = @'
    /* Local address selected by the accepted AirPlay control connection. */
    unsigned char local_address[16];
    int local_address_len;

    /* Remote address as sockaddr */
    struct sockaddr_storage remote_saddr;
'@ -replace "`r?`n", $NewLine
    $Mirror = Replace-Once $Mirror $Fields $BoundFields.TrimEnd("`r", "`n") `
        'mirror local-address storage'

    $Signature = 'raop_rtp_mirror_t *raop_rtp_mirror_init(logger_t *logger, raop_callbacks_t *callbacks, const unsigned char *remote, int remotelen,'
    $BoundSignature = 'raop_rtp_mirror_t *raop_rtp_mirror_init(logger_t *logger, raop_callbacks_t *callbacks, const unsigned char *local, int locallen,' +
        $NewLine + "`t                                    const unsigned char *remote, int remotelen,"
    $Mirror = Replace-Once $Mirror $Signature $BoundSignature `
        'mirror local-address definition'

    $InitNeedle = "    raop_rtp_mirror->mirror_timing_rport = timing_rport;${NewLine}"
    $InitReplacement = @'
    raop_rtp_mirror->mirror_timing_rport = timing_rport;
    if (local == NULL || (locallen != 4 && locallen != 16)) {
        free(raop_rtp_mirror);
        return NULL;
    }
    memcpy(raop_rtp_mirror->local_address, local, locallen);
    raop_rtp_mirror->local_address_len = locallen;

'@ -replace "`r?`n", $NewLine
    $Mirror = Replace-Once $Mirror $InitNeedle $InitReplacement `
        'mirror local-address initialization'

    foreach ($Socket in @('dsock', 'tsock')) {
        $Port = @{ dsock = 'dport'; tsock = 'tport' }[$Socket]
        $Udp = if ($Socket -eq 'tsock') { 1 } else { 0 }
        $Needle = "    $Socket = netutils_init_socket(&$Port, use_ipv6, $Udp);"
        $Replacement = "    $Socket = netutils_init_socket(&$Port, use_ipv6, $Udp,${NewLine}" +
            '        raop_rtp_mirror->local_address, raop_rtp_mirror->local_address_len);'
        $Mirror = Replace-Once $Mirror $Needle $Replacement "mirror $Socket binding"
    }
    $MarkerPoint = '    if (dsock == -1 || tsock == -1) {'
    $MarkerText = "    logger_log(raop_rtp_mirror->logger, LOGGER_INFO,${NewLine}" +
        '        "IPHONE_MIRROR_BOUND_MEDIA_SOCKETS kind=mirror local_bytes=%d",' +
        $NewLine +
        "        raop_rtp_mirror->local_address_len);${NewLine}${NewLine}" + $MarkerPoint
    $Mirror = Replace-Once $Mirror $MarkerPoint $MarkerText `
        'mirror bound-socket marker'
    Write-Source $Files.Mirror $Mirror
}

$Handlers = Read-Source $Files.Handlers
if (-not $Handlers.Contains('raop_rtp_init(conn->raop->logger, &conn->raop->callbacks, conn->local, conn->locallen,')) {
    $Handlers = Replace-Once $Handlers `
        'raop_rtp_init(conn->raop->logger, &conn->raop->callbacks, conn->remote, conn->remotelen,' `
        'raop_rtp_init(conn->raop->logger, &conn->raop->callbacks, conn->local, conn->locallen, conn->remote, conn->remotelen,' `
        'RAOP control-route propagation'
}
if (-not $Handlers.Contains('raop_rtp_mirror_init(conn->raop->logger, &conn->raop->callbacks, conn->local, conn->locallen,')) {
    $Handlers = Replace-Once $Handlers `
        'raop_rtp_mirror_init(conn->raop->logger, &conn->raop->callbacks, conn->remote, conn->remotelen,' `
        'raop_rtp_mirror_init(conn->raop->logger, &conn->raop->callbacks, conn->local, conn->locallen, conn->remote, conn->remotelen,' `
        'mirror control-route propagation'
}
Write-Source $Files.Handlers $Handlers

# Preserve the upstream IPv6 decision. The route-binding change only changes
# the local bind address; forcing these media sockets to IPv4 breaks IPv6
# AirPlay control sessions after SETUP.
foreach ($Path in @($Files.Raop, $Files.Mirror)) {
    $Content = Read-Source $Path
    $MarkerText = 'IPHONE_MIRROR_PRESERVE_IPV6_MEDIA'
    $Updated = $Content -replace '(?m)^\s*use_ipv6\s*=\s*0;\s*$', ''
    if ($Updated -notmatch 'logger_log\([^\r\n]+"IPHONE_MIRROR_PRESERVE_IPV6_MEDIA"') {
        $needle = if ([IO.Path]::GetFileName($Path) -eq 'raop_rtp.c') {
            '    if (raop_rtp->remote_saddr.ss_family == AF_INET6) {'
        }
        else {
            '    if (raop_rtp_mirror->remote_saddr.ss_family == AF_INET6) {'
        }
        $count = [regex]::Matches($Updated, [regex]::Escape($needle)).Count
        if ($count -ne 1) {
            throw "IPv6 preservation insertion point changed in $Path; found $count."
        }
        $log = if ([IO.Path]::GetFileName($Path) -eq 'raop_rtp.c') {
            '    logger_log(raop_rtp->logger, LOGGER_DEBUG, "IPHONE_MIRROR_PRESERVE_IPV6_MEDIA");'
        }
        else {
            '    logger_log(raop_rtp_mirror->logger, LOGGER_DEBUG, "IPHONE_MIRROR_PRESERVE_IPV6_MEDIA");'
        }
        $Updated = $Updated.Replace($needle, "$log`n$needle")
    }
    if ($Updated -ne $Content) { Write-Source $Path $Updated }
}

foreach ($Required in @(
        'IPHONE_MIRROR_BOUND_MEDIA_SOCKETS',
        'IPHONE_MIRROR_BOUND_MEDIA_SOCKETS kind=raop',
        'IPHONE_MIRROR_BOUND_MEDIA_SOCKETS kind=mirror',
        'IPHONE_MIRROR_PRESERVE_IPV6_MEDIA')) {
    $Found = $Files.Values | Where-Object { (Read-Source $_).Contains($Required) }
    if (-not $Found) { throw "AirPlay route-binding marker is missing: $Required" }
}
Write-Host 'Applied control-route binding to AirPlay media sockets.' -ForegroundColor Green

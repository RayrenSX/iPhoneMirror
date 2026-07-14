[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$SourceRoot
)

$ErrorActionPreference = 'Stop'
$SourceRoot = (Resolve-Path -LiteralPath $SourceRoot).Path
$SourceFile = Join-Path $SourceRoot 'AirPlayServerLib\lib\raop_handlers.h'
if (-not (Test-Path -LiteralPath $SourceFile)) {
    throw "AirPlayServer handshake source is missing: $SourceFile"
}

$Encoding = [Text.Encoding]::Unicode
$Text = [IO.File]::ReadAllText($SourceFile, $Encoding)
$ArrayStart = $Text.IndexOf('char info[] = {', [StringComparison]::Ordinal)
if ($ArrayStart -lt 0) {
    throw 'AirPlayServer display capability array is missing.'
}
$ArrayEnd = $Text.IndexOf("`n`t};", $ArrayStart, [StringComparison]::Ordinal)
if ($ArrayEnd -lt 0) {
    throw 'AirPlayServer display capability array terminator is missing.'
}
$ArrayEnd += 4
$Capability = $Text.Substring($ArrayStart, $ArrayEnd - $ArrayStart)

$SourceValues = [ordered]@{
    '0x11, 0x05, 0xa0' = '0x11, 0x0b, 0x40' # 1440 -> 2880
    '0x10, 0x1e'       = '0x10, 0x3c'       # 30 -> 60 (shared by maxFPS/refreshRate)
    '0x11, 0x0d, 0x70' = '0x11, 0x14, 0x00' # 3440 -> 5120
}

$AlreadyPatched = $true
foreach ($Target in $SourceValues.Values) {
    if ($Capability.IndexOf($Target, [StringComparison]::Ordinal) -lt 0) {
        $AlreadyPatched = $false
        break
    }
}
if ($AlreadyPatched) {
    Write-Host 'AirPlay 5120x2880@60 base display capability is already applied.'
}
else {
    foreach ($Pair in $SourceValues.GetEnumerator()) {
        $Matches = [regex]::Matches($Capability, [regex]::Escape($Pair.Key)).Count
        if ($Matches -ne 1) {
            throw "AirPlayServer display capability no longer matches '$($Pair.Key)' exactly once (found $Matches)."
        }
        $Capability = $Capability.Replace($Pair.Key, $Pair.Value)
    }

    $Text = $Text.Substring(0, $ArrayStart) + $Capability + $Text.Substring($ArrayEnd)
    $Text = $Text.Replace(
        '// Binary plist with display capabilities: 1920x1080 @ 30fps (maxFPS=30, refreshRate=30)',
        '// Binary plist with display capabilities: 5120x2880 @ 60fps (maxFPS=60, refreshRate=60)')
}

$NewLine = if ($Text.Contains("`r`n")) { "`r`n" } else { "`n" }
$HelperMarker = 'iphone_mirror_capability_value'
if (-not $Text.Contains($HelperMarker)) {
    $HandlerNeedle = 'static void' + $NewLine + 'raop_handler_info'
    $HandlerIndex = $Text.IndexOf($HandlerNeedle, [StringComparison]::Ordinal)
    if ($HandlerIndex -lt 0) { throw 'AirPlay info handler marker is missing.' }
    $Helper = @'
static unsigned long
iphone_mirror_capability_value(const char *name, unsigned long fallback,
                               unsigned long minimum, unsigned long maximum)
{
	const char *text = getenv(name);
	char *end = NULL;
	unsigned long value;
	if (text == NULL || *text == '\0') return fallback;
	value = strtoul(text, &end, 10);
	return end != NULL && *end == '\0' && value >= minimum && value <= maximum ?
		value : fallback;
}

'@ -replace "`r?`n", $NewLine
    $Text = $Text.Substring(0, $HandlerIndex) + $Helper + $Text.Substring($HandlerIndex)
}

$ArrayStart = $Text.IndexOf('char info[] = {', [StringComparison]::Ordinal)
$ArrayEnd = $Text.IndexOf("`n`t};", $ArrayStart, [StringComparison]::Ordinal) + 4
$NextHandler = $NewLine + '}' + $NewLine + $NewLine + 'static void' + $NewLine +
    'raop_handler_pairsetup'
$ResponseEnd = $Text.IndexOf($NextHandler, $ArrayEnd, [StringComparison]::Ordinal)
if ($ResponseEnd -lt 0) { throw 'AirPlay info response block terminator is missing.' }
$ResponseStart = $Text.IndexOf($NewLine + "`tsize_t len = sizeof(info);", $ArrayEnd,
    [StringComparison]::Ordinal)
if ($ResponseStart -lt 0) {
    $ResponseStart = $Text.IndexOf($NewLine + "`tplist_t capability_root", $ArrayEnd,
        [StringComparison]::Ordinal)
}
if ($ResponseStart -lt 0 -or $ResponseStart -ge $ResponseEnd) {
    throw 'AirPlay info response block is missing.'
}
$DynamicResponse = @'
	plist_t capability_root = NULL;
	plist_from_bin(info, sizeof(info), &capability_root);
	plist_t displays = capability_root ?
		plist_dict_get_item(capability_root, "displays") : NULL;
	plist_t display = displays ? plist_array_get_item(displays, 0) : NULL;
	const char *receiver_name = getenv("IPHONE_MIRROR_AIRPLAY_NAME");
	unsigned long width = iphone_mirror_capability_value(
		"IPHONE_MIRROR_AIRPLAY_WIDTH", 5120, 320, 8192);
	unsigned long height = iphone_mirror_capability_value(
		"IPHONE_MIRROR_AIRPLAY_HEIGHT", 2880, 180, 8192);
	unsigned long fps = iphone_mirror_capability_value(
		"IPHONE_MIRROR_AIRPLAY_FPS", 60, 15, 120);
	if (capability_root && receiver_name && *receiver_name)
		plist_dict_set_item(capability_root, "name", plist_new_string(receiver_name));
	if (display) {
		plist_dict_set_item(display, "width", plist_new_uint(width));
		plist_dict_set_item(display, "height", plist_new_uint(height));
		plist_dict_set_item(display, "widthPixels", plist_new_uint(width));
		plist_dict_set_item(display, "heightPixels", plist_new_uint(height));
		plist_dict_set_item(display, "maxFPS", plist_new_uint(fps));
		plist_dict_set_item(display, "refreshRate", plist_new_uint(fps));
	}
	uint32_t encoded_len = 0;
	if (capability_root) plist_to_bin(capability_root, response_data, &encoded_len);
	if (capability_root) plist_free(capability_root);
	if (!*response_data) {
		encoded_len = sizeof(info);
		*response_data = malloc(encoded_len);
		if (*response_data) memcpy(*response_data, info, encoded_len);
	}
	if (*response_data) {
        http_response_add_header(response, "Content-Type", "application/x-apple-binary-plist");
		*response_datalen = encoded_len;
	}
'@ -replace "`r?`n", $NewLine
$Text = $Text.Substring(0, $ResponseStart) + $NewLine + $DynamicResponse.TrimEnd("`r", "`n") +
    $Text.Substring($ResponseEnd)
[IO.File]::WriteAllText($SourceFile, $Text, $Encoding)
Write-Host 'Applied runtime-selectable AirPlay display capability patch.'

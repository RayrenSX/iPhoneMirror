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
$NewLine = if ($Text.Contains("`r`n")) { "`r`n" } else { "`n" }
$Marker = 'IPHONE_MIRROR_DEVICE_INFO\t%s\t%s\t%s'
if ($Text.Contains($Marker)) {
    $FreeMarker = "`t`tif (model != NULL) { free(model); model = NULL; }"
    if (-not $Text.Contains($FreeMarker)) {
        $FreeNeedle = @(
            "`t`tif (deviceId != NULL) {",
            "`t`t`tlogger_log(conn->raop->logger, LOGGER_INFO,",
            "`t`t`t`t`"$Marker`",",
            "`t`t`t`tdeviceId, model ? model : `"`", osVersion ? osVersion : `"`");",
            "`t`t}"
        ) -join $NewLine
        $FreeReplacement = @(
            $FreeNeedle,
            $FreeMarker,
            "`t`tif (osVersion != NULL) { free(osVersion); osVersion = NULL; }"
        ) -join $NewLine
        $FreeIndex = $Text.IndexOf($FreeNeedle, [StringComparison]::Ordinal)
        if ($FreeIndex -lt 0) {
            throw 'AirPlayServer metadata patch is present but its cleanup context is missing.'
        }
        $Text = $Text.Substring(0, $FreeIndex) + $FreeReplacement +
            $Text.Substring($FreeIndex + $FreeNeedle.Length)
        [IO.File]::WriteAllText($SourceFile, $Text, $Encoding)
        Write-Host 'Upgraded AirPlay sender metadata patch cleanup.'
    }
    Write-Host 'AirPlay sender metadata patch is already applied.'
    return
}

$Needle = @(
    "`t`tchar* deviceId = NULL;",
    "`t`tplist_t device_id_node = plist_dict_get_item(root_node, `"deviceID`");",
    "`t`tplist_get_string_val(device_id_node, &deviceId);"
) -join $NewLine
$Replacement = @(
    $Needle,
    "`t`tchar* model = NULL;",
    "`t`tplist_t model_node = plist_dict_get_item(root_node, `"model`");",
    "`t`tplist_get_string_val(model_node, &model);",
    "`t`tchar* osVersion = NULL;",
    "`t`tplist_t os_version_node = plist_dict_get_item(root_node, `"osVersion`");",
    "`t`tplist_get_string_val(os_version_node, &osVersion);",
    "`t`tif (deviceId != NULL) {",
    "`t`t`tlogger_log(conn->raop->logger, LOGGER_INFO,",
    "`t`t`t`t`"$Marker`",",
    "`t`t`t`tdeviceId, model ? model : `"`", osVersion ? osVersion : `"`");",
    "`t`t}",
    "`t`tif (model != NULL) { free(model); model = NULL; }",
    "`t`tif (osVersion != NULL) { free(osVersion); osVersion = NULL; }"
) -join $NewLine

$First = $Text.IndexOf($Needle, [StringComparison]::Ordinal)
$Last = $Text.LastIndexOf($Needle, [StringComparison]::Ordinal)
if ($First -lt 0 -or $First -ne $Last) {
    throw 'AirPlayServer handshake source no longer matches the pinned patch context.'
}

$Patched = $Text.Substring(0, $First) + $Replacement +
    $Text.Substring($First + $Needle.Length)
[IO.File]::WriteAllText($SourceFile, $Patched, $Encoding)
Write-Host 'Applied AirPlay sender model and OS metadata patch.'

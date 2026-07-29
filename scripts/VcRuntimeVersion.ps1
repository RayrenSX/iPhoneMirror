function ConvertTo-VcRuntimeVersion {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string]$Value
    )

    $match = [regex]::Match($Value.Trim(), '^\d+(?:\.\d+){1,3}(?=[^\d.]|$)')
    if (-not $match.Success) {
        throw "Invalid Visual C++ runtime version: $Value"
    }
    return [Version]::Parse($match.Value)
}

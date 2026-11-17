# Keep the exact loose-file contract aligned with App.csproj. These source and
# notice files accompany the newly bundled control library and device artwork.
function Get-AdditionalThirdPartyNoticeFiles([string]$Workspace) {
    $groups = @(
        @{ Source = 'third_party/device-frames'; Target = 'licenses/device-frames'; Recurse = $false },
        @{ Source = 'third_party/airplay-control'; Target = 'Wireless/licenses/airplay-control'; Recurse = $true }
    )
    foreach ($group in $groups) {
        $source = [IO.Path]::GetFullPath((Join-Path $Workspace $group.Source))
        if (-not (Test-Path -LiteralPath $source -PathType Container)) { throw "Missing bundled notices: $source" }
        foreach ($file in @(Get-ChildItem -LiteralPath $source -File -Recurse:$group.Recurse)) {
            if ($file.Extension -in @('.dll', '.exe')) { continue }
            if (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Redirected notice source: $($file.FullName)" }
            $relative = [IO.Path]::GetRelativePath($source, $file.FullName)
            Join-Path $group.Target $relative
        }
    }
    'Wireless/licenses/NOTICE-AIRPLAY-CONTROL-BUILD.json'
}

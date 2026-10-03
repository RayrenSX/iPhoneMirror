[CmdletBinding()]
param([string]$DescriptorPath = (Join-Path $PSScriptRoot '../config/uxplay-component.json'))
$ErrorActionPreference = 'Stop'
$descriptor = Get-Content -LiteralPath $DescriptorPath -Raw | ConvertFrom-Json
$tag = if ($descriptor.release) { $descriptor.release } else { "v$($descriptor.version)" }
$name = "iPhoneMirror-UxPlay-v$($descriptor.version)-win-x64.zip"
$url = "https://github.com/RayrenSX/iPhoneMirror/releases/download/$tag/$name"
if ($descriptor.schema -ne 1 -or $descriptor.version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$' -or
    $tag -cnotin @("v$($descriptor.version)", "uxplay-v$($descriptor.version)") -or
    $descriptor.name -cne $name -or $descriptor.url -cne $url -or
    $descriptor.size -le 0 -or $descriptor.sha256 -notmatch '^[0-9a-fA-F]{64}$') {
    throw 'Invalid pinned UxPlay descriptor.'
}
# Intentionally unauthenticated: users cannot download private/draft assets.
try {
    $release = Invoke-RestMethod -Uri "https://api.github.com/repos/RayrenSX/iPhoneMirror/releases/tags/$tag" `
        -Headers @{ Accept = 'application/vnd.github+json'; 'User-Agent' = 'iPhoneMirror-component-verification' } `
        -TimeoutSec 30
} catch {
    if ([int]$_.Exception.Response.StatusCode -eq 404) {
        throw "UxPlay release $tag is not public. Publish the matching component before distributing this app. URL: $url"
    }
    # API rate limiting must not prevent verification of a public download.
    # In that case verify the complete public ZIP against the pinned hash.
    Write-Host 'Release API unavailable; verifying the public asset bytes instead.'
    $release = $null
}
if ($null -ne $release) {
    $assets = @($release.assets | Where-Object { $_.name -ceq $name })
    if ($release.draft -or $release.tag_name -cne $tag -or $assets.Count -ne 1 -or
        $assets[0].state -ne 'uploaded' -or $assets[0].browser_download_url -cne $url -or
        $assets[0].size -ne $descriptor.size -or $assets[0].digest -ine "sha256:$($descriptor.sha256)") {
        throw "Public UxPlay asset is missing or differs from the pinned descriptor: $url"
    }
}
Add-Type -AssemblyName System.Net.Http
$client = [Net.Http.HttpClient]::new()
$request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Get, $url)
if ($null -ne $release) { $request.Headers.Range = [Net.Http.Headers.RangeHeaderValue]::new(0, 0) }
$request.Headers.UserAgent.ParseAdd('iPhoneMirror-component-verification')
try {
    $client.Timeout = [TimeSpan]::FromSeconds(30)
    $response = $client.SendAsync($request, [Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
    try {
        $final = $response.RequestMessage.RequestUri
        if ($final.Scheme -ne 'https' -or $final.Host -notin @('github.com', 'release-assets.githubusercontent.com', 'objects.githubusercontent.com')) {
            throw 'UxPlay asset redirected to an untrusted location.'
        }
        $response.EnsureSuccessStatusCode() | Out-Null
        $length = if ([int]$response.StatusCode -eq 206) { $response.Content.Headers.ContentRange.Length } else { $response.Content.Headers.ContentLength }
        if ($length -ne $descriptor.size) { throw "Public UxPlay asset size mismatch: $url" }
        if ($null -eq $release) {
            $timeout = [Threading.CancellationTokenSource]::new([TimeSpan]::FromMinutes(3))
            $stream = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
            try {
                $hash = [Security.Cryptography.SHA256]::HashDataAsync($stream, $timeout.Token).AsTask().GetAwaiter().GetResult()
                if ([Convert]::ToHexString($hash) -ine $descriptor.sha256) { throw 'Public UxPlay asset SHA256 mismatch.' }
            } finally { $stream.Dispose(); $timeout.Dispose() }
        }
        Write-Host "PASS UxPlay publication: $tag / $name / $($descriptor.size) bytes / SHA256 $($descriptor.sha256)"
        Write-Host "HTTP $([int]$response.StatusCode); URL $url; final $($final.GetLeftPart([UriPartial]::Path))"
    } finally { $response.Dispose() }
} finally { $request.Dispose(); $client.Dispose() }

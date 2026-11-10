# curl and PowerShell

Copy the key into `IPHONEMIRROR_API_KEY` in your shell. Keep it out of URLs and saved command files.
Use the port shown in Developer tools → Automation API. First enable mirroring and reverse control
in iPhoneMirror, or use the [preparation endpoints](README.md#device-preparation-and-windows).
Run the discovery block first, then set `IPHONEMIRROR_DEVICE_ID` to the intended source's `id`.
The input blocks below act on that device; device IDs must not be guessed from display names.

```powershell
$api = 'http://127.0.0.1:17890/api/v1'
if (-not $env:IPHONEMIRROR_API_KEY) { throw 'Set IPHONEMIRROR_API_KEY first.' }
$headers = @{ 'X-API-Key' = $env:IPHONEMIRROR_API_KEY }
$devices = Invoke-RestMethod "$api/devices" -Headers $headers
$devices.devices | Select-Object id, name, connection, capturing, controlAvailable
```

This block sends one tap to the center of the selected capture, then releases control:

```powershell
$id = $env:IPHONEMIRROR_DEVICE_ID
if (-not $id -or $id -notin $devices.devices.id) { throw 'Choose an id from the device list.' }
$status = Invoke-RestMethod "$api/devices/$id/status" -Headers $headers
if (-not $status.capabilities.tap -or $status.geometry.width -le 0 -or $status.geometry.height -le 0) {
    throw 'Start capture and absolute touch control for this device first.'
}
$lease = Invoke-RestMethod "$api/devices/$id/control/acquire" -Method Post -Headers $headers -ContentType 'application/json' -Body '{"durationSeconds":30}'
$headers['X-Control-Session'] = $lease.sessionToken
try {
    $point = @{
        x = $status.geometry.width / 2
        y = $status.geometry.height / 2
        geometryVersion = $status.geometry.version
    } | ConvertTo-Json
    Invoke-RestMethod "$api/devices/$id/input/tap" -Method Post -Headers $headers -ContentType 'application/json' -Body $point
} finally {
    try { Invoke-RestMethod "$api/devices/$id/control/release" -Method Post -Headers $headers }
    finally { $headers.Remove('X-Control-Session') }
}
# Screenshots do not require a control lease.
Invoke-WebRequest "$api/devices/$id/screenshot" -Headers $headers -OutFile iphone.png
```

With `curl.exe`, the following PowerShell example sends ENTER. JSON files avoid shell quoting issues;
`WriteAllText` writes UTF-8 without a BOM on both Windows PowerShell 5.1 and PowerShell 7.
It reuses `$api` and the selected `$id` above and acquires a new lease:

```powershell
$utf8 = [System.Text.UTF8Encoding]::new($false)
[System.IO.File]::WriteAllText((Join-Path $PWD 'acquire.json'), '{"durationSeconds":30}', $utf8)
[System.IO.File]::WriteAllText((Join-Path $PWD 'key.json'), '{"key":"ENTER"}', $utf8)
$leaseJson = curl.exe --fail-with-body -H "X-API-Key: $env:IPHONEMIRROR_API_KEY" -H 'Content-Type: application/json' --data-binary '@acquire.json' "$api/devices/$id/control/acquire"
if ($LASTEXITCODE -ne 0) { throw 'Could not acquire control.' }
$token = ($leaseJson | ConvertFrom-Json).sessionToken
try {
    curl.exe --fail-with-body -H "X-API-Key: $env:IPHONEMIRROR_API_KEY" -H "X-Control-Session: $token" -H 'Content-Type: application/json' --data-binary '@key.json' "$api/devices/$id/input/key"
    if ($LASTEXITCODE -ne 0) { throw 'Input failed; check status before any retry.' }
} finally {
    curl.exe --fail-with-body -X POST -H "X-API-Key: $env:IPHONEMIRROR_API_KEY" -H "X-Control-Session: $token" "$api/devices/$id/control/release"
    if ($LASTEXITCODE -ne 0) { Write-Warning 'Release failed; reclaim control in the app or wait for lease expiry.' }
    $token = $null
}
```

C# `HttpClient` and Node.js `fetch` use the same URLs, headers and JSON. No SDK installation is required. Do not retry input automatically after a network error; refresh status/ownership and decide whether the action already occurred.

The [Python example](python-example.py) uses the same key and device environment variables. It lists
devices when no ID is set and saves a screenshot when an ID is provided. Set `IPHONEMIRROR_SEND_TAP=1`
only when you want its single sample tap. Text and clipboard helpers are available as separate calls;
after `input/text`, resolve any iOS paste prompt and verify the field before changing the clipboard.

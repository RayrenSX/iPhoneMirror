# curl and PowerShell

Copy the key into `IPHONEMIRROR_API_KEY` in your shell. Keep it out of URLs and saved command files. Use the port shown in Developer tools → Automation API. First enable mirroring and reverse control in iPhoneMirror.

```powershell
$api = 'http://127.0.0.1:17890/api/v1'
$headers = @{ 'X-API-Key' = $env:IPHONEMIRROR_API_KEY }
$devices = Invoke-RestMethod "$api/devices" -Headers $headers
$id = $devices.devices[0].id
$status = Invoke-RestMethod "$api/devices/$id/status" -Headers $headers
$lease = Invoke-RestMethod "$api/devices/$id/control/acquire" -Method Post -Headers $headers -ContentType 'application/json' -Body '{"durationSeconds":30}'
$headers['X-Control-Session'] = $lease.sessionToken
try {
    if ($status.capabilities.tap) {
        $point = @{x = $status.geometry.width / 2; y = $status.geometry.height / 2} | ConvertTo-Json
        Invoke-RestMethod "$api/devices/$id/input/tap" -Method Post -Headers $headers -ContentType 'application/json' -Body $point
    }
    if ($status.capabilities.screenshot) {
        Invoke-WebRequest "$api/devices/$id/screenshot" -Headers $headers -OutFile iphone.png
    }
} finally {
    Invoke-RestMethod "$api/devices/$id/control/release" -Method Post -Headers $headers
}
```

With curl.exe, use the returned device ID and control token; JSON bodies can be stored in files to avoid Windows shell quoting issues:

```powershell
curl.exe --fail-with-body -H "X-API-Key: $env:IPHONEMIRROR_API_KEY" "$api/devices"
'{"durationSeconds":30}' | Set-Content -Encoding utf8NoBOM acquire.json
curl.exe --fail-with-body -H "X-API-Key: $env:IPHONEMIRROR_API_KEY" -H 'Content-Type: application/json' --data-binary '@acquire.json' "$api/devices/$id/control/acquire"
# Set IPHONEMIRROR_CONTROL_SESSION to the returned sessionToken.
'{"key":"ENTER"}' | Set-Content -Encoding utf8NoBOM key.json
curl.exe --fail-with-body -H "X-API-Key: $env:IPHONEMIRROR_API_KEY" -H "X-Control-Session: $env:IPHONEMIRROR_CONTROL_SESSION" -H 'Content-Type: application/json' --data-binary '@key.json' "$api/devices/$id/input/key"
curl.exe --fail-with-body -X POST -H "X-API-Key: $env:IPHONEMIRROR_API_KEY" -H "X-Control-Session: $env:IPHONEMIRROR_CONTROL_SESSION" "$api/devices/$id/control/release"
```

C# `HttpClient` and Node.js `fetch` use the same URLs, headers and JSON. No SDK installation is required. Do not retry input automatically after a network error; refresh status/ownership and decide whether the action already occurred.

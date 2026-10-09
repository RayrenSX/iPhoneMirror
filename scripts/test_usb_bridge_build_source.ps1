$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'UsbBridgeBuildSource.ps1')
. (Join-Path $PSScriptRoot 'UsbTouchBridgeRuntime.ps1')
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('iphoneMirror-build-test-' + [Guid]::NewGuid().ToString('N'))
$recipe = Join-Path $testRoot 'recipe'
$workRoot = Join-Path $testRoot 'stages'
New-Item -ItemType Directory -Path $recipe -Force | Out-Null
try {
    foreach ($name in @('build.ps1', 'iUsbBridge.spec', 'requirements.txt', 'hooks\hook-usb.py')) {
        New-Item -ItemType Directory -Path (Split-Path -Parent (Join-Path $recipe $name)) -Force | Out-Null
        [IO.File]::WriteAllText((Join-Path $recipe $name), "recipe fixture: $name")
    }
    New-Item -ItemType Directory -Path (Join-Path $recipe 'src') | Out-Null
    [IO.File]::WriteAllText((Join-Path $recipe 'src\usb_touch_bridge.py'), 'WRONG UPSTREAM SOURCE')
    $source = Join-Path $projectRoot 'tools'
    $stage = New-UsbBridgeBuildSource -RecipeRoot $recipe -SourceRoot $source -WorkRoot $workRoot
    $hook = Join-Path $recipe 'hooks\hook-usb.py'
    if ((Get-FileHash -LiteralPath $hook).Hash -ne
        (Get-FileHash -LiteralPath (Join-Path $stage 'hooks\hook-usb.py')).Hash) {
        throw 'The custom USB hook was not preserved in the build stage.'
    }
    # A missing override must fail before falling back to upstream host discovery.
    Remove-Item -LiteralPath $hook
    $rejected = $false
    try { New-UsbBridgeBuildSource -RecipeRoot $recipe -SourceRoot $source -WorkRoot $workRoot | Out-Null }
    catch {
        if ($_.Exception.Message -notlike '*build recipe is missing:*hook-usb.py') { throw }
        $rejected = $true
    }
    if (-not $rejected) { throw 'A recipe without its USB hook was accepted.' }
    $files = @((Get-Item -LiteralPath (Join-Path $source 'usb_touch_bridge.py'))) +
        @((Get-Item -LiteralPath (Join-Path $source 'bridge_runtime_check.py'))) +
        @((Get-Item -LiteralPath (Join-Path $source 'ddi_support.py'))) +
        @((Get-Item -LiteralPath (Join-Path $source 'mdns_discovery.py'))) +
        @(Get-ChildItem -LiteralPath (Join-Path $source 'iostouch') -Recurse -File -Filter '*.py')
    foreach ($file in $files) {
        $relative = $file.FullName.Substring($source.Length + 1)
        $copied = Join-Path $stage "src\$relative"
        if ((Get-FileHash -LiteralPath $file.FullName).Hash -ne (Get-FileHash -LiteralPath $copied).Hash) {
            throw "Staged bridge differs from audited source: $relative"
        }
    }
    if ((Get-ChildItem -LiteralPath (Join-Path $stage 'src') -Recurse -File).Count -ne $files.Count) {
        throw 'Unexpected files were included in staged bridge source.'
    }
    foreach ($relative in @(
        'src\DriverInstaller\Assets\libusb-win32-1.2.6.0\amd64\libusb0.dll',
        'third_party\libusb\bin\x64\libusb-1.0.dll')) {
        $original = Join-Path $projectRoot $relative
        $copied = Join-Path $stage ('native\' + (Split-Path -Leaf $relative))
        if ((Get-FileHash -LiteralPath $original).Hash -ne (Get-FileHash -LiteralPath $copied).Hash) {
            throw "Staged native USB backend differs from pinned source: $relative"
        }
    }

    # A valid hash manifest used to accept a bridge with only Python runtime
    # files, even if both USB backends were absent. Check both omissions.
    $payload = Join-Path $testRoot 'payload'
    New-Item -ItemType Directory -Path (Join-Path $payload '_internal') -Force | Out-Null
    $entries = foreach ($relative in @('iUsbBridge.exe', '_internal/python313.dll',
            '_internal/libusb0.dll', '_internal/libusb-1.0.dll')) {
        $file = Join-Path $payload $relative
        [IO.File]::WriteAllText($file, "payload fixture: $relative")
        [PSCustomObject]@{path = $relative; sha256 = (Get-FileHash -LiteralPath $file).Hash}
    }
    $manifest = Join-Path $payload 'iUsbBridge.runtime.json'
    [IO.File]::WriteAllText($manifest, (@{schema = 1; files = @($entries)} | ConvertTo-Json))
    Assert-UsbTouchBridgeRuntime $payload 'complete fixture'
    $packaged = @(Get-UsbTouchBridgeRuntimePayloadFiles $payload)
    foreach ($missing in @('_internal/libusb0.dll', '_internal/libusb-1.0.dll')) {
        if ((Join-Path 'tools' $missing.Replace('/', '\')) -notin $packaged) {
            throw "Release payload omitted USB backend: $missing"
        }
        [IO.File]::WriteAllText($manifest, (@{schema = 1; files = @(
            $entries | Where-Object path -ne $missing)} | ConvertTo-Json))
        $rejected = $false
        try { Get-UsbTouchBridgeRuntimeManifestEntries $payload 'incomplete fixture' | Out-Null }
        catch {
            if ($_.Exception.Message -notlike '*missing required USB backend*') { throw }
            $rejected = $true
        }
        if (-not $rejected) { throw "Manifest accepted missing USB backend: $missing" }
    }
    [IO.File]::WriteAllText($manifest, (@{schema = 1; files = @($entries)} | ConvertTo-Json))
    [IO.File]::WriteAllText((Join-Path $payload '_internal/libusb0.dll'), 'changed DLL')
    $rejected = $false
    try { Assert-UsbTouchBridgeRuntime $payload 'modified fixture' }
    catch {
        if ($_.Exception.Message -notlike '*hash mismatch*') { throw }
        $rejected = $true
    }
    if (-not $rejected) { throw 'USB backend hash mismatch was accepted.' }

    Remove-UsbBridgeBuildSource -Stage $stage -WorkRoot $workRoot
    if (Test-Path -LiteralPath $stage) { throw 'Build stage cleanup did not complete.' }
    if (-not (Test-Path -LiteralPath $recipe)) { throw 'Stage cleanup affected the original recipe.' }

    foreach ($scriptName in @('build_installer.ps1', 'package_release.ps1')) {
        $tokens = $null
        $errors = $null
        $ast = [Management.Automation.Language.Parser]::ParseFile(
            (Join-Path $PSScriptRoot $scriptName), [ref]$tokens, [ref]$errors)
        if ($errors.Count) { throw "Invalid PowerShell syntax: $scriptName" }
        if ($scriptName -eq 'build_installer.ps1') {
            $forwarding = $ast.FindAll({ param($node)
                $node -is [Management.Automation.Language.CommandParameterAst] -and
                $node.ParameterName -eq 'OmitUxPlayRuntime' -and
                $node.Argument.Extent.Text -eq '$OmitUxPlayRuntime'
            }, $true)
        } else {
            $forwarding = $ast.FindAll({ param($node)
                $node -is [Management.Automation.Language.AssignmentStatementAst] -and
                $node.Left.Extent.Text -eq '$buildArguments.OmitUxPlayRuntime' -and
                $node.Right.Extent.Text -eq '$true'
            }, $true)
        }
        if (@($forwarding).Count -eq 0) { throw "OmitUxPlayRuntime is not forwarded: $scriptName" }
    }
    Write-Host 'USB bridge build source, native backend, manifest and packaging flag tests passed.'
}
finally {
    # Only this test's freshly generated, explicit temporary directory is removed.
    if ([IO.Path]::GetFullPath($testRoot).StartsWith([IO.Path]::GetTempPath(), [StringComparison]::OrdinalIgnoreCase) -and
        (Split-Path -Leaf $testRoot) -match '^iphoneMirror-build-test-[0-9a-f]{32}$') {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}

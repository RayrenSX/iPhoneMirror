$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'UsbBridgeBuildSource.ps1')
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('iphoneMirror-build-test-' + [Guid]::NewGuid().ToString('N'))
$recipe = Join-Path $testRoot 'recipe'
$workRoot = Join-Path $testRoot 'stages'
New-Item -ItemType Directory -Path $recipe -Force | Out-Null
try {
    foreach ($name in @('build.ps1', 'iUsbBridge.spec', 'requirements.txt')) {
        [IO.File]::WriteAllText((Join-Path $recipe $name), "recipe fixture: $name")
    }
    New-Item -ItemType Directory -Path (Join-Path $recipe 'src') | Out-Null
    [IO.File]::WriteAllText((Join-Path $recipe 'src\usb_touch_bridge.py'), 'WRONG UPSTREAM SOURCE')
    $source = Join-Path $projectRoot 'tools'
    $stage = New-UsbBridgeBuildSource -RecipeRoot $recipe -SourceRoot $source -WorkRoot $workRoot
    $files = @((Get-Item -LiteralPath (Join-Path $source 'usb_touch_bridge.py'))) +
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
    Write-Host 'USB bridge build source and packaging flag tests passed.'
}
finally {
    # Only this test's freshly generated, explicit temporary directory is removed.
    if ([IO.Path]::GetFullPath($testRoot).StartsWith([IO.Path]::GetTempPath(), [StringComparison]::OrdinalIgnoreCase) -and
        (Split-Path -Leaf $testRoot) -match '^iphoneMirror-build-test-[0-9a-f]{32}$') {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}

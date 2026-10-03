$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$bootstrapPath = Join-Path $repository 'src\DriverInstaller\Services\DriverElevation.Bootstrap.ps1'
$tokens = $null
$parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile(
    $bootstrapPath, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw ($parseErrors | Out-String) }

# Load only the two pure helpers. Never execute the privileged entry point,
# create an administrator directory, launch a process, or modify drivers.
$helpers = $ast.FindAll({ param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst]
}, $false)
if ($helpers.Count -ne 2) { throw 'Unexpected bootstrap helper definitions.' }
foreach ($helper in $helpers) {
    . ([ScriptBlock]::Create($helper.Extent.Text))
}

$fixtureRoot = Join-Path $repository ('work\driver-bootstrap-test-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($fixtureRoot)
try {
    $source = Join-Path $fixtureRoot 'source'
    $target = Join-Path $fixtureRoot 'target'
    [void][IO.Directory]::CreateDirectory($source)
    [void][IO.Directory]::CreateDirectory($target)
    $image = Join-Path $source 'iPhoneMirror.Driver.exe'
    $destination = Join-Path $target 'iPhoneMirror.Driver.exe'
    [IO.File]::WriteAllText($image, 'trusted sentinel bundle')
    foreach ($sibling in @('iPhoneMirror.Driver.dll', 'iPhoneMirror.Driver.deps.json',
            'iPhoneMirror.Driver.runtimeconfig.json', 'hostfxr.dll', 'coreclr.dll')) {
        [IO.File]::WriteAllText((Join-Path $source $sibling), 'untrusted sibling')
    }
    $digest = (Get-FileHash -LiteralPath $image -Algorithm SHA256).Hash
    Copy-VerifiedDriverBundle $image $destination $digest
    if ([IO.File]::ReadAllText($destination) -ne 'trusted sentinel bundle' -or
        [IO.Directory]::GetFiles($target).Count -ne 1) {
        throw 'Bundle staging copied untrusted dependencies.'
    }
    $rejected = $false
    [IO.File]::WriteAllText($image, 'replaced bundle')
    try { Copy-VerifiedDriverBundle $image (Join-Path $target 'rejected.exe') $digest }
    catch { $rejected = $true }
    if (-not $rejected -or [IO.File]::Exists((Join-Path $target 'rejected.exe'))) {
        throw 'A replaced bundle was accepted.'
    }

    $start = New-IsolatedDriverStartInfo $destination $target '--sentinel'
    $expected = @('SystemRoot', 'WINDIR', 'PATH', 'PSModulePath', 'TEMP', 'TMP',
        'DOTNET_BUNDLE_EXTRACT_BASE_DIR')
    if ($start.EnvironmentVariables.Count -ne $expected.Count) {
        throw 'The driver environment inherited unexpected variables.'
    }
    foreach ($key in $start.EnvironmentVariables.Keys) {
        if ($key -notin $expected) { throw "Unexpected runtime override: $key" }
    }
    if ($start.FileName -ne $destination -or $start.WorkingDirectory -ne $target -or
        $start.UseShellExecute -or $start.EnvironmentVariables['PATH'] -ne [Environment]::SystemDirectory -or
        $start.EnvironmentVariables['DOTNET_BUNDLE_EXTRACT_BASE_DIR'] -ne (Join-Path $target 'runtime')) {
        throw 'The driver start information can escape its staging directory.'
    }
    'PASS: verified bundle only; replacement rejected; runtime/configuration environment isolated.'
}
finally {
    # This explicit randomly named child contains only this test's sentinel data.
    [IO.Directory]::Delete($fixtureRoot, $true)
}

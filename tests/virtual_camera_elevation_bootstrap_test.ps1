$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$source = [IO.File]::ReadAllText((Join-Path $repository 'src\App\Services\VirtualCameraService.cs'))
$match = [regex]::Match($source,
    '(?s)private const string VerifiedElevationBootstrap = """\r?\n(.*?)\r?\n        """;')
if (-not $match.Success) { throw 'The virtual camera bootstrap was not found.' }
$bootstrap = [regex]::Replace($match.Groups[1].Value, '(?m)^        ', '')
$bootstrap = $bootstrap.Replace('$HELPER_PATH_BASE64$', 'aGVscGVy').Replace(
    '$MEDIA_PATH_BASE64$', 'bWVkaWE=').Replace('$HELPER_SHA256$', ('A' * 64)).Replace(
    '$MEDIA_SHA256$', ('B' * 64)).Replace('$INSTALL$', '$true')
$tokens = $null
$parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseInput(
    $bootstrap, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw ($parseErrors | Out-String) }
$commands = $ast.FindAll({ param($node)
    $node -is [Management.Automation.Language.CommandAst]
}, $true)
foreach ($command in $commands) {
    if ($command.GetCommandName() -notin @('Copy-VerifiedPayload', 'New-IsolatedCameraStartInfo')) {
        throw ('Elevated command discovery is not allowed: ' + $command.Extent.Text)
    }
}
# Only these two pure functions are evaluated. No privileged entry point,
# process launch, camera registration, or system directory is touched.
$helpers = $ast.FindAll({ param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst]
}, $false)
if ($helpers.Count -ne 2) { throw 'Unexpected camera bootstrap functions.' }
foreach ($helper in $helpers) { . ([ScriptBlock]::Create($helper.Extent.Text)) }

$fixtureRoot = Join-Path $repository ('work\camera-bootstrap-test-' + [Guid]::NewGuid().ToString('N'))
$originalProgramFiles = $env:ProgramFiles
$originalProgramW6432 = $env:ProgramW6432
[void][IO.Directory]::CreateDirectory($fixtureRoot)
try {
    $sourcePath = Join-Path $fixtureRoot 'source.bin'
    $destination = Join-Path $fixtureRoot 'copied.bin'
    [IO.File]::WriteAllText($sourcePath, 'verified camera sentinel')
    $digest = (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash
    Copy-VerifiedPayload $sourcePath $digest $destination
    if ([IO.File]::ReadAllText($destination) -ne 'verified camera sentinel') {
        throw 'Camera payload copy failed.'
    }
    [IO.File]::WriteAllText($sourcePath, 'replaced camera sentinel')
    $rejected = $false
    $rejectedPath = Join-Path $fixtureRoot 'rejected.bin'
    try { Copy-VerifiedPayload $sourcePath $digest $rejectedPath }
    catch { $rejected = $true }
    if (-not $rejected -or [IO.File]::Exists($rejectedPath)) {
        throw 'A replaced camera payload was accepted.'
    }

    $expectedProgramFiles = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)
    $env:ProgramFiles = Join-Path $fixtureRoot 'untrusted-program-files'
    $env:ProgramW6432 = $env:ProgramFiles
    $mediaSource = Join-Path $fixtureRoot 'camera media.dll'
    foreach ($install in @($false, $true)) {
        $start = New-IsolatedCameraStartInfo $destination $fixtureRoot $mediaSource $install
        $expected = @('SystemRoot', 'WINDIR', 'PATH', 'TEMP', 'TMP', 'ProgramFiles', 'ProgramW6432')
        if ($start.EnvironmentVariables.Count -ne $expected.Count) {
            throw 'Unexpected inherited camera environment.'
        }
        foreach ($key in $start.EnvironmentVariables.Keys) {
            if ($key -notin $expected) { throw "Unexpected camera environment: $key" }
        }
        $expectedArguments = if ($install) { 'install "' + $mediaSource + '"' } else { 'uninstall' }
        if ($start.UseShellExecute -or -not $start.CreateNoWindow -or
            $start.WorkingDirectory -ne $fixtureRoot -or $start.FileName -ne $destination -or
            $start.Arguments -ne $expectedArguments -or
            $start.EnvironmentVariables['PATH'] -ne [Environment]::SystemDirectory -or
            $start.EnvironmentVariables['ProgramFiles'] -ne $expectedProgramFiles -or
            $start.EnvironmentVariables['ProgramW6432'] -ne $expectedProgramFiles) {
            throw 'The camera helper trusts inherited paths or has invalid arguments.'
        }
    }
    'PASS: camera bootstrap has no module discovery; verified copy; replacement rejected; native environment and arguments isolated.'
}
finally {
    $env:ProgramFiles = $originalProgramFiles
    $env:ProgramW6432 = $originalProgramW6432
    [IO.Directory]::Delete($fixtureRoot, $true)
}

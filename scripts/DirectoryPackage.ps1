# Shared preflight for standalone QA directory packages. A plain App/test
# project build does not integrate the separately published driver manager.
function Assert-DirectoryPackageInputs {
    param(
        [Parameter(Mandatory)][string]$ApplicationDirectory,
        [Parameter(Mandatory)][string]$DriverDirectory
    )
    foreach ($relative in @('iPhoneMirror.exe', 'iPhoneMirror.dll',
            'iPhoneMirror.VirtualCamera.dll', 'iPhoneMirror.VirtualCamera.Admin.exe')) {
        $path = Join-Path $ApplicationDirectory $relative
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Application package input is missing: $relative"
        }
    }
    $driver = Join-Path $DriverDirectory 'iPhoneMirror.Driver.exe'
    if (-not (Test-Path -LiteralPath $driver -PathType Leaf)) {
        throw 'Driver package input is missing: iPhoneMirror.Driver.exe'
    }
    $app = Join-Path $ApplicationDirectory 'iPhoneMirror.dll'
    $appVersion = ((Get-Item -LiteralPath $app).VersionInfo.ProductVersion -split '\+', 2)[0]
    $driverVersion = ((Get-Item -LiteralPath $driver).VersionInfo.ProductVersion -split '\+', 2)[0]
    if ([string]::IsNullOrWhiteSpace($appVersion) -or $appVersion -cne $driverVersion) {
        throw "Driver/app product version mismatch: app=$appVersion driver=$driverVersion"
    }
    # Read resources without executing application code or locking its file.
    $assembly = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes($app))
    foreach ($relative in @('iPhoneMirror.VirtualCamera.dll', 'iPhoneMirror.VirtualCamera.Admin.exe')) {
        $resource = 'IPhoneMirror.App.Payload.' + $relative
        $stream = $assembly.GetManifestResourceStream($resource)
        if ($null -eq $stream) { throw "Embedded camera payload is missing: $resource" }
        try { $embedded = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) }
        finally { $stream.Dispose() }
        $external = (Get-FileHash -LiteralPath (Join-Path $ApplicationDirectory $relative)).Hash
        if ($external -ine $embedded) { throw "External/embedded camera payload mismatch: $relative" }
    }
}

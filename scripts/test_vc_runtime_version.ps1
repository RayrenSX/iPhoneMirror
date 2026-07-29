$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $root 'VcRuntimeVersion.ps1')

$cases = @(
    @{ Input = '14.29.30157.0 built by: cloudtest'; Expected = '14.29.30157.0' },
    @{ Input = '14.40.33810.0'; Expected = '14.40.33810.0' },
    @{ Input = '14.38'; Expected = '14.38' }
)
foreach ($case in $cases) {
    $actual = (ConvertTo-VcRuntimeVersion $case.Input).ToString()
    if ($actual -ne $case.Expected) {
        throw "VC runtime version parse mismatch: expected $($case.Expected), got $actual"
    }
}

foreach ($invalid in @('', 'built by: cloudtest', '14', '14.29.30157.0.5')) {
    try {
        [void](ConvertTo-VcRuntimeVersion $invalid)
        throw "Invalid VC runtime version was accepted: $invalid"
    }
    catch {
        if ($_.Exception.Message -notlike 'Invalid Visual C++ runtime version:*') {
            throw
        }
    }
}

Write-Output 'VC runtime version parser tests passed.'

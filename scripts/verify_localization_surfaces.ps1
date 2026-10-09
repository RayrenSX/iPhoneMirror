[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Push-Location (Split-Path -Parent $PSScriptRoot)
try {
    # This verifies only catalogs/formatters; it never runs driver cleanup.
    & (Join-Path $PSScriptRoot 'test_cleanup_localization.ps1')
    node (Join-Path $PSScriptRoot 'test_web_localization.cjs')
    if ($LASTEXITCODE -ne 0) { throw 'Browser localization tests failed.' }

    # The broad audit otherwise only warns and skips inherited installer text.
    & (Join-Path $PSScriptRoot 'prepare_inno_setup.ps1') | Out-Host
    $auditLines = @(python -X utf8 (Join-Path $PSScriptRoot 'audit_localization.py') `
        --require-installer --summary)
    $auditExit = $LASTEXITCODE
    $auditLines | Out-Host
    if ($auditExit -ne 0) { throw 'Cross-surface localization audit failed.' }
    $audit = ($auditLines -join "`n") | ConvertFrom-Json
    if ($env:GITHUB_STEP_SUMMARY) {
        $summary = @(
            '### Additional localization surfaces'
            ''
            '| Component | Language | Text entries |'
            '| --- | --- | ---: |'
            foreach ($component in $audit.keys.PSObject.Properties) {
                foreach ($language in $component.Value.PSObject.Properties) {
                    "| $($component.Name) | $($language.Name) | $($language.Value) |"
                }
            }
            ''
            'Installer, cleanup formatter, browser catalogs, startup fallback and changelog checks passed.'
        )
        Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Value $summary -Encoding utf8
    }
}
finally { Pop-Location }

[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$source = Join-Path $PSScriptRoot 'remove_selected_iphone_drivers.ps1'
$tokens = $null
$parseErrors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($source, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw ($parseErrors | Out-String) }
# Evaluate ONLY the literal catalog and the pure formatter. Never run cleanup,
# device discovery, process termination, elevation, or any script entry point.
$assignment = $ast.Find({ param($node)
    $node -is [System.Management.Automation.Language.AssignmentStatementAst] -and
    $node.Left.Extent.Text -eq '$script:CleanupMessages'
}, $true)
$formatter = $ast.Find({ param($node)
    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
    $node.Name -eq 'Get-CleanupText'
}, $true)
if (!$assignment -or !$formatter) { throw 'Cleanup localization definitions are missing.' }
. ([scriptblock]::Create($assignment.Extent.Text))
. ([scriptblock]::Create($formatter.Extent.Text))
$cases = 0
foreach ($Language in @('zh-CN', 'zh-HK', 'en-US')) {
    foreach ($property in $script:CleanupMessages.$Language.PSObject.Properties) {
        if ([string]::IsNullOrWhiteSpace($property.Value)) { throw "Empty message: $Language/$($property.Name)" }
        foreach ($count in @(0, 1, 2, 5)) {
            $rendered = Get-CleanupText $property.Name -Values @($count, $count, $count)
            if ([string]::IsNullOrWhiteSpace($rendered)) { throw "Empty result: $Language/$($property.Name)" }
            $cases++
        }
    }
    if ((Get-CleanupText 'PnpCached' -Values @(2)) -notmatch '2') { throw 'Count substitution failed.' }
    if (((Get-CleanupText 'Confirm') -f 'REMOVE-1') -notmatch 'REMOVE-1') { throw 'Confirmation token substitution failed.' }
}
Write-Output "Cleanup localization passed: $cases formatting cases; cleanup was not executed."

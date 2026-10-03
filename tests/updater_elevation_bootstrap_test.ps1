param([switch]$DesktopProbe)
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$source = [IO.File]::ReadAllText((Join-Path $repository 'src\App\Updater\UpdateInstallerLauncher.cs'))
foreach ($name in @('VerifiedScriptBootstrap', 'VerifiedInstallerBootstrap')) {
    $match = [regex]::Match($source, '(?s)private const string ' + $name + ' = """\r?\n(.*?)\r?\n        """;')
    if (-not $match.Success) { throw ('Missing bootstrap: ' + $name) }
    $script = [regex]::Replace($match.Groups[1].Value, '(?m)^        ', '')
    foreach ($marker in @('$SCRIPT_PATH_BASE64$', '$PACKAGE_PATH_BASE64$', '$ARGUMENT_LINE_BASE64$')) {
        $script = $script.Replace($marker, 'c2VudGluZWw=')
    }
    $script = $script.Replace('$EXPECTED_SHA256$', ('A' * 64)).Replace(
        '$CLEANUP_DIRECTORY$', '$false').Replace('$ARGUMENTS_BASE64$', "'LU91dHB1dA==', 'c2VudGluZWw='")
    $tokens = $null
    $errors = $null
    $ast = [Management.Automation.Language.Parser]::ParseInput($script, [ref]$tokens, [ref]$errors)
    if ($errors.Count) { throw ($errors | Out-String) }
    foreach ($command in $ast.FindAll({ param($node)
        $node -is [Management.Automation.Language.CommandAst]
    }, $true)) {
        # Script bootstrap invokes only the hash-verified ScriptBlock. The
        # installer bootstrap uses only .NET APIs and never resolves a cmdlet.
        if ($name -ne 'VerifiedScriptBootstrap' -or $command.GetCommandName() -or
            -not $command.Extent.Text.Contains('[ScriptBlock]::Create($scriptText)')) {
            throw ('Unexpected bootstrap command discovery: ' + $command.Extent.Text)
        }
    }
}
$zipScript = Join-Path $repository 'src\App\tools\updater\Apply-ZipUpdate.ps1'
$tokens = $null
$errors = $null
$zipAst = [Management.Automation.Language.Parser]::ParseFile($zipScript, [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw ($errors | Out-String) }
if ($DesktopProbe) {
    # Load and invoke only the desktop lookup; never run the updater entry
    # point, Start-RestartProcess, ShellExecute, or an installation operation.
    $lookup = $zipAst.Find({ param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
        $node.Name -eq 'Get-InteractiveDesktopShell'
    }, $false)
    if ($null -eq $lookup) { throw 'Desktop lookup function missing.' }
    . ([ScriptBlock]::Create($lookup.Extent.Text))
    $shell = Get-InteractiveDesktopShell
    $folder = $null
    try {
        $folder = $shell.NameSpace(0)
        if ($null -eq $folder) { throw 'The returned desktop automation object is unusable.' }
    }
    finally {
        foreach ($instance in @($folder, $shell)) {
            if ($null -ne $instance -and [Runtime.InteropServices.Marshal]::IsComObject($instance)) {
                [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($instance)
            }
        }
    }
    'PASS: existing Explorer desktop automation resolved without launching a process.'
}
'PASS: update bootstrap syntax and command-discovery boundaries verified without executing an update.'

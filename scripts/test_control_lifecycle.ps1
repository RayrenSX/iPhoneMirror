[CmdletBinding()]
param([string]$Python = 'python')

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\App.Runtime.Tests\IPhoneMirror.App.Runtime.Tests.csproj'
$pythonPath = (Get-Command $Python -CommandType Application -ErrorAction Stop |
    Select-Object -First 1).Source
$dotnetPath = (Get-Command dotnet -CommandType Application -ErrorAction Stop |
    Select-Object -First 1).Source
dotnet build $project --configuration Release -p:NuGetAudit=false
if ($LASTEXITCODE -ne 0) { throw 'Control lifecycle test build failed.' }
$assembly = dotnet msbuild $project -nologo -p:Configuration=Release -getProperty:TargetPath
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($assembly)) {
    throw 'Failed to locate the control lifecycle test assembly.'
}
# These focused entry points do not create WPF windows or access a phone.
# Use an installed-app environment, matching build.ps1's managed test runner.
foreach ($arguments in @(@('--ddi-startup'), @('--control-lifecycle', $pythonPath), @('--bluetooth-input-transport'))) {
    $startInfo = [Diagnostics.ProcessStartInfo]::new($dotnetPath)
    $startInfo.UseShellExecute = $false
    $startInfo.WorkingDirectory = $root
    foreach ($name in @($startInfo.Environment.Keys)) {
        if ($name -match '^(?i:COMPLUS_|COR_|CORECLR_|DOTNET_|DEVPATH$|APPDOMAIN_MANAGER_ASM$|APPDOMAIN_MANAGER_TYPE$|CLRConfigFile$)') {
            $startInfo.Environment.Remove($name) | Out-Null
        }
    }
    $startInfo.ArgumentList.Add($assembly.Trim())
    foreach ($argument in $arguments) { $startInfo.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($startInfo)
    try {
        $process.WaitForExit()
        if ($process.ExitCode -ne 0) { throw "Control lifecycle check failed: $arguments" }
    }
    finally { $process.Dispose() }
}

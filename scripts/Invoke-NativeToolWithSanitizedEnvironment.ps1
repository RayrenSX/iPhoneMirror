param([string]$ManagedTestAssembly)

function Invoke-NativeToolWithSanitizedEnvironment(
    [string]$FilePath,
    [string[]]$ArgumentList,
    [switch]$CleanManagedTestEnvironment
) {
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FilePath
    $startInfo.WorkingDirectory = Split-Path -Parent $PSScriptRoot
    $startInfo.UseShellExecute = $false

    # Some Windows hosts expose both Path and PATH in the inherited block.
    # .NET Framework MSBuild treats those as duplicate Hashtable keys when it
    # launches CL.exe. Rebuild the child environment case-insensitively so the
    # compiler always receives exactly one canonical Path entry.
    $environment = [Environment]::GetEnvironmentVariables()
    $canonicalPath = @($environment.GetEnumerator() |
        Where-Object { $_.Key -ceq 'Path' } |
        Select-Object -First 1).Value
    if ([string]::IsNullOrWhiteSpace($canonicalPath)) {
        $canonicalPath = @($environment.GetEnumerator() |
            Where-Object { $_.Key -ieq 'Path' } |
            Select-Object -First 1).Value
    }
    $startInfo.Environment.Clear()
    $seenEnvironmentNames = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $environment.GetEnumerator()) {
        $name = [string]$entry.Key
        if ($name -ieq 'Path' -or -not $seenEnvironmentNames.Add($name)) {
            continue
        }
        # SDK/CI runtime overrides are not part of the installed application's
        # environment. Keep them out of test children so elevation tests reach
        # their intended checks; production rejection remains unchanged.
        if ($CleanManagedTestEnvironment -and
            $name -match '^(?i:COMPLUS_|COR_|CORECLR_|DOTNET_|DEVPATH$|APPDOMAIN_MANAGER_ASM$|APPDOMAIN_MANAGER_TYPE$|CLRConfigFile$)') {
            continue
        }
        $startInfo.Environment[$name] = [string]$entry.Value
    }
    $startInfo.Environment['Path'] = $canonicalPath
    # Do not reconnect to long-lived MSBuild worker nodes that may have been
    # created before the environment was sanitized.
    $startInfo.Environment['MSBUILDDISABLENODEREUSE'] = '1'
    # ArgumentList is unavailable on the .NET Framework ProcessStartInfo used by
    # Windows PowerShell 5.1. Fall back to a Windows-command-line-safe string so
    # release builds work on both PowerShell hosts.
    $quotedArguments = foreach ($argument in $ArgumentList) {
        $value = [string]$argument
        if ([string]::IsNullOrEmpty($value)) {
            '""'
        }
        elseif ($value -notmatch '[\s"]') {
            $value
        }
        else {
            $escapedValue = [regex]::Replace($value, '(\\*)"', '$1$1\\"')
            $escapedValue = [regex]::Replace($escapedValue, '(\\+)$', '$1$1')
            '"' + $escapedValue + '"'
        }
    }
    $startInfo.Arguments = $quotedArguments -join ' '

    # Construct the process explicitly.  On some PowerShell/.NET hosts the
    # static Start overload can return $null even though the child was created,
    # which then turns the following WaitForExit call into a null dereference
    # and aborts the release package.
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    if (-not $process.Start()) {
        throw "Failed to start native tool: $FilePath"
    }
    $process.WaitForExit()
    return $process.ExitCode
}

if ($ManagedTestAssembly) {
    $dotnet = (Get-Command dotnet -ErrorAction Stop).Source
    exit (Invoke-NativeToolWithSanitizedEnvironment $dotnet @($ManagedTestAssembly) -CleanManagedTestEnvironment)
}

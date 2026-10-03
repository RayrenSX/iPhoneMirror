# This resource is executed by the system Windows PowerShell with -NoProfile.
# Do not introduce module/cmdlet discovery: PSModulePath can be user controlled.
$ErrorActionPreference = 'Stop'
$sourcePath = '$SOURCE_LITERAL$'
$expectedHash = '$HASH_LITERAL$'
$argumentLine = '$ARGUMENT_LINE_LITERAL$'

function Copy-VerifiedDriverBundle([string]$SourcePath, [string]$Destination,
    [string]$ExpectedHash) {
    $source = [IO.File]::Open($SourcePath, [IO.FileMode]::Open,
        [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        $algorithm = [Security.Cryptography.SHA256]::Create()
        try {
            $actualHash = [BitConverter]::ToString($algorithm.ComputeHash($source)).Replace('-', '')
        }
        finally { $algorithm.Dispose() }
        if (-not $actualHash.Equals($ExpectedHash, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'The driver bundle changed after verification.'
        }
        $source.Position = 0
        $output = [IO.File]::Open($Destination, [IO.FileMode]::CreateNew,
            [IO.FileAccess]::Write, [IO.FileShare]::None)
        try { $source.CopyTo($output); $output.Flush() }
        finally { $output.Dispose() }
    }
    finally { $source.Dispose() }
}

function New-IsolatedDriverStartInfo([string]$Executable, [string]$Directory,
    [string]$ArgumentLine) {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $Executable
    $start.Arguments = $ArgumentLine
    $start.WorkingDirectory = $Directory
    $start.UseShellExecute = $false
    # A minimal environment prevents startup hooks, additional deps, profilers,
    # host probing, PATH DLL searches, and user-controlled native extraction.
    $start.EnvironmentVariables.Clear()
    $windows = [IO.Directory]::GetParent([Environment]::SystemDirectory).FullName
    $start.EnvironmentVariables['SystemRoot'] = $windows
    $start.EnvironmentVariables['WINDIR'] = $windows
    $start.EnvironmentVariables['PATH'] = [Environment]::SystemDirectory
    $start.EnvironmentVariables['PSModulePath'] = [IO.Path]::Combine(
        [Environment]::SystemDirectory, 'WindowsPowerShell\v1.0\Modules')
    $start.EnvironmentVariables['TEMP'] = $Directory
    $start.EnvironmentVariables['TMP'] = $Directory
    $start.EnvironmentVariables['DOTNET_BUNDLE_EXTRACT_BASE_DIR'] =
        [IO.Path]::Combine($Directory, 'runtime')
    return $start
}

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'The driver bootstrap is not elevated.'
}

# SystemDirectory has OS-protected ancestors. A random child is created with
# its final ACL and administrator owner before placing executable content in it.
# The per-user installation directory and the user's TEMP are never searched.
$directory = [IO.Path]::Combine([Environment]::SystemDirectory,
    'iPhoneMirror.Driver-' + [Guid]::NewGuid().ToString('N'))
$administrators = [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
$system = [Security.Principal.SecurityIdentifier]::new('S-1-5-18')
$security = [Security.AccessControl.DirectorySecurity]::new()
$security.SetAccessRuleProtection($true, $false)
$security.SetOwner($administrators)
$inheritance = [Security.AccessControl.InheritanceFlags]::ContainerInherit -bor
    [Security.AccessControl.InheritanceFlags]::ObjectInherit
foreach ($sid in @($administrators, $system)) {
    $security.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
        $sid, [Security.AccessControl.FileSystemRights]::FullControl, $inheritance,
        [Security.AccessControl.PropagationFlags]::None,
        [Security.AccessControl.AccessControlType]::Allow))
}
$directoryInfo = [IO.DirectoryInfo]::new($directory)
if ($directoryInfo.Exists) { throw 'The driver staging directory already exists.' }
$directoryInfo.Create($security)
$exitCode = 1
try {
    $destination = [IO.Path]::Combine($directory, 'iPhoneMirror.Driver.exe')
    Copy-VerifiedDriverBundle $sourcePath $destination $expectedHash
    $start = New-IsolatedDriverStartInfo $destination $directory $argumentLine
    $process = [Diagnostics.Process]::Start($start)
    if ($null -eq $process) { throw 'The verified driver host did not start.' }
    try { $process.WaitForExit(); $exitCode = $process.ExitCode }
    finally { $process.Dispose() }
}
finally {
    # Only this fresh, administrator-owned child is removed; no shared cache or
    # installation data is part of this operation.
    try { [IO.Directory]::Delete($directory, $true) } catch { }
}
exit $exitCode

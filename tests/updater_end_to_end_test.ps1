[CmdletBinding()]
param(
    [switch]$ElevatedInstallerBootstrap,
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release'
)
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$fixture = Join-Path $repository ('work\updater-e2e-' + [Guid]::NewGuid().ToString('N'))
$payload = Join-Path $fixture 'payload'
$install = Join-Path $fixture '安装 ''$(); files'
$oldPayload = Join-Path $fixture 'old'
foreach ($directory in @($fixture, $payload, $install, $oldPayload)) {
    [void][IO.Directory]::CreateDirectory($directory)
}

function Start-TestProcess([string]$Executable, [string[]]$Arguments) {
    $start = [Diagnostics.ProcessStartInfo]::new($Executable)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $start.WorkingDirectory = [Environment]::SystemDirectory
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    $process | Add-Member -NotePropertyName TestOutput -NotePropertyValue $process.StandardOutput.ReadToEndAsync()
    $process | Add-Member -NotePropertyName TestError -NotePropertyValue $process.StandardError.ReadToEndAsync()
    return $process
}

function Wait-TestProcess([Diagnostics.Process]$Process, [int]$Timeout = 30000) {
    if (-not $Process.WaitForExit($Timeout)) {
        throw "Test process timed out: $($Process.Id)"
    }
    $output = $Process.TestOutput.GetAwaiter().GetResult()
    $errorOutput = $Process.TestError.GetAwaiter().GetResult()
    [IO.File]::WriteAllText((Join-Path $fixture ("process-$($Process.Id).log")), $output + $errorOutput)
    if ($output) { Write-Host $output.TrimEnd() }
    if ($Process.ExitCode -ne 0 -and $errorOutput) { Write-Host $errorOutput.TrimEnd() }
    if ($Process.ExitCode -ne 0) { throw "Test process failed: $($Process.ExitCode)" }
}

function Wait-TestFile([string]$Path) {
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    while (-not [IO.File]::Exists($Path)) {
        if ([DateTime]::UtcNow -gt $deadline) { throw "Test marker missing: $Path" }
        Start-Sleep -Milliseconds 100
    }
}

$source = @'
using System;
using System.IO;
using System.Windows.Forms;
class Fixture : Form {
    [STAThread] static void Main() {
#if NEW_VERSION
        File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "restarted.txt"), "new-version");
#else
        Application.Run(new Fixture());
#endif
    }
    Fixture() {
        ShowInTaskbar = false;
        WindowState = FormWindowState.Minimized;
        Load += delegate { Hide(); File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "old-ready.txt"), "ready"); };
        FormClosing += delegate(object sender, FormClosingEventArgs e) {
            e.Cancel = true; Hide();
            File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "close-requested.txt"), "close-to-tray");
        };
        var timer = new Timer { Interval = 100 };
        timer.Tick += delegate { if (File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "exit-old.txt"))) Environment.Exit(0); };
        timer.Start();
    }
}
'@
$sourceFile = Join-Path $fixture 'Fixture.cs'
[IO.File]::WriteAllText($sourceFile, $source)
$compiler = Join-Path ([Environment]::GetFolderPath('Windows')) 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
foreach ($build in @(@($oldPayload, ''), @($payload, '/define:NEW_VERSION'))) {
    $arguments = @('/nologo', '/target:winexe', '/r:System.Windows.Forms.dll',
        ('/out:' + (Join-Path $build[0] 'iPhoneMirror.exe')), $sourceFile)
    if ($build[1]) { $arguments += $build[1] }
    $compilation = Start-TestProcess $compiler $arguments
    try { Wait-TestProcess $compilation } finally { $compilation.Dispose() }
}
[IO.File]::Copy((Join-Path $payload 'iPhoneMirror.exe'), (Join-Path $payload 'iPhoneMirror.Driver.exe'))
[IO.File]::WriteAllText((Join-Path $payload 'libusb0.dll'), 'fixture only')
$inno = Join-Path $repository 'work\tools\inno-setup\ISCC.exe'
if (-not [IO.File]::Exists($inno)) { throw 'Run scripts/prepare_inno_setup.ps1 first.' }
$identity = 'iPhoneMirrorUpdaterTest-' + [Guid]::NewGuid().ToString('N')
$arguments = @('/Q', '/DMyAppVersion=9.9.0-updater-e2e', '/DMyNumericVersion=9.9.0.0',
    ('/DMySourceDir=' + $payload), ('/DMyOutputDir=' + $fixture),
    ('/DMyAppId=' + $identity), ('/DMyAppName=' + $identity),
    ('/DMyAppUserModelId=' + $identity), ('/DMyAppPathName=' + $identity + '.exe'),
    ('/DMyUserDataDir=' + (Join-Path $fixture 'userdata')), ('/DMyDefaultDir=' + $install),
    '/DMyPrivilegesRequired=lowest', '/DMyCompression=none', '/DMySolidCompression=no',
    (Join-Path $repository 'installer\iPhoneMirror.iss'))
$compilation = Start-TestProcess $inno $arguments
try { Wait-TestProcess $compilation } finally { $compilation.Dispose() }

$old = $null
$setup = $null
$uninstaller = $null
try {
    [IO.File]::Copy((Join-Path $oldPayload 'iPhoneMirror.exe'), (Join-Path $install 'iPhoneMirror.exe'))
    $old = Start-TestProcess (Join-Path $install 'iPhoneMirror.exe') @()
    Wait-TestFile (Join-Path $install 'old-ready.txt')
    # Use the production installer and its actual Restart Manager / desktop
    # restart logic. Unique user-mode identities isolate registry and shortcuts.
    $testRunner = Join-Path $repository "src\App.Logic.Tests\bin\$Configuration\net10.0-windows10.0.19041.0\IPhoneMirror.App.Logic.Tests.exe"
    if (-not [IO.File]::Exists($testRunner)) {
        throw "Build App.Logic.Tests with configuration $Configuration before running this test."
    }
    $runnerArguments = @('--updater-package-e2e', $fixture, $install, $identity)
    if ($ElevatedInstallerBootstrap) { $runnerArguments += '--elevated' }
    $setup = Start-TestProcess $testRunner $runnerArguments
    Wait-TestProcess $setup 60000
    if (-not $old.WaitForExit(10000)) { throw 'Setup left the close-to-tray process running.' }
    Wait-TestFile (Join-Path $install 'restarted.txt')
    $expected = (Get-FileHash -LiteralPath (Join-Path $payload 'iPhoneMirror.exe')).Hash
    if ((Get-FileHash -LiteralPath (Join-Path $install 'iPhoneMirror.exe')).Hash -ne $expected) {
        throw 'Setup did not install the expected executable.'
    }
    "PASS: real Setup replaced a running close-to-tray executable and restarted the new version. Evidence: $fixture"
}
finally {
    if ($null -ne $old) {
        if (-not $old.HasExited) { $old.Kill(); $old.WaitForExit() }
        $old.Dispose()
    }
    if ($null -ne $setup) {
        if (-not $setup.HasExited) { $setup.Kill(); $setup.WaitForExit() }
        $setup.Dispose()
    }
    $uninstallPath = Join-Path $install 'unins000.exe'
    if ([IO.File]::Exists($uninstallPath)) {
        $uninstaller = Start-TestProcess $uninstallPath @('/VERYSILENT', '/SUPPRESSMSGBOXES',
            '/NORESTART', '/KEEPUSERDATA=1', ('/LOG=' + (Join-Path $fixture 'uninstall.log')))
        try { Wait-TestProcess $uninstaller } finally { $uninstaller.Dispose() }
    }
}

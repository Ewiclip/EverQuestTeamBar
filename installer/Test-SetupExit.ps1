#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][string]$TeamBarFixtureExecutable,
    [Parameter(Mandatory)][string]$NotificationOnlyBeta4Executable
)
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -ne 'Desktop') { throw 'Run under the same 64-bit Windows PowerShell 5.1 host as Setup.' }
. (Join-Path $PSScriptRoot 'Close-TeamBarForSetup.ps1')
$root = [IO.Path]::GetFullPath($OutputDirectory)
if ([IO.Directory]::Exists($root)) { throw 'Use a fresh fixture directory.' }
[void][IO.Directory]::CreateDirectory($root)
$request = Join-Path $root 'request'
[IO.File]::WriteAllText($request, 'active')
if (-not (Test-NotificationOnlyBeta4 (Resolve-Path -LiteralPath $NotificationOnlyBeta4Executable).Path)) { throw 'Beta.4 release identity mismatch.' }

# This Team Bar entry point hosts a dispatcher-only fixture and an isolated
# instance scope. Send exclusively to its child PID, never to a live install.
$fixture = (Resolve-Path -LiteralPath $TeamBarFixtureExecutable).Path
$start = [Diagnostics.ProcessStartInfo]::new($fixture, ('--app-instance-fixture "' + (Join-Path $root 'scope') + '" "' + (Join-Path $root 'report.json') + '" --installer-exit-fixture'))
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.RedirectStandardOutput = $true
$receiver = [Diagnostics.Process]::Start($start)
try {
    $ready = $receiver.StandardOutput.ReadLineAsync()
    if (-not $ready.Wait(8000) -or -not $ready.Result -or $receiver.HasExited) { throw 'No .NET fixture readiness signal.' }
    if (-not (Send-InstallerExitEvent $receiver $request)) { throw 'WinPS could not open the .NET event.' }
    $retry = $receiver.StandardOutput.ReadLineAsync()
    if (-not $retry.Wait(8000) -or $retry.Result -ne 'retry') { throw 'The .NET fixture did not receive the request.' }
    if (-not (Send-InstallerExitEvent $receiver $request) -or -not $receiver.WaitForExit(8000) -or $receiver.ExitCode -ne 0) { throw 'Retry failed.' }
    if (-not [IO.File]::Exists((Join-Path $root 'report.json'))) { throw 'Missing orderly-exit report.' }
    Write-Output 'PASS Windows PowerShell 5.1 to .NET installer event, save refusal, and retry'
} finally { $receiver.Dispose() }

# Small same-user WPF stand-in for beta.4. The second launch reopens Settings
# in the first process. No keyboard, mouse, live profiles, or EQ clients are used.
$source = @'
using System;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
class LegacyExitFixture {
    static string PathFor(string name) { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, name); }
    [STAThread] public static void Main() {
        if (File.Exists(PathFor("ready"))) { File.WriteAllText(PathFor("show"), "1"); return; }
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var exit = new Button { Content = "Exit Team Bar", Name = "ExitTeamBarButton" };
        AutomationProperties.SetName(exit, "Exit Team Bar");
        var window = new Window { Content = exit, ShowActivated = false, ShowInTaskbar = false,
            Top = -30000, Left = -30000, Width = 160, Height = 80 };
        exit.Click += delegate { File.WriteAllText(PathFor("exited"), "1"); app.Shutdown(); };
        var timeout = DateTime.UtcNow.AddSeconds(15);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        timer.Tick += delegate {
            if (DateTime.UtcNow > timeout) app.Shutdown(1);
            if (!window.IsVisible && File.Exists(PathFor("show"))) window.Show();
        };
        timer.Start();
        File.WriteAllText(PathFor("ready"), "1");
        app.Run();
    }
}
'@
$wpf = Join-Path ([Runtime.InteropServices.RuntimeEnvironment]::GetRuntimeDirectory()) 'WPF'
$refs = @('WindowsBase','PresentationCore','PresentationFramework','UIAutomationTypes') | ForEach-Object { Join-Path $wpf ($_.ToString() + '.dll') }
$dummy = Join-Path $root 'LegacyExitFixture.exe'
Add-Type -TypeDefinition $source -ReferencedAssemblies (@('System.dll','System.Xaml.dll') + $refs) -OutputAssembly $dummy -OutputType WindowsApplication
if (Test-NotificationOnlyBeta4 $dummy) { throw 'Non-release executable accepted as beta.4.' }
$legacy = Start-Process -FilePath $dummy -PassThru -WindowStyle Hidden
try {
    for ($i = 0; $i -lt 40 -and -not [IO.File]::Exists((Join-Path $root 'ready')); $i++) { Start-Sleep -Milliseconds 100 }
    if (-not [IO.File]::Exists((Join-Path $root 'ready'))) { throw 'Synthetic WPF owner was not ready.' }
    Request-Beta4Exit $legacy $dummy $request
    if (-not $legacy.WaitForExit(3000) -or $legacy.ExitCode -ne 0 -or -not [IO.File]::Exists((Join-Path $root 'exited'))) { throw 'Beta.4-style Settings Exit button was not invoked.' }
    Write-Output 'PASS tray-only beta.4-style Settings forwarding and WPF Exit command'
} finally { $legacy.Dispose() }

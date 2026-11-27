#Requires -Version 5.1
param([string]$ApplicationPath, [string]$RequestFile)

# The installer's request file expires when its bounded wait ends. UI Automation
# can block on a hung desktop app, so Setup runs this helper out of process.
function Test-ActiveCloseRequest($Target, [string]$Request) {
    return [IO.File]::Exists($Request) -and -not $Target.HasExited
}

function Test-NotificationOnlyBeta4([string]$Executable) {
    # Only this released unsigned build is known to have a normal Exit command
    # that cannot start a second, queued update. Other builds must opt in to IPC.
    $managed = [IO.Path]::ChangeExtension($Executable, '.dll')
    return [IO.File]::Exists($managed) -and
        (Get-FileHash -LiteralPath $Executable).Hash -eq '83A642405A4AC238409ABA932EECF37C3B03DBFCB69540DACC2BE00B16862F0A' -and
        (Get-FileHash -LiteralPath $managed).Hash -eq 'BD19AA44E0E1C954F450AEE33665E83B5E8F03EAF54A3754E3E9ADA88A0F7B5E'
}

function Find-SettingsExitButton([int]$ProcessId) {
    $element = [Windows.Automation.AutomationElement]
    $scope = [Windows.Automation.TreeScope]
    $owner = [Windows.Automation.PropertyCondition]::new($element::ProcessIdProperty, $ProcessId)
    $buttonId = [Windows.Automation.PropertyCondition]::new($element::AutomationIdProperty, 'ExitTeamBarButton')
    foreach ($window in $element::RootElement.FindAll($scope::Children, $owner)) {
        $button = $window.FindFirst($scope::Descendants, $buttonId)
        if ($button -and $button.Current.ProcessId -eq $ProcessId -and $button.Current.IsEnabled -and
            $button.Current.ControlType -eq [Windows.Automation.ControlType]::Button) { return $button }
    }
}

function Request-Beta4Exit($Target, [string]$Executable, [string]$Request) {
    Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes
    $button = Find-SettingsExitButton $Target.Id
    if (-not $button -and (Test-ActiveCloseRequest $Target $Request)) {
        # Starting the exact installed exe asks its existing instance to open
        # Settings when the user has closed all windows to the tray.
        $forwarder = Start-Process -FilePath $Executable -WorkingDirectory (Split-Path -Parent $Executable) -WindowStyle Hidden -PassThru
        try {
            if (-not $forwarder.WaitForExit(5000)) { return }
        } finally { $forwarder.Dispose() }
        for ($attempt = 0; $attempt -lt 15 -and (Test-ActiveCloseRequest $Target $Request); $attempt++) {
            $button = Find-SettingsExitButton $Target.Id
            if ($button) { break }
            Start-Sleep -Milliseconds 200
        }
    }
    if ($button -and (Test-ActiveCloseRequest $Target $Request)) {
        $invoke = $button.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern)
        if (Test-ActiveCloseRequest $Target $Request) { $invoke.Invoke() }
    }
}

function Send-InstallerExitEvent($Target, [string]$Request) {
    $signal = $null
    if (-not [Threading.EventWaitHandle]::TryOpenExisting(('Local\TeamBar.Setup.Exit.' + $Target.Id),
        [Security.AccessControl.EventWaitHandleRights]::Modify, [ref]$signal)) { return $false }
    try {
        if (Test-ActiveCloseRequest $Target $Request) { [void]$signal.Set() }
        return $true
    } finally { $signal.Dispose() }
}

function Request-InstalledAppExit([string]$Executable, [string]$Request) {
    $session = [Diagnostics.Process]::GetCurrentProcess().SessionId
    $rows = @(Get-CimInstance Win32_Process -Filter "Name='TeamBar.exe'" -OperationTimeoutSec 4)
    if ($rows.Count -eq 0) { return 'closed' }
    if ($rows.Count -ne 1 -or $rows[0].SessionId -ne $session -or $rows[0].ExecutablePath -ne $Executable) { return 'manual' }
    $target = [Diagnostics.Process]::GetProcessById([int]$rows[0].ProcessId)
    try {
        $null = $target.Handle # Pin the PID while checking its path and signaling it.
        if ($target.SessionId -ne $session -or $target.MainModule.FileName -ne $Executable) { return 'manual' }
        $owner = Invoke-CimMethod -InputObject $rows[0] -MethodName GetOwnerSid -OperationTimeoutSec 4
        if ($owner.ReturnValue -ne 0 -or $owner.Sid -ne [Security.Principal.WindowsIdentity]::GetCurrent().User.Value) { return 'manual' }
        if (-not (Send-InstallerExitEvent $target $Request)) {
            if (-not (Test-NotificationOnlyBeta4 $Executable)) { return 'manual' }
            Request-Beta4Exit $target $Executable $Request
        }

        if ($target.WaitForExit(10000)) { return 'closed' }
        return 'manual'
    } finally { $target.Dispose() }
}

if ($MyInvocation.InvocationName -ne '.') {
    $ErrorActionPreference = 'Stop'
    $result = 'manual'
    try { $result = Request-InstalledAppExit ([IO.Path]::GetFullPath($ApplicationPath)) $RequestFile }
    catch { Write-Verbose $_.Exception.Message }
    if ([IO.File]::Exists($RequestFile)) {
        [IO.File]::WriteAllText($RequestFile + '.writing', $result, [Text.Encoding]::ASCII)
        [IO.File]::Move($RequestFile + '.writing', $RequestFile + '.done')
    }
}

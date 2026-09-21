# Only disposable GitHub Windows runners. Never use this on an everyday profile.
param(
    [Parameter(Mandatory)][string]$Installer,
    [string]$Report = 'test-results/install-tests.txt',
    [switch]$LifecycleSmoke,
    [string]$LegacyInstaller = '',
    [string]$LegacySha256 = ''
)
$ErrorActionPreference = 'Stop'
if (-not $IsWindows -or $env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') {
    throw 'Installer regression tests require an ephemeral GitHub-hosted Windows runner.'
}
if ([bool]$LegacyInstaller -xor [bool]$LegacySha256) {
    throw 'Supply both LegacyInstaller and LegacySha256, or neither.'
}
if ($LegacyInstaller) {
    if ($LegacySha256 -notmatch '^[A-Fa-f0-9]{64}$') { throw 'LegacySha256 must be a SHA256 digest.' }
    $LegacyInstaller = (Resolve-Path $LegacyInstaller).Path
    if ((Get-FileHash $LegacyInstaller -Algorithm SHA256).Hash -ne $LegacySha256) {
        throw 'Legacy installer checksum mismatch.'
    }
}
$Installer = (Resolve-Path $Installer).Path
$Report = [IO.Path]::GetFullPath($Report)
New-Item -ItemType Directory -Force (Split-Path $Report) | Out-Null
$results = [Collections.Generic.List[string]]::new()
$root = Join-Path ([IO.Path]::GetTempPath()) ('Grimoire-install-test-' + [Guid]::NewGuid().ToString('N'))
$install = Join-Path $root 'installed'
$data = Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'Grimoire'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{B6E0C3A2-7D3F-4C7E-9B7A-2F5A6C1D8E90}_is1'
$exe = Join-Path $install 'Grimoire.exe'
$sendTo = Join-Path ([Environment]::GetFolderPath('SendTo')) 'Grimoire.lnk'
$script:started = [Collections.Generic.List[Diagnostics.Process]]::new()
function Note([string]$value) { $results.Add($value); Write-Host $value }
function Assert([bool]$condition, [string]$message) { if (-not $condition) { throw $message } }
function Run-Installer([string]$file, [string[]]$extra = @(), [bool]$expectSuccess = $true) {
    $arguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', ('/DIR="' + $install + '"')) + $extra
    $process = Start-Process -FilePath $file -ArgumentList $arguments -PassThru
    if (-not $process.WaitForExit(60000)) { throw 'Installer did not finish within 60 seconds.' }
    if ($expectSuccess) { Assert ($process.ExitCode -eq 0) 'Installer returned a failure exit code.' }
    else { Assert ($process.ExitCode -ne 0) 'Busy target was incorrectly accepted.' }
}
function Startup-Value { (Get-ItemProperty -Path $runKey -Name Grimoire -ErrorAction SilentlyContinue).Grimoire }
function Assert-Data { Assert ((Get-FileHash (Join-Path $data 'grimoire-settings.ini')).Hash -eq $script:dataHash) 'Settings changed during install/uninstall.' }
function Uninstall {
    $uninstaller = Join-Path $install 'unins000.exe'
    if (Test-Path $uninstaller) {
        $process = Start-Process -FilePath $uninstaller -ArgumentList '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART' -PassThru
        if (-not $process.WaitForExit(30000)) { throw 'Uninstall did not finish without prompts.' }
        Assert ($process.ExitCode -eq 0) 'Uninstall failed.'
    }
}
# Reject any existing app/profile state, even on CI. The legacy installer globally
# taskkills app names, so it must only ever run on a clean hosted runner.
Assert (-not (Test-Path $sendTo)) 'Refusing to overwrite existing Grimoire SendTo shortcut.'
Assert (-not (Test-Path $data)) 'Refusing to touch existing Grimoire user data.'
Assert (-not (Test-Path $uninstallKey)) 'Refusing to replace an existing registered install.'
Assert (-not (Startup-Value)) 'Refusing to modify existing Grimoire startup registration.'
Assert (-not (Get-Process -Name Grimoire -ErrorAction SilentlyContinue)) 'Refusing to run while Grimoire is already running.'
New-Item -ItemType Directory -Force $root, $data | Out-Null
try {
    $settings = Join-Path $data 'grimoire-settings.ini'
    "[general]`nfirst_run_done=0`nfixture=synthetic-install-data" | Set-Content $settings
    $script:dataHash = (Get-FileHash $settings).Hash
    Run-Installer $Installer @('/TASKS=""')
    Assert (Test-Path $exe) 'Clean installation did not create executable.'
    Assert (-not (Startup-Value)) 'Clean install unexpectedly enabled startup.'
    Assert (-not (Get-Process -Name Grimoire -ErrorAction SilentlyContinue)) 'Silent install launched the app.'
    Assert ((Get-Item $exe).VersionInfo.ProductVersion -eq '2.2.0-rc.1') 'Installed candidate product version incorrect.'
    Assert ((Get-Item $exe).VersionInfo.FileVersion -eq '2.2.0.0') 'Installed candidate numeric file version incorrect.'
    Assert-Data
    $candidateBackup = Join-Path $root 'candidate-backup.exe'
    Copy-Item $exe $candidateBackup
    Note 'PASS: clean silent per-user install, startup opt-out, RC product/numeric versions, no app launch.'

    Run-Installer $Installer @('/TASKS=startup')
    Assert ((Startup-Value) -eq ('"' + $exe + '"')) 'Startup opt-in was not applied.'
    Remove-ItemProperty $runKey -Name Grimoire
    Run-Installer $Installer
    Assert (-not (Startup-Value)) 'Silent reinstall forgot app-disabled startup.'
    New-ItemProperty $runKey -Name Grimoire -Value ('"' + $exe + '"') -PropertyType String -Force | Out-Null
    Run-Installer $Installer
    Assert ((Startup-Value) -eq ('"' + $exe + '"')) 'Silent reinstall forgot app-enabled startup.'
    Run-Installer $Installer @('/TASKS=""')
    Assert (-not (Startup-Value)) 'Explicit startup opt-out was not applied.'
    Assert-Data
    Note 'PASS: same-version replacement (not version upgrade), startup opt-in/opt-out and app preference preservation.'

    # A locked executable stands in for an unresponsive/legacy process; no hooks.
    $locked = [IO.File]::Open($exe, 'Open', 'Read', 'Read')
    try { Run-Installer $Installer @() $false } finally { $locked.Dispose() }
    Assert-Data
    Note 'PASS: busy executable safely refuses replacement within bounded helper timeout.'

    if ($LifecycleSmoke) {
        # Explicitly enables candidate lifecycle only, with no hooks, providers,
        # router, clipboard tracking, input injection, setup UI or network.
        $isolated = Join-Path $root 'lifecycle-data'
        New-Item -ItemType Directory $isolated | Out-Null
        New-Item -ItemType File (Join-Path $isolated 'grimoire-lifecycle-fixture') | Out-Null
        $env:GRIMOIRE_HOME = $isolated
        $env:GRIMOIRE_LIFECYCLE_SMOKE = '1'
        $app = Start-Process $exe -ArgumentList '--lifecycle-smoke' -PassThru
        $script:started.Add($app)
        try {
            $ready = Join-Path $isolated 'lifecycle-ready'
            $deadline = [DateTime]::UtcNow.AddSeconds(15)
            while (-not (Test-Path $ready) -and -not $app.HasExited -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 100 }
            Assert (Test-Path $ready) 'Candidate lifecycle did not become ready.'
            # A helper directed elsewhere must leave the installed app alive.
            $wrong = Start-Process $exe -ArgumentList ('--quit-target "' + (Join-Path $root 'portable/Grimoire.exe') + '"') -PassThru
            Assert ($wrong.WaitForExit(5000)) 'Wrong-path helper did not terminate.'
            Assert (-not $app.HasExited) 'Wrong-path quit closed the installed instance.'
            Run-Installer $Installer
            Assert ($app.WaitForExit(5000)) 'Graceful candidate shutdown did not finish.'
            Assert ($app.ExitCode -eq 0) 'Candidate lifecycle did not exit cleanly.'
            Assert (Test-Path (Join-Path $isolated 'lifecycle-closed')) 'Actual tray teardown did not finish.'
            Note 'PASS: explicit isolated candidate lifecycle, wrong-path isolation, installer graceful quit and bounded exit.'
        } finally {
            Remove-Item Env:GRIMOIRE_HOME, Env:GRIMOIRE_LIFECYCLE_SMOKE -ErrorAction SilentlyContinue
        }
    } else { Note 'PENDING: candidate lifecycle smoke was not requested.' }
    $shell = New-Object -ComObject WScript.Shell
    $link = $shell.CreateShortcut($sendTo)
    $link.TargetPath = $exe; $link.Arguments = '--files'; $link.Save()
    New-ItemProperty $runKey -Name Grimoire -Value ('"' + $exe + '"') -PropertyType String -Force | Out-Null
    Uninstall
    Assert-Data
    Assert (-not (Test-Path $sendTo)) 'Uninstall left its own SendTo shortcut.'
    Assert (-not (Test-Path $exe)) 'Uninstall did not remove installed executable.'
    Assert (-not (Startup-Value)) 'Uninstall left its startup registration.'
    Note 'PASS: silent uninstall retained synthetic data without deletion prompts.'

    if ($LifecycleSmoke) {
        # Exercise unrelated portable preservation on every public CI run.
        Run-Installer $Installer @('/TASKS=""')
        $portableDir = Join-Path $root 'unrelated-portable'
        $portableData = Join-Path $root 'unrelated-data'
        New-Item -ItemType Directory $portableDir, $portableData | Out-Null
        $portableExe = Join-Path $portableDir 'Grimoire.exe'
        Copy-Item $candidateBackup $portableExe
        New-Item -ItemType File (Join-Path $portableData 'grimoire-lifecycle-fixture') | Out-Null
        $env:GRIMOIRE_HOME = $portableData
        $env:GRIMOIRE_LIFECYCLE_SMOKE = '1'
        try {
            $portable = Start-Process $portableExe -ArgumentList '--lifecycle-smoke' -PassThru
            $script:started.Add($portable)
        } finally { Remove-Item Env:GRIMOIRE_HOME, Env:GRIMOIRE_LIFECYCLE_SMOKE -ErrorAction SilentlyContinue }
        $deadline = [DateTime]::UtcNow.AddSeconds(15)
        while (-not (Test-Path (Join-Path $portableData 'lifecycle-ready')) -and -not $portable.HasExited -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 100 }
        Assert (Test-Path (Join-Path $portableData 'lifecycle-ready')) 'Unrelated portable fixture did not start.'
        New-ItemProperty $runKey -Name Grimoire -Value ('"' + $portableExe + '"') -PropertyType String -Force | Out-Null
        $link = $shell.CreateShortcut($sendTo)
        $link.TargetPath = $portableExe; $link.Arguments = '--files'; $link.Save()
        Uninstall
        Assert-Data
        Assert (-not $portable.HasExited) 'Uninstall killed an unrelated portable instance.'
        Assert ((Startup-Value) -eq ('"' + $portableExe + '"')) 'Uninstall removed portable startup registration.'
        Assert (Test-Path $sendTo) 'Uninstall removed portable SendTo shortcut.'
        $quit = Start-Process $portableExe -ArgumentList '--quit' -PassThru
        Assert ($quit.WaitForExit(15000)) 'Portable own-path quit was unbounded.'
        Assert ($quit.ExitCode -eq 0 -and $portable.WaitForExit(5000)) 'Portable did not close gracefully.'
        Remove-ItemProperty $runKey -Name Grimoire
        Remove-Item $sendTo
        Note 'PASS: uninstall preserved unrelated portable process, startup and SendTo; direct --quit exited cleanly.'
    } else { Note 'NOT RUN: unrelated portable lifecycle requires -LifecycleSmoke.' }

    if ($LegacyInstaller) {
        $old = Join-Path $root 'old'
        New-Item -ItemType Directory $old | Out-Null
        Assert (-not (Get-Process -Name Grimoire -ErrorAction SilentlyContinue)) 'Legacy installer requires no running Grimoire instances.'
        Run-Installer $LegacyInstaller @('/TASKS=""')
        Assert ((Get-Item $exe).VersionInfo.ProductVersion -like '2.1.0*') 'Historic installation version mismatch.'
        Assert (-not (Get-Process -Name Grimoire -ErrorAction SilentlyContinue)) 'Legacy silent install unexpectedly launched the app.'
        $backup = Join-Path $old 'Grimoire-backup.exe'
        Copy-Item $exe $backup
        $oldHash = (Get-FileHash $backup).Hash
        Run-Installer $Installer
        Assert ((Get-Item $exe).VersionInfo.ProductVersion -eq '2.2.0-rc.1') 'Actual 2.1-to-candidate upgrade failed.'
        Assert-Data
        Copy-Item $backup $exe -Force
        Assert ((Get-FileHash $exe).Hash -eq $oldHash) 'Old executable restoration failed.'
        Assert-Data
        # Candidate uninstaller must safely handle a restored 2.1 executable
        # without executing that executable with an unknown --quit option.
        Uninstall
        Assert-Data
        Note 'PASS: supplied checksum-verified 2.1.0 -> candidate upgrade, settings retention, old executable restore, candidate uninstall.'
    } else { Note 'NOT RUN: 2.1.0 upgrade/old executable restore; no explicit legacy installer fixture supplied.' }
    Note 'LIMIT: hosted runner user context; standard non-admin desktop, SmartScreen and interactive hooks are not certified.'
} catch {
    Note ('FAIL: ' + $_.Exception.Message)
    throw
} finally {
    # Only processes explicitly started by this test may be stopped during cleanup.
    foreach ($process in $script:started) {
        if (-not $process.HasExited) { $process.Kill(); $process.WaitForExit(5000) | Out-Null }
        $process.Dispose()
    }
    try { Uninstall } catch { Note 'CLEANUP: candidate uninstall could not finish; ephemeral runner will be discarded.' }
    if ((Startup-Value) -like ('*' + $root + '*')) { Remove-ItemProperty $runKey -Name Grimoire }
    if (Test-Path $sendTo) {
        $cleanupShell = New-Object -ComObject WScript.Shell
        if ($cleanupShell.CreateShortcut($sendTo).TargetPath.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) { Remove-Item $sendTo }
    }
    if (Test-Path $root) { Remove-Item $root -Recurse -Force }
    if (Test-Path $data) { Remove-Item $data -Recurse -Force }
    $results | Set-Content $Report -Encoding utf8
}

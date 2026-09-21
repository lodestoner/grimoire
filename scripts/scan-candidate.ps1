param([Parameter(Mandatory)][string]$Directory, [string]$Report = 'test-results/defender.txt')
$ErrorActionPreference = 'Stop'
$Directory = (Resolve-Path $Directory).Path
New-Item -ItemType Directory -Force (Split-Path $Report) | Out-Null
$lines = [Collections.Generic.List[string]]::new()
try {
    if (-not (Get-Command Get-MpComputerStatus -ErrorAction SilentlyContinue)) {
        $lines.Add('UNAVAILABLE: Microsoft Defender cmdlets are not installed. No scan claimed.')
        return
    }
    try { $status = Get-MpComputerStatus }
    catch { $lines.Add('UNAVAILABLE: Defender status API is not operational on this runner. No scan claimed.'); return }
    $lines.Add("Defender engine: $($status.AMEngineVersion); antivirus: $($status.AntivirusEnabled); real-time: $($status.RealTimeProtectionEnabled)")
    $lines.Add("Signature: $($status.AntivirusSignatureVersion); updated: $($status.AntivirusSignatureLastUpdated)")
    if (-not $status.AntivirusEnabled) { $lines.Add('UNAVAILABLE: Defender antivirus is disabled in runner state. No settings changed.'); return }
    $start = Get-Date
    Start-MpScan -ScanType CustomScan -ScanPath $Directory
    $detections = @(Get-MpThreatDetection | Where-Object { $_.InitialDetectionTime -ge $start -and @($_.Resources | Where-Object { $_ -like "*$Directory*" }).Count -gt 0 })
    if ($detections.Count -gt 0) { $lines.Add("FAIL: Defender reported $($detections.Count) candidate detection(s)."); throw 'Defender detected a candidate threat.' }
    $lines.Add('PASS: custom scan of exact final candidate directory completed with no new candidate detections.')
    $lines.Add('SmartScreen download reputation/prompt remains unobserved; this scan does not certify it.')
} catch {
    $lines.Add('ERROR: Defender scan could not be certified: ' + $_.Exception.Message)
    throw
} finally { $lines | Set-Content $Report -Encoding utf8; $lines | Write-Host }

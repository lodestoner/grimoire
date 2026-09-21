# Build a source candidate locally. No tags, releases, uploads or installation.
param([switch]$SkipInstaller, [switch]$ValidateOnly, [string]$Version = '')
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$requiredSdk = [string]((Get-Content "$root/global.json" -Raw | ConvertFrom-Json).sdk.version)
$actualSdk = (& dotnet --version).Trim()
if ($LASTEXITCODE -ne 0 -or $actualSdk -ne $requiredSdk) { throw "Grimoire requires SDK $requiredSdk; selected $actualSdk." }
$source = [xml](Get-Content "$root/Version.props" -Raw)
$ver = [string]$source.Project.PropertyGroup.GrimoireVersion
if ($ver -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)-rc\.(0|[1-9]\d*)$') { throw 'Invalid source candidate version.' }
$numeric = ($ver -split '-')[0] + '.0'
if ($Version -and $Version -cne $ver) { throw "Version mismatch: requested $Version, source declares $ver." }
$evaluated = (& dotnet msbuild "$root/app/Grimoire/Grimoire.csproj" -getProperty:Version,AssemblyVersion,FileVersion,InformationalVersion | ConvertFrom-Json).Properties
if ($LASTEXITCODE -ne 0 -or $evaluated.Version -cne $ver -or $evaluated.InformationalVersion -cne $ver -or $evaluated.FileVersion -ne $numeric -or $evaluated.AssemblyVersion -ne $numeric) { throw 'Evaluated application versions disagree with Version.props.' }
Write-Host "Grimoire $ver (Windows $numeric), SDK $actualSdk"
if ($ValidateOnly) { return }

$python = @('python', 'python3') | ForEach-Object { Get-Command $_ -ErrorAction SilentlyContinue } | Select-Object -First 1
if (-not $python) { throw 'Python 3 is required for package/license audits.' }
$dist = Join-Path $root 'dist'
# dist is dedicated, ignored build output. Never use arbitrary user paths here.
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
New-Item -ItemType Directory -Force "$dist/candidate", "$dist/audit", "$dist/publish" | Out-Null
dotnet restore "$root/app/Grimoire/Grimoire.csproj" --locked-mode --nologo
if ($LASTEXITCODE -ne 0) { throw 'Locked restore failed.' }
# Map compile-time source paths to a generic root; no workstation paths in artifacts.
dotnet publish "$root/app/Grimoire/Grimoire.csproj" -c Release --no-restore --nologo -o "$dist/publish" "-p:PathMap=$root=/_/Grimoire"
if ($LASTEXITCODE -ne 0) { throw 'Single-file publish failed.' }
Copy-Item "$dist/publish/Grimoire.exe" "$dist/Grimoire.exe"
dotnet publish "$root/app/Grimoire/Grimoire.csproj" -c Release --no-restore --nologo -p:PublishSingleFile=false -o "$dist/audit" "-p:PathMap=$root=/_/Grimoire"
if ($LASTEXITCODE -ne 0) { throw 'Unbundled audit publish failed.' }
& $python.Source "$root/scripts/package-notices.py" --output $dist
if ($LASTEXITCODE -ne 0) { throw 'License inventory failed.' }
Copy-Item "$root/packaging/README.txt" "$dist/README.txt"
Copy-Item "$root/LICENSE" "$dist/LICENSE"
$compilerVersion = $null
if (-not $SkipInstaller) {
    $iscc = @("${env:ProgramFiles(x86)}/Inno Setup 6/ISCC.exe", "$env:LOCALAPPDATA/Programs/Inno Setup 6/ISCC.exe", "$env:ProgramFiles/Inno Setup 6/ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $iscc) { throw 'Inno Setup 6 is required to build the local installer.' }
    # ISCC.exe does not always carry a usable version resource; record what it reports and require
    # Inno Setup 6 only when a version is actually readable.
    $info = (Get-Item $iscc).VersionInfo
    $compilerVersion = @($info.ProductVersion, $info.FileVersion) |
        Where-Object { [regex]::IsMatch([string]$_, '^[1-9]\d*(\.\d+){1,3}') } | Select-Object -First 1
    if (-not $compilerVersion) { $compilerVersion = 'unreported' }
    elseif ([version][regex]::Match([string]$compilerVersion, '^\d+(\.\d+){1,3}').Value -lt [version]'6.0') {
        throw "Inno Setup 6 is required; found '$compilerVersion'."
    }
    & $iscc /Q "/DAppVersion=$ver" "/DNumericVersion=$numeric" "$root/installer/Grimoire.iss"
    if ($LASTEXITCODE -ne 0) { throw 'Inno Setup compilation failed.' }
    Copy-Item "$dist/Grimoire-Setup-$ver.exe" "$dist/candidate/"
}
$candidate = "$dist/candidate"
Copy-Item "$dist/Grimoire.exe" "$candidate/Grimoire-$ver-portable.exe"
Copy-Item "$dist/README.txt", "$dist/LICENSE", "$dist/THIRD-PARTY-NOTICES.txt" $candidate
Copy-Item "$dist/licenses" "$candidate/licenses" -Recurse
Compress-Archive -Path "$dist/Grimoire.exe", "$dist/README.txt", "$dist/LICENSE", "$dist/THIRD-PARTY-NOTICES.txt", "$dist/licenses" -DestinationPath "$candidate/Grimoire-$ver-portable.zip"
$commit = (& git -C $root rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $commit -notmatch '^[a-f0-9]{40}$') { throw 'Cannot determine build commit.' }
$dirty = [bool](& git -C $root status --porcelain)
$files = @(Get-ChildItem $candidate -Recurse -File | Sort-Object FullName | ForEach-Object {
    [ordered]@{ name = [IO.Path]::GetRelativePath($candidate, $_.FullName).Replace('\', '/'); sha256 = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower(); bytes = $_.Length }
})
[ordered]@{ version = $ver; windowsVersion = $numeric; commit = $commit; workingTreeDirty = $dirty; sdk = $actualSdk; innoCompiler = $compilerVersion; runtime = 'win-x64'; channel = 'source-candidate'; files = $files } |
    ConvertTo-Json -Depth 5 | Set-Content "$candidate/build-manifest.json" -Encoding utf8
Get-ChildItem $candidate -Recurse -File | Sort-Object FullName | ForEach-Object {
    '{0}  {1}' -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower(), [IO.Path]::GetRelativePath($candidate, $_.FullName).Replace('\', '/')
} | Set-Content "$candidate/SHA256SUMS.txt" -Encoding ascii
if (-not $SkipInstaller) {
    & $python.Source "$root/scripts/audit-package.py" --candidate $candidate --staging "$dist/audit" --version $ver
    if ($LASTEXITCODE -ne 0) { throw 'Candidate package audit failed.' }
} else {
    & $python.Source "$root/scripts/audit-package.py" --candidate $candidate --staging "$dist/audit" --version $ver --portable-only
    if ($LASTEXITCODE -ne 0) { throw 'Portable preparation audit failed.' }
}
Write-Host "Prepared source candidate in dist/candidate. No install or publication performed."

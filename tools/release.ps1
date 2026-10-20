<#
.SYNOPSIS
  Builds, signs and optionally publishes an EizoSwitcher release.

.EXAMPLE
  .\tools\release.ps1 -Version 1.0.0             # build, sign, package
  .\tools\release.ps1 -Version 1.0.0 -Publish    # + GitHub release (needs gh)
  .\tools\release.ps1 -Version 1.0.0 -NoSign     # unsigned test build (cannot be published)

.NOTES
  Output: artifacts\<version>\EizoSwitcher-Setup.exe and EizoSwitcher-win-x64.zip (each + .sha256) – the asset names the auto-updater expects.
  Needs Inno Setup 6 (winget install JRSoftware.InnoSetup). Release notes: artifacts\<version>\notes.md (optional).

  Signing: Microsoft Artifact Signing (account and certificate profile in tools\signing.json).
    winget install -e --id Microsoft.Azure.ArtifactSigningClientTools
    az login        (the account needs the "Artifact Signing Certificate Profile Signer" role)
  Signs both exes, the setup and its uninstaller, timestamps them and verifies every signature.
  Every published release must be signed: a signed EizoSwitcher only accepts updates from the same publisher.
#>
param(
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version,
    [switch]$Publish,
    [switch]$NoSign
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $root "artifacts\$Version"
$pkg = Join-Path $out 'package'
if ($Publish -and $NoSign) { throw 'Releases must be signed – -Publish cannot be combined with -NoSign.' }

# ---------------------------------------------------------------- signing setup (checked before building)
$sign = -not $NoSign
$timestamp = 'http://timestamp.acs.microsoft.com'
if ($sign) {
    $metadata = Join-Path $PSScriptRoot 'signing.json'
    $signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue |
        Where-Object { $_.Directory.Parent.Name -match '^\d+(\.\d+)+$' } | Sort-Object { [version]$_.Directory.Parent.Name } -Descending | Select-Object -First 1 -ExpandProperty FullName
    if (-not $signtool) { throw 'signtool.exe not found (install the Windows SDK)' }
    $dlib = @($env:ARTIFACT_SIGNING_DLIB,
              "$env:LOCALAPPDATA\Microsoft\MicrosoftArtifactSigningClientTools\Azure.CodeSigning.Dlib.dll",
              "${env:ProgramFiles(x86)}\Microsoft\ArtifactSigningClientTools\bin\Azure.CodeSigning.Dlib.dll") |
        Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
    if (-not $dlib) { throw 'Artifact Signing dlib not found (winget install -e --id Microsoft.Azure.ArtifactSigningClientTools)' }
    az account show --only-show-errors -o none 2>$null
    if ($LASTEXITCODE) { throw 'Not signed in to Azure – run "az login" first.' }
    $signArgs = @('sign', '/fd', 'SHA256', '/tr', $timestamp, '/td', 'SHA256', '/dlib', $dlib, '/dmdf', $metadata,
                  '/d', 'EizoSwitcher', '/du', 'https://github.com/s1nn3r/EizoSwitcher')
}
function Sign([string[]]$files) {
    if (-not $sign) { return }
    Write-Host "== Signing $(($files | Split-Path -Leaf) -join ', ')" -ForegroundColor Cyan
    & $signtool @signArgs @files
    if ($LASTEXITCODE) { throw 'signing failed' }
}
function Verify([string[]]$files) {
    if (-not $sign) { return }
    foreach ($f in $files) {
        & $signtool verify /pa /q $f
        if ($LASTEXITCODE) { throw "signature of $(Split-Path $f -Leaf) is not valid" }
    }
}
function Hash([string]$file) {
    $h = (Get-FileHash $file -Algorithm SHA256).Hash.ToLowerInvariant()
    "$h  $(Split-Path $file -Leaf)" | Set-Content "$file.sha256" -NoNewline -Encoding ascii
}

# start clean, but keep hand-written release notes
Get-ChildItem $out -Exclude 'notes.md' -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force
New-Item -ItemType Directory -Force $pkg | Out-Null

# Framework-dependent single files: tiny downloads, the .NET 10 Desktop Runtime is installed once
# (the exe offers the download link itself if it is missing).
$common = @('-c', 'Release', '-r', 'win-x64', '--self-contained', 'false', '-p:PublishSingleFile=true',
            '-p:DebugType=none', "-p:Version=$Version", '--nologo', '-v', 'q',
            '--artifacts-path', "$out\build")  # isolated from the dev bin/obj folders

Write-Host "== Tray app" -ForegroundColor Cyan
dotnet publish "$root\src\EizoSwitcher\EizoSwitcher.csproj" @common -o "$out\app"
if ($LASTEXITCODE) { throw 'publish app failed' }

Write-Host "== Command line tool" -ForegroundColor Cyan
dotnet publish "$root\src\EizoSwitcher.Cli\EizoSwitcher.Cli.csproj" @common -o "$out\cli"
if ($LASTEXITCODE) { throw 'publish cli failed' }

Copy-Item "$out\app\EizoSwitcher.exe", "$out\cli\eizoswitch.exe" $pkg
Copy-Item "$root\LICENSE" $pkg

$exes = "$pkg\EizoSwitcher.exe", "$pkg\eizoswitch.exe"
Sign $exes
Verify $exes

# portable zip (for use without the setup; such copies update by swapping the exe)
$zip = Join-Path $out 'EizoSwitcher-win-x64.zip'
Compress-Archive "$pkg\*" $zip -CompressionLevel Optimal
Hash $zip

# installer (Inno Setup 6) – Inno signs the uninstaller and the setup itself through the "artifact" sign tool
Write-Host "== Installer" -ForegroundColor Cyan
$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 not found (winget install JRSoftware.InnoSetup)' }
$isccArgs = @('/Q', "/DVersion=$Version", "/DSourceDir=$pkg", "/DOutputDir=$out")
if ($sign) {
    # $q = quote, $f = file to sign (Inno Setup sign tool syntax)
    $cmd = ($signArgs | ForEach-Object { if ($_ -match '\s') { '$q' + $_ + '$q' } else { $_ } }) -join ' '
    $isccArgs += '/DSign', ('/Sartifact=$q' + $signtool + '$q ' + $cmd + ' $f')
}
& $iscc @isccArgs "$root\installer\EizoSwitcher.iss"
if ($LASTEXITCODE) { throw 'installer build failed' }
$setup = Join-Path $out 'EizoSwitcher-Setup.exe'
Verify $setup
Hash $setup

Get-ChildItem $pkg, $zip, $setup | Format-Table Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 2) } } -AutoSize
if ($sign) {
    $signer = (Get-AuthenticodeSignature $setup).SignerCertificate
    Write-Host "Signed by: $($signer.Subject)" -ForegroundColor Green
}

if ($Publish) {
    Write-Host "== GitHub release v$Version" -ForegroundColor Cyan
    $notes = Join-Path $out 'notes.md'
    $noteArgs = if (Test-Path $notes) { @('--notes-file', $notes) } else { @('--generate-notes') }
    # gh reads "file#label" – upload by file name from the output folder, the repo path contains "_C#"
    Push-Location $out
    try {
        $assets = foreach ($f in $setup, $zip) { Split-Path $f -Leaf; (Split-Path $f -Leaf) + '.sha256' }
        gh release create "v$Version" @assets --title "EizoSwitcher $Version" @noteArgs
        if ($LASTEXITCODE) { throw 'gh release create failed' }
    }
    finally { Pop-Location }
}

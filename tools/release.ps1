<#
.SYNOPSIS
  Builds, optionally signs and publishes an EizoSwitcher release.

.EXAMPLE
  .\tools\release.ps1 -Version 1.0.0                                   # build + package only
  .\tools\release.ps1 -Version 1.0.0 -CertThumbprint 0123ABCD...       # + Authenticode signing
  .\tools\release.ps1 -Version 1.0.0 -CertThumbprint 0123... -Publish  # + GitHub release (needs gh)

.NOTES
  Output: artifacts\<version>\EizoSwitcher-Setup.exe and EizoSwitcher-win-x64.zip (each + .sha256) – the asset names the auto-updater expects.
  Needs Inno Setup 6 (winget install JRSoftware.InnoSetup). Release notes: artifacts\<version>\notes.md (optional).
  Signing uses signtool from the Windows SDK and a certificate from the current user's store.
#>
param(
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version,
    [string]$CertThumbprint,
    [string]$TimestampUrl = 'http://timestamp.digicert.com',
    [switch]$Publish
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $root "artifacts\$Version"
$pkg = Join-Path $out 'package'
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

$signtool = $null
if ($CertThumbprint) {
    $signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending | Select-Object -First 1
    if (-not $signtool) { throw 'signtool.exe not found (install the Windows SDK)' }
}
function Sign([string[]]$files) {
    if (-not $signtool) { return }
    Write-Host "== Signing $($files | Split-Path -Leaf)" -ForegroundColor Cyan
    & $signtool.FullName sign /sha1 $CertThumbprint /fd sha256 /tr $TimestampUrl /td sha256 `
        /d EizoSwitcher /du "https://github.com/s1nn3r/EizoSwitcher" @files
    if ($LASTEXITCODE) { throw 'signing failed' }
}
function Hash([string]$file) {
    $h = (Get-FileHash $file -Algorithm SHA256).Hash.ToLowerInvariant()
    "$h  $(Split-Path $file -Leaf)" | Set-Content "$file.sha256" -NoNewline -Encoding ascii
}

Sign "$pkg\EizoSwitcher.exe", "$pkg\eizoswitch.exe"

# portable zip (also what versions <= 0.2 update from)
$zip = Join-Path $out 'EizoSwitcher-win-x64.zip'
Compress-Archive "$pkg\*" $zip -CompressionLevel Optimal
Hash $zip

# installer (Inno Setup 6)
Write-Host "== Installer" -ForegroundColor Cyan
$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 not found (winget install JRSoftware.InnoSetup)' }
& $iscc /Q "/DVersion=$Version" "/DSourceDir=$pkg" "/DOutputDir=$out" "$root\installer\EizoSwitcher.iss"
if ($LASTEXITCODE) { throw 'installer build failed' }
$setup = Join-Path $out 'EizoSwitcher-Setup.exe'
Sign $setup
Hash $setup

Get-ChildItem $pkg, $zip, $setup | Format-Table Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 2) } } -AutoSize

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

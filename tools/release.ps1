<#
.SYNOPSIS
  Builds, optionally signs and publishes an EizoSwitcher release.

.EXAMPLE
  .\tools\release.ps1 -Version 1.0.0                                   # build + package only
  .\tools\release.ps1 -Version 1.0.0 -CertThumbprint 0123ABCD...       # + Authenticode signing
  .\tools\release.ps1 -Version 1.0.0 -CertThumbprint 0123... -Publish  # + GitHub release (needs gh)

.NOTES
  Output: artifacts\<version>\EizoSwitcher-win-x64.zip (+ .sha256), the asset names the auto-updater expects.
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
Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $pkg | Out-Null

# Framework-dependent single files: tiny downloads, the .NET 10 Desktop Runtime is installed once
# (the exe offers the download link itself if it is missing).
$common = @('-c', 'Release', '-r', 'win-x64', '--self-contained', 'false', '-p:PublishSingleFile=true',
            '-p:DebugType=none', "-p:Version=$Version", '--nologo', '-v', 'q')

Write-Host "== Tray app" -ForegroundColor Cyan
dotnet publish "$root\src\EizoSwitcher\EizoSwitcher.csproj" @common -o "$out\app"
if ($LASTEXITCODE) { throw 'publish app failed' }

Write-Host "== Command line tool" -ForegroundColor Cyan
dotnet publish "$root\src\EizoSwitcher.Cli\EizoSwitcher.Cli.csproj" @common -o "$out\cli"
if ($LASTEXITCODE) { throw 'publish cli failed' }

Copy-Item "$out\app\EizoSwitcher.exe", "$out\cli\eizoswitch.exe" $pkg
Copy-Item "$root\LICENSE" $pkg

if ($CertThumbprint) {
    Write-Host "== Signing" -ForegroundColor Cyan
    $signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending | Select-Object -First 1
    if (-not $signtool) { throw 'signtool.exe not found (install the Windows SDK)' }
    & $signtool.FullName sign /sha1 $CertThumbprint /fd sha256 /tr $TimestampUrl /td sha256 `
        /d EizoSwitcher /du "https://github.com/s1nn3r/EizoSwitcher" "$pkg\EizoSwitcher.exe" "$pkg\eizoswitch.exe"
    if ($LASTEXITCODE) { throw 'signing failed' }
}

$zip = Join-Path $out 'EizoSwitcher-win-x64.zip'
Compress-Archive "$pkg\*" $zip -CompressionLevel Optimal
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  EizoSwitcher-win-x64.zip" | Set-Content "$zip.sha256" -NoNewline -Encoding ascii

Get-ChildItem $pkg, $zip | Format-Table Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } } -AutoSize
Write-Host "SHA-256: $hash"

if ($Publish) {
    Write-Host "== GitHub release v$Version" -ForegroundColor Cyan
    gh release create "v$Version" $zip "$zip.sha256" --title "EizoSwitcher $Version" --generate-notes
    if ($LASTEXITCODE) { throw 'gh release create failed' }
}

<#
  Builds the Optim PDF installer: publishes a self-contained x64 build, optionally signs it, and packs the MSI.

  .\installer\build.ps1                       # unsigned MSI in artifacts\
  .\installer\build.ps1 -CertThumbprint ABC…  # sign the exe/dlls and the MSI with a certificate from the
                                              # Windows store (e.g. an EV code-signing token)
  .\installer\build.ps1 -PfxPath cert.pfx -PfxPassword (Read-Host -AsSecureString)
#>
param(
    [string]$CertThumbprint,
    [string]$PfxPath,
    [SecureString]$PfxPassword,
    [string]$TimestampUrl = "http://timestamp.digicert.com"
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$csproj = Join-Path $root "src\OptimPdf.App\OptimPdf.App.csproj"
$version = ([xml](Get-Content $csproj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$publish = Join-Path $root "artifacts\publish"
$msi = Join-Path $root "artifacts\OptimPdf-$version-x64.msi"

Write-Host "Optim PDF $version" -ForegroundColor Cyan
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
dotnet publish $csproj -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=true -o $publish
if ($LASTEXITCODE) { throw "Publish failed." }

function Sign([string[]]$files) {
    if (-not $CertThumbprint -and -not $PfxPath) { return }
    $signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Recurse -Filter signtool.exe |
        Where-Object FullName -match '\\x64\\' | Sort-Object FullName | Select-Object -Last 1
    if (-not $signtool) { throw "signtool.exe not found (install the Windows SDK)." }
    $args = @('sign', '/fd', 'SHA256', '/tr', $TimestampUrl, '/td', 'SHA256', '/d', 'Optim PDF')
    if ($CertThumbprint) { $args += @('/sha1', $CertThumbprint) }
    else {
        $plain = [Runtime.InteropServices.Marshal]::PtrToStringUni([Runtime.InteropServices.Marshal]::SecureStringToBSTR($PfxPassword))
        $args += @('/f', $PfxPath, '/p', $plain)
    }
    & $signtool.FullName @args @files
    if ($LASTEXITCODE) { throw "Signing failed." }
}

# Sign our own binaries (third-party binaries keep their publishers' signatures).
Sign @((Join-Path $publish "OptimPdf.exe"), (Join-Path $publish "OptimPdf.dll"), (Join-Path $publish "OptimPdf.Core.dll"))

Push-Location $PSScriptRoot
try {
    dotnet tool restore | Out-Null
    dotnet wix build OptimPdf.wxs -arch x64 -d "Version=$version" -d "PublishDir=$publish" `
        -ext WixToolset.UI.wixext -ext WixToolset.Util.wixext -o $msi
    if ($LASTEXITCODE) { throw "WiX build failed." }
}
finally { Pop-Location }
Sign @($msi)
Write-Host "Built $msi ($([math]::Round((Get-Item $msi).Length / 1MB, 1)) MB)" -ForegroundColor Green

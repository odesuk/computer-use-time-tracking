<# Builds a one-file elevated setup wrapper around a signed local-pilot MSIX. #>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [ValidatePattern('^\d+\.\d+\.\d+\.\d+$')] [string] $Version,
    [Parameter(Mandatory)] [string] $CertificatePath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$installer = $PSScriptRoot
$project = Split-Path $installer -Parent
$artifacts = Join-Path $project 'src\Windows\artifacts'
$msixName = "ComputerUse.TimeTracking_${Version}_x64.msix"
$msixPath = Join-Path $artifacts $msixName
$output = Join-Path $artifacts "ComputerUse.TimeTracking_${Version}_Setup.exe"
$stage = Join-Path $artifacts 'pilot-setup-stage'
$payload = Join-Path $stage 'payload'

function Find-SdkTool([string] $name) {
    $kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    $tool = Get-ChildItem $kits -Directory | Where-Object { $_.Name -match '^\d+\.' } | Sort-Object { [version]$_.Name } -Descending |
        ForEach-Object { Join-Path $_.FullName "x64\$name" } | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
    if (-not $tool) { throw "$name not found. Install the Windows 10/11 SDK." }
    $tool
}

if (-not (Test-Path -LiteralPath $msixPath -PathType Leaf)) { throw "MSIX not found: $msixPath" }
if (-not (Test-Path -LiteralPath $CertificatePath -PathType Leaf)) { throw "Certificate not found: $CertificatePath" }
$certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new((Resolve-Path -LiteralPath $CertificatePath))
if ($certificate.HasPrivateKey) { throw 'Setup may only bundle a public certificate (.cer), never a private key.' }
$signature = Get-AuthenticodeSignature -LiteralPath $msixPath
if (-not $signature.SignerCertificate -or $signature.SignerCertificate.Thumbprint -ne $certificate.Thumbprint -or $signature.SignerCertificate.Subject -ne $certificate.Subject -or $signature.Status -in @('HashMismatch', 'NotSigned', 'NotSupported')) {
    throw "MSIX is not signed by the supplied certificate: $($signature.Status)"
}

try {
    Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Path $payload -Force | Out-Null
    Copy-Item -LiteralPath $msixPath -Destination $payload
    Copy-Item -LiteralPath $CertificatePath -Destination (Join-Path $payload 'ComputerUse.TimeTracking_LocalPublisher.cer')
    $install = (Get-Content -LiteralPath (Join-Path $installer 'PilotSetup\Install-Pilot.ps1.template') -Raw).
        Replace('__MSIX_NAME__', $msixName).
        Replace('__MSIX_SHA256__', (Get-FileHash -LiteralPath $msixPath -Algorithm SHA256).Hash).
        Replace('__CERT_SHA256__', (Get-FileHash -LiteralPath $CertificatePath -Algorithm SHA256).Hash).
        Replace('__CERT_THUMBPRINT__', $certificate.Thumbprint).
        Replace('__CERT_SUBJECT__', $certificate.Subject)
    Set-Content -LiteralPath (Join-Path $payload 'Install-Pilot.ps1') -Value $install -Encoding UTF8

    $sedPath = Join-Path $stage 'pilot-setup.sed'
    @"
[Version]
Class=IEXPRESS
SEDVersion=3
[Options]
PackagePurpose=InstallApp
ShowInstallProgramWindow=1
HideExtractAnimation=0
UseLongFileName=1
InsideCompressed=0
CAB_FixedSize=0
CAB_ResvCodeSigning=0
RebootMode=N
InstallPrompt=
DisplayLicense=
FinishMessage=
TargetName=$output
FriendlyName=Computer Use Time Tracking $Version Pilot Setup
AppLaunched=powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File Install-Pilot.ps1
PostInstallCmd=<None>
AdminQuietInstCmd=
UserQuietInstCmd=
SourceFiles=SourceFiles
[Strings]
FILE0=ComputerUse.TimeTracking_LocalPublisher.cer
FILE1=$msixName
FILE2=Install-Pilot.ps1
[SourceFiles]
SourceFiles0=$payload\
[SourceFiles0]
%FILE0%=
%FILE1%=
%FILE2%=
"@ | Set-Content -LiteralPath $sedPath -Encoding ASCII

    $iexpress = Join-Path $env:SystemRoot 'System32\iexpress.exe'
    if (-not (Test-Path -LiteralPath $iexpress -PathType Leaf)) { throw 'IExpress is not available on this Windows machine.' }
    Remove-Item -LiteralPath $output -Force -ErrorAction SilentlyContinue
    $process = Start-Process -FilePath $iexpress -ArgumentList '/N', $sedPath -Wait -PassThru
    if ($process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $output -PathType Leaf)) { throw "IExpress failed ($($process.ExitCode))." }
    & (Find-SdkTool 'mt.exe') -nologo -manifest (Join-Path $installer 'PilotSetup\setup.exe.manifest') "-outputresource:$output;#1"
    if ($LASTEXITCODE -ne 0) { throw "mt.exe failed ($LASTEXITCODE)." }
    & (Find-SdkTool 'signtool.exe') sign /sha1 $certificate.Thumbprint /fd SHA256 /tr http://timestamp.acs.microsoft.com /td SHA256 /d 'Computer Use Time Tracking Pilot Setup' $output
    if ($LASTEXITCODE -ne 0) { throw "signtool sign failed ($LASTEXITCODE)." }
    $wrapper = Get-AuthenticodeSignature -LiteralPath $output
    if (-not $wrapper.SignerCertificate -or $wrapper.SignerCertificate.Thumbprint -ne $certificate.Thumbprint -or $wrapper.Status -in @('HashMismatch', 'NotSigned', 'NotSupported')) { throw "Setup signature verification failed: $($wrapper.Status)" }
    Write-Host "Built $output"
}
finally {
    Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
}

<#
.SYNOPSIS
    Builds (and optionally signs) the Computer Use Time Tracker MSIX and its .appinstaller file.

.DESCRIPTION
    1. dotnet publish, self-contained win-x64 (Properties/PublishProfiles/win-x64.pubxml).
    2. Stages the publish output with Installer/Assets and a manifest generated from
       Package.appxmanifest.template (publisher and version filled in).
    3. makeappx pack, then signtool sign unless -SkipSigning.
    4. Generates ComputerUse.TimeTracking.appinstaller from its template when -FeedUrl is given.

    Run on Windows with the Windows 10/11 SDK installed (makeappx.exe, signtool.exe), e.g. a
    GitHub Actions windows-latest job. Keep signing credentials in the CI secret store, never in
    the repository.

.EXAMPLE
    # Self-signed test build (see README.md for creating the test certificate)
    .\build-msix.ps1 -Version 1.0.0.0 -Publisher "CN=Local Test Publisher" `
        -SignToolArgs '/f','C:\certs\local-test.pfx','/p',$env:TIME_TRACKER_TEST_PFX_PASSWORD

.EXAMPLE
    # Azure Trusted Signing
    .\build-msix.ps1 -Version 1.0.3.0 -Publisher "CN=Computer Use Time Tracking contributors, O=Computer Use Time Tracking contributors, L=..., S=..., C=US" `
        -FeedUrl https://downloads.example.com/timetracker `
        -SignToolArgs '/dlib','C:\tools\Azure.CodeSigning.Dlib.dll','/dmdf','C:\tools\metadata.json'
#>
[CmdletBinding()]
param(
    # Four-part version; must increase with every release or App Installer won't update.
    [Parameter(Mandatory)] [ValidatePattern('^\d+\.\d+\.\d+\.\d+$')] [string] $Version,

    # Exact signing certificate subject; must match the certificate or installation fails.
    [Parameter(Mandatory)] [string] $Publisher,

    [string] $Configuration = 'Release',

    # HTTPS folder that will host the .msix and .appinstaller. Omit to skip the .appinstaller.
    [string] $FeedUrl,

    # Extra signtool arguments: a .pfx (/f, /p) or Trusted Signing (/dlib, /dmdf).
    [string[]] $SignToolArgs = @(),

    [string] $TimestampUrl = 'http://timestamp.acs.microsoft.com',

    [switch] $SkipSigning
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$installer = $PSScriptRoot
$project   = Split-Path $installer -Parent
$artifacts = Join-Path $project 'artifacts'
$publish   = Join-Path $artifacts 'publish'
$stage     = Join-Path $artifacts 'msix-stage'
$msixName  = "ComputerUse.TimeTracking_$Version`_x64.msix"
$msix      = Join-Path $artifacts $msixName

if ($Publisher -like '*REPLACE_WITH*') { throw 'Pass the real certificate subject with -Publisher.' }
if ($FeedUrl -and -not $FeedUrl.StartsWith('https://')) { throw '-FeedUrl must be an https:// URL.' }

function Find-SdkTool([string] $name) {
    $kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    $tool = Get-ChildItem $kits -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^\d+\.' } |
        Sort-Object { [version]$_.Name } -Descending |
        ForEach-Object { Join-Path $_.FullName "x64\$name" } |
        Where-Object { Test-Path $_ } |
        Select-Object -First 1
    if (-not $tool) { throw "$name not found. Install the Windows 10/11 SDK." }
    return $tool
}

Write-Host "== Publishing $Configuration win-x64 (self-contained)"
Remove-Item $publish, $stage -Recurse -Force -ErrorAction SilentlyContinue
$fileVersion = ($Version.Split('.')[0..2]) -join '.'
dotnet publish (Join-Path $project 'ComputerUse.TimeTracking.Windows.csproj') `
    -c $Configuration -p:PublishProfile=win-x64 -p:Version=$fileVersion -p:FileVersion=$Version `
    -o $publish
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)." }

$missingRuntimeFiles = @('coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll') |
    Where-Object { -not (Test-Path (Join-Path $publish $_)) }
if ($missingRuntimeFiles) {
    throw "Self-contained publish is missing .NET runtime files: $($missingRuntimeFiles -join ', ')."
}

$runtimeConfig = Get-Content (Join-Path $publish 'ComputerUse.TimeTracking.runtimeconfig.json') -Raw | ConvertFrom-Json
$runtimeProperties = $runtimeConfig.runtimeOptions.PSObject.Properties.Name
if ($runtimeProperties -contains 'frameworks' -or $runtimeProperties -notcontains 'includedFrameworks') {
    throw 'Publish output does not include the .NET runtime; refusing to package it.'
}

Write-Host '== Staging package layout'
New-Item $stage -ItemType Directory | Out-Null
Copy-Item (Join-Path $publish '*') $stage -Recurse
Copy-Item (Join-Path $installer 'Assets') (Join-Path $stage 'Assets') -Recurse -Force

$manifest = Get-Content (Join-Path $installer 'Package.appxmanifest.template') -Raw
$manifest = $manifest.Replace('CN=REPLACE_WITH_SIGNING_CERT_SUBJECT', [Security.SecurityElement]::Escape($Publisher))
$manifest = $manifest -replace '(<Identity[^>]*?Version=")[^"]+(")', "`${1}$Version`${2}"
Set-Content (Join-Path $stage 'AppxManifest.xml') $manifest -Encoding utf8

Write-Host "== Packing $msixName"
$makeappx = Find-SdkTool 'makeappx.exe'
& $makeappx pack /d $stage /p $msix /o
if ($LASTEXITCODE -ne 0) { throw "makeappx failed ($LASTEXITCODE)." }

if ($SkipSigning) {
    Write-Warning 'Package is NOT signed. Windows will refuse to install it until it is.'
} else {
    Write-Host '== Signing'
    $signtool = Find-SdkTool 'signtool.exe'
    & $signtool sign /fd SHA256 /tr $TimestampUrl /td SHA256 @SignToolArgs $msix
    if ($LASTEXITCODE -ne 0) { throw "signtool failed ($LASTEXITCODE)." }
}

if ($FeedUrl) {
    $feed = $FeedUrl.TrimEnd('/')
    $appinstaller = Get-Content (Join-Path $installer 'ComputerUse.TimeTracking.appinstaller.template') -Raw
    $appinstaller = $appinstaller.Replace('https://REPLACE_WITH_HTTPS_FEED/ComputerUse.TimeTracking.appinstaller', "$feed/ComputerUse.TimeTracking.appinstaller")
    $appinstaller = $appinstaller.Replace('https://REPLACE_WITH_HTTPS_FEED/ComputerUse.TimeTracking.msix', "$feed/$msixName")
    $appinstaller = $appinstaller.Replace('CN=REPLACE_WITH_SIGNING_CERT_SUBJECT', [Security.SecurityElement]::Escape($Publisher))
    $appinstaller = $appinstaller -replace 'Version="1\.0\.0\.0"', "Version=`"$Version`""
    $appinstallerPath = Join-Path $artifacts 'ComputerUse.TimeTracking.appinstaller'
    Set-Content $appinstallerPath $appinstaller -Encoding utf8
    Write-Host "Built $appinstallerPath"
}

Write-Host "Built $msix"

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$DriverVersion = '25.7.23'
$DriverUrl = 'https://github.com/VirtualDrivers/Virtual-Display-Driver/releases/download/25.7.23/VirtualDisplayDriver-x86.Driver.Only.zip'
$DriverSha256 = 'e24210692b442b39af763536330ce78b423f19342b7a7792c26de3944e418b3a'
$NefConVersion = '1.14.0'
$NefConUrl = 'https://github.com/nefarius/nefcon/releases/download/v1.14.0/nefcon_v1.14.0.zip'
$NefConSha256 = 'a15557da24a9efca203158de3b43b0eaf982db231f0194031f1ed428bc13e669'
$InstallDir = 'C:\VirtualDisplayDriver'
$TempDir = Join-Path $env:TEMP 'VMU-Alpha-VDD'

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Assert-Hash {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Expected
    )

    $actual = (Get-FileHash -Path $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $Expected.ToLowerInvariant()) {
        throw "SHA-256 mismatch for $Path. Expected $Expected, got $actual."
    }
}

if ($env:OS -ne 'Windows_NT') {
    throw 'VMU ALPHA setup supports Windows only.'
}

if (-not [Environment]::Is64BitOperatingSystem) {
    throw 'VMU ALPHA currently supports x64 Windows only.'
}

if (-not (Test-IsAdministrator)) {
    Write-Host 'Administrator rights are required. Opening UAC prompt...' -ForegroundColor Yellow
    $arguments = @(
        '-NoProfile',
        '-ExecutionPolicy', 'Bypass',
        '-File', ('"{0}"' -f $PSCommandPath)
    ) -join ' '
    $process = Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList $arguments -Wait -PassThru
    exit $process.ExitCode
}

Write-Host "VMU ALPHA setup - Virtual Display Driver $DriverVersion" -ForegroundColor Cyan
Write-Host 'The setup uses the upstream VDD silent-install method, with pinned versions and SHA-256 verification.'

Remove-Item -Path $TempDir -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $TempDir -Force | Out-Null

try {
    $driverZip = Join-Path $TempDir 'vdd.zip'
    $nefconZip = Join-Path $TempDir 'nefcon.zip'

    Write-Host "Downloading VDD $DriverVersion..." -ForegroundColor Cyan
    Invoke-WebRequest -Uri $DriverUrl -OutFile $driverZip -UseBasicParsing
    Assert-Hash -Path $driverZip -Expected $DriverSha256

    Write-Host "Downloading NefCon $NefConVersion..." -ForegroundColor Cyan
    Invoke-WebRequest -Uri $NefConUrl -OutFile $nefconZip -UseBasicParsing
    Assert-Hash -Path $nefconZip -Expected $NefConSha256

    $driverExtract = Join-Path $TempDir 'driver'
    $nefconExtract = Join-Path $TempDir 'nefcon'
    Expand-Archive -Path $driverZip -DestinationPath $driverExtract -Force
    Expand-Archive -Path $nefconZip -DestinationPath $nefconExtract -Force

    $driverSource = Join-Path $driverExtract 'VirtualDisplayDriver'
    $infPath = Join-Path $driverSource 'MttVDD.inf'
    $catPath = Join-Path $driverSource 'mttvdd.cat'
    $nefconExe = Join-Path $nefconExtract 'x64\nefconw.exe'

    foreach ($required in @($infPath, $catPath, $nefconExe)) {
        if (-not (Test-Path $required)) {
            throw "Required installation file not found: $required"
        }
    }

    Write-Host 'Installing trusted publisher certificate from the signed driver catalog...' -ForegroundColor Cyan
    $catalogBytes = [System.IO.File]::ReadAllBytes($catPath)
    $certificates = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2Collection
    $certificates.Import($catalogBytes)

    $certDir = Join-Path $TempDir 'certificates'
    New-Item -ItemType Directory -Path $certDir -Force | Out-Null
    foreach ($certificate in $certificates) {
        $certPath = Join-Path $certDir ($certificate.Thumbprint + '.cer')
        [System.IO.File]::WriteAllBytes(
            $certPath,
            $certificate.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Cert)
        )
        Import-Certificate -FilePath $certPath -CertStoreLocation 'Cert:\LocalMachine\TrustedPublisher' | Out-Null
    }

    Write-Host "Preparing $InstallDir..." -ForegroundColor Cyan
    New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
    Copy-Item -Path (Join-Path $driverSource '*') -Destination $InstallDir -Recurse -Force

    $installedInf = Join-Path $InstallDir 'MttVDD.inf'
    Write-Host 'Installing Virtual Display Driver...' -ForegroundColor Cyan
    & $nefconExe install $installedInf 'Root\MttVDD'
    if ($LASTEXITCODE -ne 0) {
        throw "NefCon installation failed with exit code $LASTEXITCODE."
    }

    Start-Sleep -Seconds 10

    Write-Host 'VDD installation completed.' -ForegroundColor Green
    $statusScript = Join-Path $PSScriptRoot 'vmu-alpha.ps1'
    if (Test-Path $statusScript) {
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $statusScript --status
    }

    Write-Host ''
    Write-Host 'ALPHA environment is ready. Next test: run tools\alpha\vmu-alpha.ps1 --on' -ForegroundColor Green
}
finally {
    Remove-Item -Path $TempDir -Recurse -Force -ErrorAction SilentlyContinue
}

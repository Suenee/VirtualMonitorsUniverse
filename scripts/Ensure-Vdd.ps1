[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$PipeName = 'MTTVirtualDisplayPipe'
$DriverVersion = '25.7.23'
$DriverUrl = 'https://github.com/VirtualDrivers/Virtual-Display-Driver/releases/download/25.7.23/VirtualDisplayDriver-x86.Driver.Only.zip'
$DriverSha256 = 'e24210692b442b39af763536330ce78b423f19342b7a7792c26de3944e418b3a'
$NefConVersion = '1.14.0'
$NefConUrl = 'https://github.com/nefarius/nefcon/releases/download/v1.14.0/nefcon_v1.14.0.zip'
$NefConSha256 = 'a15557da24a9efca203158de3b43b0eaf982db231f0194031f1ed428bc13e669'
$VddFriendlyName = 'Virtual Display Driver'

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Test-VddPipe {
    $pipe = [System.IO.Pipes.NamedPipeClientStream]::new(
        '.',
        $PipeName,
        [System.IO.Pipes.PipeDirection]::InOut,
        [System.IO.Pipes.PipeOptions]::None)
    try {
        $pipe.Connect(500)
        return $pipe.IsConnected
    }
    catch {
        return $false
    }
    finally {
        $pipe.Dispose()
    }
}

function Wait-Until {
    param(
        [Parameter(Mandatory)][scriptblock]$Condition,
        [Parameter(Mandatory)][string]$Description,
        [int]$TimeoutSeconds = 15)

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        if (& $Condition) {
            Write-Host "VDD dependency: $Description"
            return $true
        }
        Start-Sleep -Milliseconds 500
    } while ((Get-Date) -lt $deadline)

    return $false
}

function Get-VddDevices {
    return @(
        Get-PnpDevice -Class Display -ErrorAction SilentlyContinue |
            Where-Object { $_.FriendlyName -eq $VddFriendlyName }
    )
}

function Assert-Hash {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$Expected)
    $actual = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $Expected.ToLowerInvariant()) {
        throw "SHA-256 mismatch for $Path."
    }
}

function Invoke-Native {
    param([Parameter(Mandatory)][string]$FilePath, [Parameter()][string[]]$Arguments = @())
    $process = Start-Process -FilePath $FilePath -ArgumentList $Arguments -Wait -PassThru -NoNewWindow
    if ($process.ExitCode -ne 0) {
        throw "$FilePath failed with exit code $($process.ExitCode)."
    }
}

if ($env:OS -ne 'Windows_NT') {
    throw 'Virtual Display Driver dependency setup is supported only on Windows.'
}

if (Test-VddPipe) {
    Write-Host 'VDD dependency: named pipe already available.'
    exit 0
}

if (-not (Test-IsAdministrator)) {
    throw 'VDD dependency setup requires an elevated process.'
}

$devices = @(Get-VddDevices)
if ($devices.Count -gt 0) {
    Write-Host "VDD dependency: found $($devices.Count) existing device node(s); enabling them."
    foreach ($device in $devices) {
        Invoke-Native -FilePath (Join-Path $env:SystemRoot 'System32\pnputil.exe') -Arguments @('/enable-device', ('"{0}"' -f $device.InstanceId))
    }

    if (Wait-Until -Description 'existing driver enabled and named pipe available' -TimeoutSeconds 15 -Condition { Test-VddPipe }) {
        exit 0
    }

    throw 'Existing Virtual Display Driver device nodes were enabled, but the named pipe did not become available.'
}

$workRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('VMU-VDD-' + [Guid]::NewGuid().ToString('N'))
$driverZip = Join-Path $workRoot "vdd-$DriverVersion.zip"
$nefconZip = Join-Path $workRoot "nefcon-$NefConVersion.zip"
$driverExtract = Join-Path $workRoot 'driver'
$nefconExtract = Join-Path $workRoot 'nefcon'

try {
    New-Item -ItemType Directory -Path $workRoot -Force | Out-Null

    Write-Host "VDD dependency: downloading Virtual Display Driver $DriverVersion..."
    Invoke-WebRequest -Uri $DriverUrl -OutFile $driverZip -UseBasicParsing
    Assert-Hash -Path $driverZip -Expected $DriverSha256

    Write-Host "VDD dependency: downloading NefCon $NefConVersion..."
    Invoke-WebRequest -Uri $NefConUrl -OutFile $nefconZip -UseBasicParsing
    Assert-Hash -Path $nefconZip -Expected $NefConSha256

    Expand-Archive -LiteralPath $driverZip -DestinationPath $driverExtract -Force
    Expand-Archive -LiteralPath $nefconZip -DestinationPath $nefconExtract -Force

    $driverSource = Join-Path $driverExtract 'VirtualDisplayDriver'
    $infPath = Join-Path $driverSource 'MttVDD.inf'
    $catPath = Join-Path $driverSource 'mttvdd.cat'
    $nefconExe = Join-Path $nefconExtract 'x64\nefconw.exe'

    foreach ($required in @($infPath, $catPath, $nefconExe)) {
        if (-not (Test-Path -LiteralPath $required)) {
            throw "Required VDD installation file not found: $required"
        }
    }

    # The signed catalog certificate is imported only when it is not already
    # trusted. This is the same installation path validated by the ALPHA POC.
    $catalogBytes = [System.IO.File]::ReadAllBytes($catPath)
    $certificates = [System.Security.Cryptography.X509Certificates.X509Certificate2Collection]::new()
    $certificates.Import($catalogBytes)

    foreach ($certificate in $certificates) {
        $existing = Get-ChildItem 'Cert:\LocalMachine\TrustedPublisher' |
            Where-Object { $_.Thumbprint -eq $certificate.Thumbprint } |
            Select-Object -First 1
        if ($null -ne $existing) { continue }

        $certPath = Join-Path $workRoot ($certificate.Thumbprint + '.cer')
        [System.IO.File]::WriteAllBytes(
            $certPath,
            $certificate.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Cert))
        Import-Certificate -FilePath $certPath -CertStoreLocation 'Cert:\LocalMachine\TrustedPublisher' | Out-Null
    }

    Write-Host 'VDD dependency: installing one root-enumerated MttVDD device...'
    Invoke-Native -FilePath $nefconExe -Arguments @('install', ('"{0}"' -f $infPath), 'Root\MttVDD')

    if (-not (Wait-Until -Description 'driver installed and named pipe available' -TimeoutSeconds 20 -Condition { Test-VddPipe })) {
        throw 'Virtual Display Driver installation completed, but the named pipe did not become available.'
    }

    $installed = @(Get-VddDevices)
    if ($installed.Count -lt 1) {
        throw 'Virtual Display Driver named pipe is available, but no VDD PnP device node was found.'
    }

    Write-Host "VDD dependency: OK ($($installed.Count) device node(s))."
    exit 0
}
finally {
    Remove-Item -LiteralPath $workRoot -Recurse -Force -ErrorAction SilentlyContinue
}

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
$VddFriendlyName = 'Virtual Display Driver'
$PipeName = 'MTTVirtualDisplayPipe'

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Get-VddDevices {
    # Keep post-install discovery equivalent to the ALPHA test that was
    # validated on the development machine. Root\MttVDD is the hardware ID
    # supplied to NefCon; the installed display device is discovered through
    # the Display class and its official friendly name.
    return @(
        Get-PnpDevice -Class Display -ErrorAction SilentlyContinue |
            Where-Object { $_.FriendlyName -eq $VddFriendlyName }
    )
}

function Test-VddPipe {
    $pipe = [System.IO.Pipes.NamedPipeClientStream]::new('.', $PipeName, [System.IO.Pipes.PipeDirection]::InOut, [System.IO.Pipes.PipeOptions]::None)
    try {
        $pipe.Connect(500)
        return $pipe.IsConnected
    }
    catch { return $false }
    finally { $pipe.Dispose() }
}

function Wait-Until {
    param(
        [Parameter(Mandatory)][scriptblock]$Condition,
        [Parameter(Mandatory)][string]$Description,
        [int]$TimeoutSeconds = 20)

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        if (& $Condition) {
            Write-Host "VDD INSTALL: $Description"
            return $true
        }
        Start-Sleep -Milliseconds 500
    } while ((Get-Date) -lt $deadline)
    return $false
}

function Assert-Hash {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$Expected)
    $actual = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $Expected.ToLowerInvariant()) { throw "SHA-256 mismatch for $Path." }
}

function Invoke-NativeProcess {
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [Parameter()][string[]]$Arguments = @())

    Write-Host ("VDD INSTALL: RUN {0} {1}" -f $FilePath, ($Arguments -join ' '))
    $process = Start-Process -FilePath $FilePath -ArgumentList $Arguments -Wait -PassThru -NoNewWindow
    Write-Host "VDD INSTALL: EXIT CODE $($process.ExitCode)"
    if ($process.ExitCode -ne 0) {
        throw "$FilePath failed with exit code $($process.ExitCode)."
    }
    return $process.ExitCode
}

if ($env:OS -ne 'Windows_NT') { throw 'Virtual Display Driver installation is supported only on Windows.' }
if (-not (Test-IsAdministrator)) { throw 'Virtual Display Driver installation requires an elevated process.' }

$existing = @(Get-VddDevices)
if ($existing.Count -gt 0) {
    Write-Host "VDD INSTALL: ALPHA device already present: $($existing[0].InstanceId) [$($existing[0].Status)]"
    if (Test-VddPipe) {
        Write-Host 'VDD INSTALL: runtime pipe already available.'
        exit 0
    }
    throw 'Virtual Display Driver device already exists but MTTVirtualDisplayPipe is unavailable. Refusing to mutate an unknown unhealthy state; use a dedicated repair procedure.'
}

$workRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('VMU-VDD-' + [Guid]::NewGuid().ToString('N'))
$driverZip = Join-Path $workRoot "vdd-$DriverVersion.zip"
$nefconZip = Join-Path $workRoot "nefcon-$NefConVersion.zip"
$driverExtract = Join-Path $workRoot 'driver'
$nefconExtract = Join-Path $workRoot 'nefcon'

try {
    New-Item -ItemType Directory -Path $workRoot -Force | Out-Null
    Write-Host "VDD INSTALL: downloading Virtual Display Driver $DriverVersion..."
    Invoke-WebRequest -Uri $DriverUrl -OutFile $driverZip -UseBasicParsing
    Assert-Hash -Path $driverZip -Expected $DriverSha256
    Write-Host "VDD INSTALL: downloading NefCon $NefConVersion..."
    Invoke-WebRequest -Uri $NefConUrl -OutFile $nefconZip -UseBasicParsing
    Assert-Hash -Path $nefconZip -Expected $NefConSha256
    Expand-Archive -LiteralPath $driverZip -DestinationPath $driverExtract -Force
    Expand-Archive -LiteralPath $nefconZip -DestinationPath $nefconExtract -Force

    $driverSource = Join-Path $driverExtract 'VirtualDisplayDriver'
    $infPath = Join-Path $driverSource 'MttVDD.inf'
    $catPath = Join-Path $driverSource 'mttvdd.cat'
    $nefconExe = Join-Path $nefconExtract 'x64\nefconw.exe'
    foreach ($required in @($infPath, $catPath, $nefconExe)) {
        if (-not (Test-Path -LiteralPath $required)) { throw "Required VDD installation file not found: $required" }
    }

    $catalogBytes = [System.IO.File]::ReadAllBytes($catPath)
    $certificates = [System.Security.Cryptography.X509Certificates.X509Certificate2Collection]::new()
    $certificates.Import($catalogBytes)
    foreach ($certificate in $certificates) {
        $existingCert = Get-ChildItem 'Cert:\LocalMachine\TrustedPublisher' | Where-Object { $_.Thumbprint -eq $certificate.Thumbprint } | Select-Object -First 1
        if ($null -ne $existingCert) { continue }
        $certPath = Join-Path $workRoot ($certificate.Thumbprint + '.cer')
        [System.IO.File]::WriteAllBytes($certPath, $certificate.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Cert))
        Import-Certificate -FilePath $certPath -CertStoreLocation 'Cert:\LocalMachine\TrustedPublisher' | Out-Null
    }

    Write-Host 'VDD INSTALL: creating exactly one root-enumerated Root\MttVDD device...'
    # This is intentionally equivalent to the validated ALPHA invocation.
    Invoke-NativeProcess -FilePath $nefconExe -Arguments @('install', ('"{0}"' -f $infPath), 'Root\MttVDD') | Out-Null

    if (-not (Wait-Until -Description 'one Virtual Display Driver device detected' -Condition { @(Get-VddDevices).Count -eq 1 })) {
        throw "Expected exactly one Virtual Display Driver device after NefCon exit code 0, found $(@(Get-VddDevices).Count)."
    }

    $installed = @(Get-VddDevices)
    Write-Host "VDD INSTALL: device detected: $($installed[0].InstanceId) [$($installed[0].Status)]"

    if (-not (Wait-Until -Description 'MTTVirtualDisplayPipe available' -Condition { Test-VddPipe })) {
        throw 'Virtual Display Driver was detected after installation, but MTTVirtualDisplayPipe did not become available.'
    }

    Write-Host "VDD INSTALL: PASS - $($installed[0].InstanceId)"
    exit 0
}
catch {
    # Emit one stable, non-wrapped diagnostic marker. The C# CLI can use this
    # instead of mistaking a wrapped command/path fragment for the error cause.
    Write-Host ("VDD INSTALL ERROR: {0}" -f $_.Exception.Message)
    exit 1
}
finally {
    Remove-Item -LiteralPath $workRoot -Recurse -Force -ErrorAction SilentlyContinue
}

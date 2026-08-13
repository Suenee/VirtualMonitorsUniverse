[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$Controller = Join-Path $PSScriptRoot 'vmu-alpha.ps1'
$WaitSeconds = 4

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Get-Screens {
    Add-Type -AssemblyName System.Windows.Forms
    return @([System.Windows.Forms.Screen]::AllScreens)
}

function Show-Screens {
    foreach ($screen in (Get-Screens)) {
        $bounds = $screen.Bounds
        [pscustomobject]@{
            DeviceName = $screen.DeviceName
            Resolution = "{0}x{1}" -f $bounds.Width, $bounds.Height
            Position   = "{0},{1}" -f $bounds.X, $bounds.Y
            Primary    = $screen.Primary
        }
    } | Format-Table -AutoSize
}

function Invoke-Controller {
    param([Parameter(Mandatory = $true)][string]$Command)

    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $Controller $Command
    if ($LASTEXITCODE -ne 0) {
        throw "VMU controller command '$Command' failed with exit code $LASTEXITCODE."
    }
}

function Ensure-NativeDisplayApi {
    if ('Vmu.NativeDisplay' -as [type]) { return }

    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

namespace Vmu
{
    public static class NativeDisplay
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct DEVMODE
        {
            private const int CCHDEVICENAME = 32;
            private const int CCHFORMNAME = 32;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHDEVICENAME)] public string dmDeviceName;
            public ushort dmSpecVersion;
            public ushort dmDriverVersion;
            public ushort dmSize;
            public ushort dmDriverExtra;
            public uint dmFields;
            public int dmPositionX;
            public int dmPositionY;
            public uint dmDisplayOrientation;
            public uint dmDisplayFixedOutput;
            public short dmColor;
            public short dmDuplex;
            public short dmYResolution;
            public short dmTTOption;
            public short dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHFORMNAME)] public string dmFormName;
            public ushort dmLogPixels;
            public uint dmBitsPerPel;
            public uint dmPelsWidth;
            public uint dmPelsHeight;
            public uint dmDisplayFlags;
            public uint dmDisplayFrequency;
            public uint dmICMMethod;
            public uint dmICMIntent;
            public uint dmMediaType;
            public uint dmDitherType;
            public uint dmReserved1;
            public uint dmReserved2;
            public uint dmPanningWidth;
            public uint dmPanningHeight;
        }

        public const int ENUM_CURRENT_SETTINGS = -1;
        public const uint DM_BITSPERPEL = 0x00040000;
        public const uint DM_PELSWIDTH = 0x00080000;
        public const uint DM_PELSHEIGHT = 0x00100000;
        public const uint DM_DISPLAYFREQUENCY = 0x00400000;
        public const uint CDS_UPDATEREGISTRY = 0x00000001;
        public const int DISP_CHANGE_SUCCESSFUL = 0;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DEVMODE devMode);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int ChangeDisplaySettingsEx(string deviceName, ref DEVMODE devMode, IntPtr hwnd, uint flags, IntPtr lParam);
    }
}
'@
}

function Set-DisplayMode {
    param(
        [Parameter(Mandatory = $true)][string]$DeviceName,
        [Parameter(Mandatory = $true)][uint32]$Width,
        [Parameter(Mandatory = $true)][uint32]$Height,
        [Parameter(Mandatory = $true)][uint32]$RefreshRate
    )

    Ensure-NativeDisplayApi
    $mode = New-Object Vmu.NativeDisplay+DEVMODE
    $mode.dmSize = [Runtime.InteropServices.Marshal]::SizeOf($mode)

    if (-not [Vmu.NativeDisplay]::EnumDisplaySettings($DeviceName, [Vmu.NativeDisplay]::ENUM_CURRENT_SETTINGS, [ref]$mode)) {
        throw "Cannot read current display mode for $DeviceName."
    }

    $mode.dmPelsWidth = $Width
    $mode.dmPelsHeight = $Height
    $mode.dmDisplayFrequency = $RefreshRate
    $mode.dmFields = [Vmu.NativeDisplay]::DM_PELSWIDTH -bor [Vmu.NativeDisplay]::DM_PELSHEIGHT -bor [Vmu.NativeDisplay]::DM_DISPLAYFREQUENCY

    $result = [Vmu.NativeDisplay]::ChangeDisplaySettingsEx(
        $DeviceName,
        [ref]$mode,
        [IntPtr]::Zero,
        [Vmu.NativeDisplay]::CDS_UPDATEREGISTRY,
        [IntPtr]::Zero
    )

    if ($result -ne [Vmu.NativeDisplay]::DISP_CHANGE_SUCCESSFUL) {
        throw "Windows rejected ${Width}x${Height} @ ${RefreshRate} Hz for $DeviceName (result $result)."
    }

    Start-Sleep -Seconds 3
}

function Assert-DisplayMode {
    param(
        [Parameter(Mandatory = $true)][string]$DeviceName,
        [Parameter(Mandatory = $true)][int]$Width,
        [Parameter(Mandatory = $true)][int]$Height
    )

    $screen = Get-Screens | Where-Object { $_.DeviceName -eq $DeviceName } | Select-Object -First 1
    if (-not $screen) {
        throw "Display $DeviceName disappeared while validating its mode."
    }

    if ($screen.Bounds.Width -ne $Width -or $screen.Bounds.Height -ne $Height) {
        throw "Expected ${Width}x${Height} on $DeviceName, but Windows reports $($screen.Bounds.Width)x$($screen.Bounds.Height)."
    }

    Write-Host "PASS: $DeviceName is ${Width}x${Height}." -ForegroundColor Green
}

if (-not (Test-IsAdministrator)) {
    throw 'Run this script from an elevated terminal.'
}

Write-Host 'VMU ALPHA - display recreation and mode-switch test' -ForegroundColor Cyan
Write-Host ''
Write-Host '1/6 Powering off the current virtual display...' -ForegroundColor Cyan
Invoke-Controller '--off'
Start-Sleep -Seconds $WaitSeconds

$physicalNames = @(Get-Screens | ForEach-Object DeviceName)
Write-Host "Physical/remaining displays after VDD off: $($physicalNames.Count)"

Write-Host '2/6 Recreating one virtual display...' -ForegroundColor Cyan
Invoke-Controller '--on'
Start-Sleep -Seconds $WaitSeconds

$newScreens = @(Get-Screens | Where-Object { $_.DeviceName -notin $physicalNames })
if ($newScreens.Count -ne 1) {
    Write-Host 'Current Windows display topology:'
    Show-Screens
    throw "Expected exactly one newly created virtual display, found $($newScreens.Count)."
}

$virtualDisplay = $newScreens[0].DeviceName
Write-Host "Detected recreated virtual display: $virtualDisplay" -ForegroundColor Green

Write-Host '3/6 Setting FullHD 1920x1080 @ 60 Hz...' -ForegroundColor Cyan
Set-DisplayMode -DeviceName $virtualDisplay -Width 1920 -Height 1080 -RefreshRate 60
Assert-DisplayMode -DeviceName $virtualDisplay -Width 1920 -Height 1080

Write-Host '4/6 Switching to 4K UHD 3840x2160 @ 60 Hz...' -ForegroundColor Cyan
Set-DisplayMode -DeviceName $virtualDisplay -Width 3840 -Height 2160 -RefreshRate 60
Assert-DisplayMode -DeviceName $virtualDisplay -Width 3840 -Height 2160

Write-Host '5/6 Switching back to FullHD 1920x1080 @ 60 Hz...' -ForegroundColor Cyan
Set-DisplayMode -DeviceName $virtualDisplay -Width 1920 -Height 1080 -RefreshRate 60
Assert-DisplayMode -DeviceName $virtualDisplay -Width 1920 -Height 1080

Write-Host '6/6 Final display topology:' -ForegroundColor Cyan
Show-Screens
Write-Host ''
Write-Host 'ALPHA MODE TEST PASSED: recreate + FullHD + 4K + FullHD.' -ForegroundColor Green

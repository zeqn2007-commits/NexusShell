# Launches Nexus on a given page/theme and saves a screenshot of its window.
# Usage: tools/capture.ps1 -Exe <path to Nexus.exe> -Page home -Theme dark -Out shot.png
param(
    [Parameter(Mandatory)] [string]$Exe,
    [string]$Page = 'home',
    [ValidateSet('dark', 'light')] [string]$Theme = 'dark',
    [Parameter(Mandatory)] [string]$Out,
    [int]$WaitSeconds = 6
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class NxWin32 {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT rect, int size);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] public static extern void SwitchToThisWindow(IntPtr hwnd, bool altTab);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
}
'@
[NxWin32]::SetProcessDPIAware() | Out-Null

$process = Start-Process -FilePath $Exe -ArgumentList "--page=$Page", "--theme=$Theme" -PassThru
try {
    $deadline = (Get-Date).AddSeconds(20)
    do {
        Start-Sleep -Milliseconds 300
        $process.Refresh()
    } while ($process.MainWindowHandle -eq [IntPtr]::Zero -and (Get-Date) -lt $deadline -and -not $process.HasExited)

    if ($process.HasExited) { throw "Nexus exited early with code $($process.ExitCode)" }
    $hwnd = $process.MainWindowHandle
    [NxWin32]::ShowWindow($hwnd, 9) | Out-Null
    [NxWin32]::SwitchToThisWindow($hwnd, $true)
    [NxWin32]::SetForegroundWindow($hwnd) | Out-Null
    Start-Sleep -Seconds $WaitSeconds

    $rect = New-Object NxWin32+RECT
    [NxWin32]::DwmGetWindowAttribute($hwnd, 9, [ref]$rect, [System.Runtime.InteropServices.Marshal]::SizeOf($rect)) | Out-Null
    $width = $rect.Right - $rect.Left
    $height = $rect.Bottom - $rect.Top
    $bitmap = New-Object System.Drawing.Bitmap $width, $height
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.CopyFromScreen($rect.Left, $rect.Top, 0, 0, $bitmap.Size)
    $graphics.Dispose()
    $bitmap.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
    Write-Output "Saved $Out ($width x $height)"
}
finally {
    if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force }
}

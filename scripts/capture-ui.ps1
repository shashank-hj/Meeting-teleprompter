param([Parameter(Mandatory)][int]$AppProcessId, [string]$OutputPath = 'artifacts/ui-preview.png')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class TeleprompterWindowCapture {
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr window);
}
'@
[TeleprompterWindowCapture]::SetThreadDpiAwarenessContext([IntPtr](-4)) | Out-Null
$app = Get-Process -Id $AppProcessId
if ($app.ProcessName -ne 'MeetingTeleprompter.App') { throw 'Only the Teleprompter window may be captured.' }
[TeleprompterWindowCapture]::ShowWindow($app.MainWindowHandle, 9) | Out-Null
Start-Sleep -Milliseconds 600
$bounds = New-Object TeleprompterWindowCapture+Rect
if (-not [TeleprompterWindowCapture]::GetWindowRect($app.MainWindowHandle, [ref]$bounds)) { throw 'Window bounds unavailable.' }
$dpi = [TeleprompterWindowCapture]::GetDpiForWindow($app.MainWindowHandle)
# PrintWindow renders this WinUI window into a 96-DPI destination DC.
$scale = 96.0 / [Math]::Max(96, $dpi)
$bitmap = New-Object System.Drawing.Bitmap([int](($bounds.Right - $bounds.Left) * $scale), [int](($bounds.Bottom - $bounds.Top) * $scale))
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$dc = $graphics.GetHdc()
try { if (-not [TeleprompterWindowCapture]::PrintWindow($app.MainWindowHandle, $dc, 2)) { throw 'Window capture failed.' } }
finally { $graphics.ReleaseHdc($dc); $graphics.Dispose() }
$target = [IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $PSScriptRoot) $OutputPath))
$allowed = [IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts')) + [IO.Path]::DirectorySeparatorChar
if (-not $target.StartsWith($allowed, [StringComparison]::OrdinalIgnoreCase)) { $bitmap.Dispose(); throw 'Captures must be saved inside artifacts.' }
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
try { $bitmap.Save($target, [System.Drawing.Imaging.ImageFormat]::Png) } finally { $bitmap.Dispose() }
Write-Host "Saved app-only capture: $target"

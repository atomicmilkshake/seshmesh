param(
    [string]$ThemeName = "Tokyo Night",
    [string]$OutFile = (Join-Path $PSScriptRoot "..\artifacts\casr_rendered.png")
)

$OutFile = [System.IO.Path]::GetFullPath($OutFile)
New-Item -ItemType Directory -Force -Path (Split-Path $OutFile) | Out-Null

Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase
Add-Type -Path (Join-Path $PSScriptRoot '..\src\Casr.Core\bin\Release\net8.0\Casr.Core.dll')
Add-Type -Path (Join-Path $PSScriptRoot '..\src\Casr.App\bin\Release\net8.0-windows\Casr.App.dll')

if (-not [System.Windows.Application]::Current) {
    $app = New-Object Casr.App.App
}
[Casr.App.Theming.ThemeManager]::ApplyTheme($ThemeName)

$win = New-Object Casr.App.MainWindow
$win.Width = 1280
$win.Height = 780
$win.Measure([System.Windows.Size]::new(1280, 780))
$win.Arrange([System.Windows.Rect]::new(0, 0, 1280, 780))
$win.UpdateLayout()

$rtb = New-Object System.Windows.Media.Imaging.RenderTargetBitmap(1280, 780, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
$rtb.Render($win)

$encoder = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
$encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($rtb))

$fs = [System.IO.File]::OpenWrite($OutFile)
$encoder.Save($fs)
$fs.Close()
Write-Host "Rendered $ThemeName successfully to $OutFile"

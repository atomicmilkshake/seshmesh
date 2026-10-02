param(
    [string]$OutPath = (Join-Path $PSScriptRoot "..\artifacts\casr_screenshot.png")
)

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$OutPath = [System.IO.Path]::GetFullPath($OutPath)
New-Item -ItemType Directory -Force -Path (Split-Path $OutPath) | Out-Null

$exe = Join-Path $PSScriptRoot "..\src\Casr.App\bin\Release\net8.0-windows\Casr.App.exe"
$proc = Start-Process -FilePath $exe -PassThru

Write-Host "Waiting 3 seconds for UI to load and render..."
Start-Sleep -Seconds 3

$screen = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$bmp = New-Object System.Drawing.Bitmap $screen.Width, $screen.Height
$graphics = [System.Drawing.Graphics]::FromImage($bmp)
$graphics.CopyFromScreen($screen.Location, [System.Drawing.Point]::Empty, $screen.Size)

$bmp.Save($OutPath, [System.Drawing.Imaging.ImageFormat]::Png)
$graphics.Dispose()
$bmp.Dispose()

Stop-Process -Id $proc.Id -Force
Write-Host "Screenshot saved successfully to $OutPath"

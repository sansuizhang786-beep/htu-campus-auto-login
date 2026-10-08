$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
[void][Reflection.Assembly]::LoadFrom((Join-Path $PSScriptRoot 'HTUAutoLogin.exe'))
[HtuAutoLogin.Storage]::DataDirectory = Join-Path $PSScriptRoot 'test-output\gui'
[Windows.Forms.Application]::EnableVisualStyles()
$form = New-Object HtuAutoLogin.SettingsForm
try {
    $form.StartPosition = [Windows.Forms.FormStartPosition]::Manual
    $form.Location = New-Object Drawing.Point(-32000, -32000)
    $form.ShowInTaskbar = $false
    $form.Show()
    [Windows.Forms.Application]::DoEvents()
    $form.PerformLayout()
    $bitmap = New-Object Drawing.Bitmap($form.Width, $form.Height)
    try {
        $form.DrawToBitmap($bitmap, (New-Object Drawing.Rectangle(0, 0, $form.Width, $form.Height)))
        $outDir = Join-Path $PSScriptRoot 'test-output'
        [void][IO.Directory]::CreateDirectory($outDir)
        $bitmap.Save((Join-Path $outDir 'settings-preview.png'), [Drawing.Imaging.ImageFormat]::Png)
    } finally { $bitmap.Dispose() }
} finally { $form.Dispose() }
Write-Output 'GUI rendered without starting the worker or writing user settings.'

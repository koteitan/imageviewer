# Runs imageviewer's code (loaded from the exe by reflection) for the jobs in a JSON file and
# writes the results to another JSON file. Nothing is shown on the screen: viewer windows are
# created but never shown. Run with powershell.exe -STA.
#
#   powershell.exe -NoProfile -STA -ExecutionPolicy Bypass -File harness.ps1 -exe <exe> -jobs <in.json> -results <out.json> -data <dir>
#
# Job kinds (fields):
#   load      path, out          decode as the viewer does, write the shown image as PNG
#   plan      path               ImageSaver.Plan -> mode, reason
#   save      path, deg          ImageSaver.SaveRotated
#   convert   path, deg, kind, target   ImageSaver.SaveConverted (kind: Jpeg / Png)
#   make_jxr  src, out, rotation        JPEG XR (lossless) from a PNG, with WmpBitmapEncoder.Rotation
#   readonly_save path, deg      save into a read-only copy in %TEMP%; reports the error and whether the bytes changed
#   viewer    path, actions[]    ViewerWindow (hidden); actions: ctrlwheel:<delta>, wheel:<delta>, rotate:<quarter turns>,
#                                state, popup, save, navigate:<+1|-1>
param([string]$exe, [string]$jobs, [string]$results, [string]$data)
$ErrorActionPreference = 'Stop'
$env:IMAGEVIEWER_DATA_DIR = $data
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase
# .NET does not load assemblies from network paths (\\wsl.localhost\...): use a local copy.
$local = Join-Path $env:TEMP 'imageviewer-test'
New-Item -ItemType Directory $local -Force | Out-Null
Copy-Item $exe (Join-Path $local 'imageviewer.exe') -Force
$asm = [Reflection.Assembly]::LoadFrom((Join-Path $local 'imageviewer.exe'))
$bf = [Reflection.BindingFlags]'NonPublic,Public,Instance,Static'
$Loader = $asm.GetType('ImageViewer.ImageLoader')
$Saver = $asm.GetType('ImageViewer.ImageSaver')
$Viewer = $asm.GetType('ImageViewer.ViewerWindow')
$KindType = $asm.GetType('ImageViewer.ImageSaver+Kind')

function Write-Png($src, $path) {
  $bgra = New-Object System.Windows.Media.Imaging.FormatConvertedBitmap($src, [System.Windows.Media.PixelFormats]::Bgra32, $null, 0)
  $wb = New-Object System.Windows.Media.Imaging.WriteableBitmap($bgra)
  $enc = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
  $enc.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($wb))
  $fs = [IO.File]::Create($path); $enc.Save($fs); $fs.Close()
}

function Inner($e) { if ($e.Exception.InnerException) { $e.Exception.InnerException.Message } else { $e.Exception.Message } }

function Viewer-State($w) {
  @{
    rotation = $Viewer.GetProperty('Rotation').GetValue($w)
    scale = $Viewer.GetProperty('Scale').GetValue($w)
    path = $Viewer.GetProperty('FilePath').GetValue($w)
    x = $Viewer.GetField('imgX', $bf).GetValue($w); y = $Viewer.GetField('imgY', $bf).GetValue($w)
    w = $Viewer.GetField('imgW', $bf).GetValue($w); h = $Viewer.GetField('imgH', $bf).GetValue($w)
  }
}

function Send-Wheel($w, $delta, $keys) {
  $wparam = [IntPtr]([int64](($delta -band 0xFFFF) -shl 16) -bor $keys)
  $args4 = @([uint32]0x020A, $wparam, [IntPtr]::Zero, $null)
  $Viewer.GetMethod('HandleMessage', $bf).Invoke($w, $args4) | Out-Null
}

$list = Get-Content -Raw -Encoding UTF8 $jobs | ConvertFrom-Json
$out = @()
foreach ($job in $list) {
  $r = @{ id = $job.id; kind = $job.kind }
  try {
    switch ($job.kind) {
      'load' {
        $bmp = $Loader.GetMethod('Load', $bf).Invoke($null, @($job.path))
        Write-Png $bmp $job.out
        $r.w = $bmp.PixelWidth; $r.h = $bmp.PixelHeight
      }
      'plan' {
        $a = @($job.path, $null)
        $r.mode = $Saver.GetMethod('Plan', $bf).Invoke($null, $a).ToString()
        $r.reason = $a[1]
      }
      'save' {
        $Saver.GetMethod('SaveRotated', $bf).Invoke($null, @($job.path, [int]$job.deg)) | Out-Null
      }
      'convert' {
        $kind = [Enum]::Parse($KindType, $job.kind2)
        $Saver.GetMethod('SaveConverted', $bf).Invoke($null, @($job.path, [int]$job.deg, $kind, $job.target)) | Out-Null
      }
      'make_jxr' {
        $fs = [IO.File]::OpenRead($job.src)
        $frame = [System.Windows.Media.Imaging.BitmapDecoder]::Create($fs, 'None', 'OnLoad').Frames[0]; $fs.Close()
        $enc = New-Object System.Windows.Media.Imaging.WmpBitmapEncoder
        $enc.Lossless = $true; $enc.Rotation = $job.rotation
        $enc.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($frame))
        $o = [IO.File]::Create($job.out); $enc.Save($o); $o.Close()
      }
      'readonly_save' {
        $dir = Join-Path $env:TEMP ('imageviewer-test-' + [Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory $dir | Out-Null
        $copy = Join-Path $dir ([IO.Path]::GetFileName($job.path))
        Copy-Item $job.path $copy
        $before = (Get-FileHash $copy).Hash
        (Get-Item $copy).IsReadOnly = $true
        try { $Saver.GetMethod('SaveRotated', $bf).Invoke($null, @($copy, [int]$job.deg)) | Out-Null; $r.error = $null }
        catch { $r.error = Inner $_ }
        (Get-Item $copy).IsReadOnly = $false
        $r.changed = ((Get-FileHash $copy).Hash -ne $before)
        $r.leftover = @(Get-ChildItem -Force $dir | Where-Object { $_.Name -ne [IO.Path]::GetFileName($job.path) } | ForEach-Object { $_.Name })
        Remove-Item -LiteralPath $copy -Force
        Remove-Item -LiteralPath $dir -Force
      }
      'viewer' {
        $bmp = $Loader.GetMethod('Load', $bf).Invoke($null, @($job.path))
        $w = $Viewer.GetConstructors($bf)[0].Invoke(@($job.path, $bmp))
        $steps = @()
        foreach ($action in $job.actions) {
          $name, $arg = $action.Split(':')
          $s = @{ action = $action }
          switch ($name) {
            'ctrlwheel' { Send-Wheel $w ([int]$arg) 0x0008 }
            'wheel' { Send-Wheel $w ([int]$arg) 0 }
            'rotate' { $Viewer.GetMethod('Rotate', $bf).Invoke($w, @([int]$arg)) | Out-Null }
            'navigate' { $Viewer.GetMethod('Navigate', $bf).Invoke($w, @([int]$arg)) | Out-Null }
            'save' { $Viewer.GetMethod('SaveRotation', $bf).Invoke($w, @()) | Out-Null }
            'popup' {
              $p = $Viewer.GetField('popup', $bf).GetValue($w)
              $pt = $p.GetType()
              $pt.GetMethod('Refresh', $bf).Invoke($p, @()) | Out-Null
              $s.rotate_visible = $pt.GetField('rotateLabel', $bf).GetValue($p).Visibility.ToString()
              $s.rotate_text = $pt.GetField('rotateInfo', $bf).GetValue($p).Text
              $s.save_enabled = $pt.GetField('saveButton', $bf).GetValue($p).IsEnabled
            }
            'state' { }
          }
          $s.state = Viewer-State $w
          $steps += $s
        }
        $r.steps = $steps
        $Viewer.GetMethod('RememberPlace', $bf).Invoke($w, @()) | Out-Null
      }
      default { throw "unknown job kind $($job.kind)" }
    }
    $r.ok = $true
  } catch {
    $r.ok = $false; $r.error = Inner $_
  }
  $out += $r
}
ConvertTo-Json -InputObject $out -Depth 8 | Set-Content -Encoding UTF8 $results

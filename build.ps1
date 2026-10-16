# 编译脚本：使用 Windows 自带的 C# 编译器（.NET Framework 4.x）
# 无需安装 Visual Studio / .NET SDK / 任何第三方依赖。
# 用法：powershell -ExecutionPolicy Bypass -File build.ps1

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$src  = Join-Path $root 'src'
$dist = Join-Path $root 'dist'

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path $csc)) { throw '未找到 csc.exe，需要 Windows 自带的 .NET Framework 4.x' }

New-Item -ItemType Directory -Force -Path $dist | Out-Null

$common = @(
  '/nologo', '/optimize+', '/langversion:5', '/codepage:65001',
  '/r:System.dll', '/r:System.Core.dll', '/r:System.Net.Http.dll', '/r:System.Web.Extensions.dll'
)

Write-Host '编译 GUI: HikWarranty.exe'
& $csc @common '/target:winexe' "/out:$dist\HikWarranty.exe" `
  (Join-Path $src 'Models.cs') (Join-Path $src 'Api.cs') (Join-Path $src 'Gui.cs') `
  '/r:System.Drawing.dll' '/r:System.Windows.Forms.dll' `
  "/win32manifest:$(Join-Path $root 'app.manifest')"

Write-Host '编译 CLI: hikq.exe'
& $csc @common '/target:exe' "/out:$dist\hikq.exe" `
  (Join-Path $src 'Models.cs') (Join-Path $src 'Api.cs') (Join-Path $src 'Cli.cs')

Write-Host ''
Get-ChildItem $dist -File | Select-Object Name, Length, LastWriteTime | Format-Table -AutoSize
Write-Host '完成。'
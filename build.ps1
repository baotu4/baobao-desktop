param([string]$OutputDirectory = (Join-Path $PSScriptRoot 'bin'), [switch]$Review)
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($env:WINDIR)) { throw '缺少 Windows 环境变量 WINDIR，无法定位 .NET Framework 编译器。' }
$baobaoCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $baobaoCompiler -PathType Leaf)) { throw ('未找到 Windows .NET Framework C# 编译器：' + $baobaoCompiler) }
$baobaoFramework = Split-Path -Parent $baobaoCompiler
$baobaoWpf = Join-Path $baobaoFramework 'WPF'
$baobaoFrames = Join-Path $PSScriptRoot 'assets\frames'
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$baobaoArguments = @('/nologo','/langversion:5','/target:winexe','/platform:x64','/optimize+',('/win32manifest:' + (Join-Path $PSScriptRoot 'app.manifest')),('/out:' + (Join-Path $OutputDirectory 'Baobao.exe')))
if ($Review) { $baobaoArguments += '/define:REVIEW' }
foreach ($baobaoReference in @('System.dll','System.Core.dll','System.Xml.dll','System.Xaml.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Runtime.Serialization.dll')) { $baobaoArguments += '/reference:' + (Join-Path $baobaoFramework $baobaoReference) }
foreach ($baobaoReference in @('PresentationFramework.dll','PresentationCore.dll','WindowsBase.dll')) { $baobaoArguments += '/reference:' + (Join-Path $baobaoWpf $baobaoReference) }
for ($baobaoIndex = 0; $baobaoIndex -lt 44; $baobaoIndex++) {
    $baobaoNumber = $baobaoIndex.ToString('00')
    $baobaoFrame = Join-Path $baobaoFrames ('baobao-frame-' + $baobaoNumber + '.png')
    if (-not (Test-Path -LiteralPath $baobaoFrame)) { throw ('缺少动作素材：' + $baobaoFrame) }
    $baobaoArguments += '/resource:' + $baobaoFrame + ',frame' + $baobaoNumber
}
$baobaoCleanPlate = Join-Path $PSScriptRoot 'assets\idle-clean-plate.png'
if (-not (Test-Path -LiteralPath $baobaoCleanPlate)) { throw '缺少耳尾遮挡补全素材。' }
$baobaoArguments += '/resource:' + $baobaoCleanPlate + ',cleanplate'
$baobaoTrayIcon = Join-Path $PSScriptRoot 'assets\baobao-tray.ico'
if (-not (Test-Path -LiteralPath $baobaoTrayIcon)) { throw '缺少包包头像图标。' }
$baobaoArguments += '/resource:' + $baobaoTrayIcon + ',trayicon'
$baobaoArguments += '/win32icon:' + $baobaoTrayIcon
foreach ($baobaoSource in @('AssemblyInfo.cs','BaobaoDesktop.cs','InteractionController.cs','IdleMotionController.cs','IdlePetRenderer.cs','DropRouter.cs','RecycleService.cs')) { $baobaoArguments += Join-Path $PSScriptRoot $baobaoSource }
& $baobaoCompiler $baobaoArguments
if ($LASTEXITCODE -ne 0) { throw '包包桌面摆件编译失败。' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Baobao.exe.config') -Destination (Join-Path $OutputDirectory 'Baobao.exe.config') -Force
Get-Item -LiteralPath (Join-Path $OutputDirectory 'Baobao.exe') | Select-Object FullName,Length

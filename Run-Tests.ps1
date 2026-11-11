param([switch]$NativeChecks)
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($env:WINDIR)) { throw '缺少 Windows 环境变量 WINDIR，无法定位 .NET Framework 编译器。' }
$baobaoCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $baobaoCompiler -PathType Leaf)) { throw ('未找到 Windows .NET Framework C# 编译器：' + $baobaoCompiler) }
$baobaoFramework = Split-Path -Parent $baobaoCompiler
$baobaoWpf = Join-Path $baobaoFramework 'WPF'
$baobaoTestSource = Join-Path $PSScriptRoot 'tests'
$baobaoTestBin = Join-Path $baobaoTestSource 'bin'
$baobaoTestLogs = Join-Path $baobaoTestSource 'logs'
if ($NativeChecks) {
    Write-Output 'NativeChecks 先构建本仓库 bin\Baobao.exe（不启动程序）。'
    & (Join-Path $PSScriptRoot 'build.ps1') -OutputDirectory (Join-Path $PSScriptRoot 'bin')
}
New-Item -ItemType Directory -Path $baobaoTestBin,$baobaoTestLogs -Force | Out-Null
function Invoke-BaobaoCheck {
    param([string]$Name,[string[]]$Sources,[string[]]$Extra=@(),[string[]]$Arguments=@())
    $baobaoTestExe = Join-Path $baobaoTestBin ($Name+'.exe')
    $baobaoCheckArguments = @('/nologo','/langversion:5','/target:exe','/platform:x64',('/out:'+$baobaoTestExe)) + $Extra + $Sources
    & $baobaoCompiler $baobaoCheckArguments
    if ($LASTEXITCODE -ne 0) { throw ($Name+' 编译失败。') }
    & $baobaoTestExe @Arguments 2>&1 | Tee-Object -FilePath (Join-Path $baobaoTestLogs ($Name+'.log'))
    if ($LASTEXITCODE -ne 0) { throw ($Name+' 检查失败。') }
}
$baobaoLogic = Join-Path $PSScriptRoot 'InteractionController.cs'
$baobaoIdle = Join-Path $PSScriptRoot 'IdleMotionController.cs'
$baobaoRenderer = Join-Path $PSScriptRoot 'IdlePetRenderer.cs'
$baobaoRouter = Join-Path $PSScriptRoot 'DropRouter.cs'
Invoke-BaobaoCheck 'IdleMotionTests' @($baobaoIdle,(Join-Path $baobaoTestSource 'IdleMotionControllerTests.cs'))
Invoke-BaobaoCheck 'InteractionTests' @($baobaoLogic,(Join-Path $baobaoTestSource 'InteractionControllerTests.cs'))
Invoke-BaobaoCheck 'InteractionPacingTests' @($baobaoLogic,(Join-Path $baobaoTestSource 'InteractionPacingRegressionTests.cs'))
Invoke-BaobaoCheck 'PetDetailTests' @($baobaoLogic,(Join-Path $baobaoTestSource 'PetDetailRegressionTests.cs'))
Invoke-BaobaoCheck 'DropRouterTests' @($baobaoRouter,(Join-Path $baobaoTestSource 'DropRouterTests.cs')) @('/r:System.Runtime.Serialization.dll','/r:System.Xml.dll')
Invoke-BaobaoCheck 'V1SoakTests' @($baobaoLogic,$baobaoIdle,(Join-Path $baobaoTestSource 'V1SoakTests.cs'))
$baobaoWpfReferences = @()
foreach($baobaoAssembly in @('PresentationFramework.dll','PresentationCore.dll','WindowsBase.dll')) { $baobaoWpfReferences += '/r:'+(Join-Path $baobaoWpf $baobaoAssembly) }
$baobaoWpfReferences += '/r:'+(Join-Path $baobaoFramework 'System.Xaml.dll')
Invoke-BaobaoCheck 'RendererTests' @($baobaoRenderer,$baobaoIdle,(Join-Path $baobaoTestSource 'RendererTests.cs')) $baobaoWpfReferences @((Join-Path $PSScriptRoot 'assets\idle-clean-plate.png'))
if ($NativeChecks) {
    Write-Output 'NativeChecks 仅回收测试程序自己创建的文件，不操作其他回收站条目。'
    $baobaoIntegration = Join-Path $baobaoTestBin 'integration'
    New-Item -ItemType Directory -Path $baobaoIntegration -Force | Out-Null
    $baobaoIntegrationExe = Join-Path $baobaoIntegration 'NativeChecks.exe'
    $baobaoExtra = $baobaoWpfReferences + @('/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:System.Runtime.Serialization.dll','/r:System.Xml.dll','/main:BaobaoDesktop.MainIntegrationTests')
    for ($baobaoIndex=0;$baobaoIndex -lt 44;$baobaoIndex++) {
        $baobaoNumber=$baobaoIndex.ToString('00')
        $baobaoExtra += '/resource:'+(Join-Path $PSScriptRoot ('assets\frames\baobao-frame-'+$baobaoNumber+'.png'))+',frame'+$baobaoNumber
    }
    $baobaoExtra += '/resource:'+(Join-Path $PSScriptRoot 'assets\idle-clean-plate.png')+',cleanplate'
    $baobaoExtra += '/resource:'+(Join-Path $PSScriptRoot 'assets\baobao-tray.ico')+',trayicon'
    $baobaoSources = @('AssemblyInfo.cs','BaobaoDesktop.cs','InteractionController.cs','IdleMotionController.cs','IdlePetRenderer.cs','DropRouter.cs','RecycleService.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
    $baobaoSources += Join-Path $baobaoTestSource 'MainIntegrationTests.cs'
    & $baobaoCompiler /nologo /langversion:5 /target:exe /platform:x64 ('/out:'+$baobaoIntegrationExe) $baobaoExtra $baobaoSources
    if ($LASTEXITCODE -ne 0) { throw 'NativeChecks 编译失败。' }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Baobao.exe.config') -Destination ($baobaoIntegrationExe+'.config') -Force
    & $baobaoIntegrationExe 2>&1 | Tee-Object -FilePath (Join-Path $baobaoTestLogs 'NativeChecks.log')
    if ($LASTEXITCODE -ne 0) { throw 'NativeChecks 检查失败。' }
}
Write-Output '所选自动检查均已通过。真实鼠标手势、拖放和显示效果需另行实机验收。'

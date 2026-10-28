<#
    松月启动器唯一构建入口：本地与 GitHub Actions 云端共用本脚本。

    .\installer\build.ps1 -Version 1.0.0
      → installer\output\SongYueLauncher-windows-x64-1.0.0.exe   （默认：瘦身 Bundle）
      → installer\output\SongYueLauncher-windows-x64-1.0.0.msi   （-Full：旧版自包含 MSI）

    参数：
      -Version      版本号，可带非数字后缀（1.0.0）。文件名用完整串，
                    MSI 的 ProductVersion 只取前面的数字段（Windows Installer 限制）。
      -SkipPublish  复用已有 installer\publish 产物，只重打安装包（加速调试）。
      -Full         构建旧版「自包含」MSI（.NET 与 Windows App SDK 运行时全部内嵌，包体 ~110MB）。
                    默认瘦身 Bundle：应用框架依赖发布（发布产物 ~60MB → MSI 高压缩内嵌），
                    .NET 桌面运行时安装时按检测按需下载、Windows App Runtime 始终幂等安装
                    （检测锚点与兜底链路见 Bundle.wxs 注释）。两种形态启动行为一致。

    说明：WiX v4 没有内置目录收割（Files 元素是 v5 才有的），Heat 又要额外装扩展，
    所以这里自己扫描发布目录生成 harvest.wxs。组件 GUID 由相对路径派生，
    同一份代码重复打包得到稳定 GUID，升级安装才会正确替换旧文件。
#>
[CmdletBinding()]
param(
    [string]$Version = '1.0.0',
    [switch]$SkipPublish,
    [switch]$Full
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$installerDir = $PSScriptRoot
$repoRoot = Split-Path -Parent $installerDir
$project = Join-Path $repoRoot 'src\ComfyUiRocmLauncher.GUI\ComfyUiRocmLauncher.GUI.csproj'
$icon = Join-Path $repoRoot 'src\ComfyUiRocmLauncher.GUI\Assets\AppIcon.ico'
$publishDir = Join-Path $installerDir 'publish'
$outputDir = Join-Path $installerDir 'output'
$harvestWxs = Join-Path $installerDir 'harvest.wxs'

# 包标识：UpgradeCode 一经发布不得更改，否则升级安装识别不到旧版本
$appName = 'SongYueLauncher'
$manufacturer = 'self-exiler'
$upgradeCode = '2EF83F16-F613-4995-80EC-F62AFC7B9A49'

if (-not ($Version -match '^(\d+(?:\.\d+){0,2})')) {
    throw "版本号 '$Version' 无法解析出数字主版本（形如 0.1.0 或 0.1.0beta；MSI ProductVersion 只有 major.minor.build 三段）"
}
$msiVersion = $Matches[1]
$msiPath = Join-Path $outputDir "$appName-windows-x64-$Version.msi"
$bundlePath = Join-Path $outputDir "$appName-windows-x64-$Version.exe"
# Bundle 自身升级码：与 MSI UpgradeCode 分离——旧版自包含 MSI 仍靠 MSI 内部 MajorUpgrade 被替换
$bundleUpgradeCode = 'AA65422F-A713-4020-855C-6A502C7F4F73'
# 外置运行时的下载源（构建期下载到 cache 并计算 SHA1/大小注入 Bundle；-Full 不需要）。
# 两个源都必须钉死不可变 URL：WAR 若用 aka.ms「latest」短链，微软轮换后存量 Bundle 的哈希校验
# 必然失败——所有已发布安装包同时变坏且无法远程修复（审查 #10）。版本与 GUI csproj 的
# WindowsAppSDK 包版本保持一致（升级 SDK 时同步改这里）。
$dotNetVersion = '10.0.13'
$dotNetUrl = "https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/$dotNetVersion/windowsdesktop-runtime-$dotNetVersion-win-x64.exe"
$warVersion = '1.8.260317003'
$warUrl = "https://aka.ms/windowsappsdk/1.8/$warVersion/windowsappruntimeinstall-x64.exe"
$cacheDir = Join-Path $installerDir 'cache'

Write-Output "== 松月启动器打包 =="
Write-Output "   文件名版本 : $Version"
Write-Output "   MSI 版本   : $msiVersion"
Write-Output "   形态       : $(if ($Full) { '自包含 MSI（运行时内嵌）' } else { '瘦身 Bundle（运行时按需下载）' })"

# ---------- 1. 发布应用本体 ----------
# 两种形态都必须显式写死发布参数（*.pubxml 被 .gitignore 忽略，隐式形态会漂移成坏包）：
# 自包含（-Full）：运行时全部内嵌；瘦身（默认）：.NET 与 Windows App SDK 运行时外置给 Bundle。
if ($SkipPublish) {
    if (-not (Test-Path (Join-Path $publishDir 'ComfyUiRocmLauncher.GUI.exe'))) {
        throw "-SkipPublish 需要已存在的 $publishDir 产物，请先不带该参数运行一次"
    }
    Write-Output "== 跳过发布，复用 $publishDir =="
}
else {
    if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
    if ($Full) {
        dotnet publish $project -c Release --self-contained true -o $publishDir --nologo -v minimal
    }
    else {
        dotnet publish $project -c Release --self-contained false -p:WindowsAppSDKSelfContained=false -o $publishDir --nologo -v minimal
    }
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败（退出码 $LASTEXITCODE）" }
    if (-not (Test-Path (Join-Path $publishDir 'ComfyUiRocmLauncher.GUI.exe'))) {
        throw "发布产物缺少 ComfyUiRocmLauncher.GUI.exe"
    }
}

# XAML 资源索引与应用图标是两类静默坏包：缺了本机能跑，但会出现打不开 / 没图标。
# 形态哨兵：自包含必须有 coreclr.dll；瘦身必须有 Bootstrap 托管包装（同时证明 WASDK 运行时已外置）。
# 必须对 -SkipPublish 也无条件校验（按本次 -Full 取值）：复用不同形态的旧产物重打 = 静默坏包（审查 #19）
$required = @('ComfyUiRocmLauncher.GUI.pri', 'Assets\AppIcon.ico')
if ($Full) { $required += 'coreclr.dll' } else { $required += 'Microsoft.WindowsAppRuntime.Bootstrap.Net.dll' }
foreach ($item in $required) {
    if (-not (Test-Path (Join-Path $publishDir $item))) {
        throw "发布产物缺少 $item，产物与目标形态（$(if ($Full) { '自包含' } else { '瘦身' })）不符"
    }
}

# ---------- 2. 定位 WiX v4 命令行 ----------
$wix = $null
foreach ($candidate in @(
    (Get-Command wix.exe -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source),
    (Join-Path $env:USERPROFILE '.dotnet\tools\wix.exe'),
    (Join-Path $env:ProgramFiles 'dotnet\tools\wix.exe')
)) {
    if ($candidate -and (Test-Path $candidate)) { $wix = $candidate; break }
}
if (-not $wix) {
    throw "未找到 WiX 命令行工具，请先执行：dotnet tool install --global wix --version 5.0.2（Bundle.wxs 已用 v5 语法，装 v4 会构建失败）"
}
Write-Output "== 使用 WiX：$wix =="

if (-not (Test-Path $icon)) { throw "应用图标不存在：$icon（MSI 图标 / 快捷方式图标都依赖它）" }

# ---------- 3. 收割发布目录 → harvest.wxs ----------
$md5 = [System.Security.Cryptography.MD5]::Create()

function Get-Hex([string]$seed) {
    (($md5.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($seed)) | ForEach-Object { $_.ToString('X2') }) -join '').Substring(0, 24)
}

function Get-StableGuid([string]$seed) {
    $bytes = $md5.ComputeHash([System.Text.Encoding]::UTF8.GetBytes("SongYueLauncher|$seed"))
    ([guid][byte[]]$bytes).ToString().ToUpper()
}

$files = @(Get-ChildItem -Path $publishDir -File -Recurse | Sort-Object FullName)
if ($files.Count -eq 0) { throw "发布目录为空：$publishDir" }

# 相对目录 → 该目录下的文件；发布根目录映射到 INSTALLFOLDER
$byDir = [ordered]@{}
foreach ($f in $files) {
    $rel = $f.FullName.Substring($publishDir.Length).TrimStart('\', '/')
    $dirRel = Split-Path -Parent $rel
    if (-not $dirRel) { $dirRel = '.' }
    if (-not $byDir.Contains($dirRel)) { $byDir[$dirRel] = New-Object System.Collections.Generic.List[string] }
    $byDir[$dirRel].Add($rel)
}

# 只装子目录、本身没有文件的中间层（如 publish\Assets）也必须拿到 ID，
# 否则其子目录的 DirectoryRef 会指向空值，故补齐每条路径的全部祖先目录
$allDirs = [System.Collections.Generic.SortedSet[string]]::new([System.StringComparer]::Ordinal)
foreach ($dirRel in $byDir.Keys) {
    $cur = $dirRel
    while ($cur -and $cur -ne '.') {
        [void]$allDirs.Add($cur)
        $cur = Split-Path -Parent $cur
    }
}

$dirIds = @{}
$dirIds['.'] = 'INSTALLFOLDER'
foreach ($dirRel in $allDirs) { $dirIds[$dirRel] = 'dir' + (Get-Hex $dirRel) }

$sb = New-Object System.Text.StringBuilder
[void]$sb.AppendLine('<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">')
[void]$sb.AppendLine('  <!-- 由 installer/build.ps1 自动生成，请勿手改 -->')
[void]$sb.AppendLine('  <Fragment>')

# 目录声明按层级深度排序，保证父目录先于子目录出现
foreach ($dirRel in ($allDirs | Sort-Object { ($_ -split '[\\/]').Count })) {
    $parent = Split-Path -Parent $dirRel
    if (-not $parent) { $parent = '.' }
    [void]$sb.AppendLine("    <DirectoryRef Id=`"$($dirIds[$parent])`">")
    [void]$sb.AppendLine("      <Directory Id=`"$($dirIds[$dirRel])`" Name=`"$(Split-Path -Leaf $dirRel)`" />")
    [void]$sb.AppendLine('    </DirectoryRef>')
}

[void]$sb.AppendLine('    <ComponentGroup Id="ApplicationFiles">')
foreach ($dirRel in $byDir.Keys) {
    [void]$sb.AppendLine("      <Component Id=`"cmp$(Get-Hex $dirRel)`" Guid=`"$(Get-StableGuid $dirRel)`" Directory=`"$($dirIds[$dirRel])`">")
    foreach ($rel in $byDir[$dirRel]) {
        [void]$sb.AppendLine("        <File Id=`"f$(Get-Hex $rel)`" Source=`"$(Join-Path $publishDir $rel)`" />")
    }
    [void]$sb.AppendLine('      </Component>')
}
[void]$sb.AppendLine('    </ComponentGroup>')
[void]$sb.AppendLine('  </Fragment>')
[void]$sb.AppendLine('</Wix>')

[System.IO.File]::WriteAllText($harvestWxs, $sb.ToString(), (New-Object System.Text.UTF8Encoding($true)))
Write-Output "== 收割完成：$($files.Count) 个文件，$($byDir.Count) 个目录 =="

# ---------- 4. 生成 MSI ----------
New-Item -ItemType Directory -Force -Path $outputDir | Out-Null
if (Test-Path $msiPath) { Remove-Item $msiPath -Force }

& $wix build (Join-Path $installerDir 'Product.wxs') $harvestWxs `
    -arch x64 `
    -o $msiPath `
    -d ProductVersion=$msiVersion `
    -d Manufacturer=$manufacturer `
    -d UpgradeCode=$upgradeCode `
    -d IconPath=$icon
if ($LASTEXITCODE -ne 0) { throw "wix build 失败（退出码 $LASTEXITCODE）" }

$sizeMB = [math]::Round((Get-Item $msiPath).Length / 1MB, 1)
Write-Output "== 完成：$msiPath （$sizeMB MB）=="

if ($Full) { return }

# ---------- 5. 构建瘦身 Bundle（默认形态）----------
# 运行时依赖在构建期下载到 cache（重复打包复用），计算 SHA1/大小注入 RemotePayload——
# Burn 在用户安装时按这些哈希校验下载。两个源都钉死不可变 URL（见文件头部的 $warUrl 注释）。
function Get-InstallerPayload([string]$url, [string]$fileName) {
    New-Item -ItemType Directory -Force -Path $cacheDir | Out-Null
    $path = Join-Path $cacheDir $fileName
    if (-not (Test-Path $path)) {
        # 先下到 .tmp 再原子改名：下载中断（网络抖动/Ctrl+C）留下的半截文件若被当成完整缓存，
        # 其哈希会被注入 Bundle——构建成功但所有用户安装时校验必失败（审查 #19）
        $tmp = "$path.tmp"
        try {
            Write-Host "   下载依赖：$url"
            Invoke-WebRequest -Uri $url -OutFile $tmp -UseBasicParsing
            Move-Item -Force $tmp $path
        }
        finally {
            if (Test-Path $tmp) { Remove-Item $tmp -Force -ErrorAction SilentlyContinue }
        }
    }
    $hash = (Get-FileHash $path -Algorithm SHA1).Hash.ToLower()
    $size = (Get-Item $path).Length
    # 状态行必须走 Write-Host：Write-Output 会混进返回数组，把 [0]/[1] 的哈希/大小错位
    Write-Host "   依赖就绪：$fileName（$([math]::Round($size / 1MB, 1)) MB，SHA1 $hash）"
    return @($hash, $size)
}

Write-Output "== 准备外置运行时依赖 =="
$dotNetPayload = Get-InstallerPayload $dotNetUrl "windowsdesktop-runtime-$dotNetVersion-win-x64.exe"
$warPayload = Get-InstallerPayload $warUrl 'windowsappruntimeinstall-x64.exe'

# Bundle 需要 util（检测搜索）与 bootstrapperApplications（标准引导界面）两个扩展（全局装一次，幂等）。
# 必须钉住与 wix.exe 相同的大版本（不钉会解析到最新 7.x，与 WiX v5 不兼容）
$extList = & $wix extension list -g 2>&1 | Out-String
$wixVersion = (& $wix --version 2>&1 | Out-String).Trim()
$wixMajor = ($wixVersion -split '\.')[0]
foreach ($ext in 'WixToolset.Util.wixext', 'WixToolset.BootstrapperApplications.wixext') {
    if ($extList -notmatch [regex]::Escape($ext)) {
        & $wix extension add -g "$ext/$wixMajor.0.2"
        if ($LASTEXITCODE -ne 0) { throw "wix extension add $ext 失败" }
    }
}

& $wix build (Join-Path $installerDir 'Bundle.wxs') `
    -arch x64 `
    -o $bundlePath `
    -ext WixToolset.Util.wixext `
    -ext WixToolset.BootstrapperApplications.wixext `
    -d ProductVersion=$msiVersion `
    -d Manufacturer=$manufacturer `
    -d BundleUpgradeCode=$bundleUpgradeCode `
    -d IconPath=$icon `
    -d MsiPath=$msiPath `
    -d DotNetVersion=$dotNetVersion `
    -d DotNetUrl=$dotNetUrl `
    -d DotNetSha1=$($dotNetPayload[0]) `
    -d DotNetSize=$($dotNetPayload[1]) `
    -d WarUrl=$warUrl `
    -d WarSha1=$($warPayload[0]) `
    -d WarSize=$($warPayload[1])
if ($LASTEXITCODE -ne 0) { throw "wix build Bundle 失败（退出码 $LASTEXITCODE）" }

$bundleMB = [math]::Round((Get-Item $bundlePath).Length / 1MB, 1)
Write-Output "== 完成：$bundlePath （$bundleMB MB；.NET 桌面运行时按需下载，Windows App Runtime 安装时下载）=="

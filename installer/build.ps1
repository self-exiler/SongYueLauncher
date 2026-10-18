<#
    松月启动器唯一构建入口：本地与 GitHub Actions 云端共用本脚本。

    .\installer\build.ps1 -Version 0.1.0beta
      → installer\output\SongYueLauncher-windows-x64-0.1.0beta.msi

    参数：
      -Version      版本号，可带非数字后缀（0.1.0beta）。文件名用完整串，
                    MSI 的 ProductVersion 只取前面的数字段（Windows Installer 限制）。
      -SkipPublish  复用已有 installer\publish 产物，只重打 MSI（加速调试）。

    说明：WiX v4 没有内置目录收割（Files 元素是 v5 才有的），Heat 又要额外装扩展，
    所以这里自己扫描发布目录生成 harvest.wxs。组件 GUID 由相对路径派生，
    同一份代码重复打包得到稳定 GUID，升级安装才会正确替换旧文件。
#>
[CmdletBinding()]
param(
    [string]$Version = '0.1.0beta',
    [switch]$SkipPublish
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

if (-not ($Version -match '^(\d+(?:\.\d+){0,3})')) {
    throw "版本号 '$Version' 无法解析出数字主版本（形如 0.1.0 或 0.1.0beta）"
}
$msiVersion = $Matches[1]
$msiPath = Join-Path $outputDir "$appName-windows-x64-$Version.msi"

Write-Output "== 松月启动器打包 =="
Write-Output "   文件名版本 : $Version"
Write-Output "   MSI 版本   : $msiVersion"

# ---------- 1. 发布自包含产物（免打包模式，Windows App SDK 运行时随程序输出） ----------
if ($SkipPublish) {
    if (-not (Test-Path (Join-Path $publishDir 'ComfyUiRocmLauncher.GUI.exe'))) {
        throw "-SkipPublish 需要已存在的 $publishDir 产物，请先不带该参数运行一次"
    }
    Write-Output "== 跳过发布，复用 $publishDir =="
}
else {
    if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
    # 必须显式 --self-contained：本地开发靠 Properties\PublishProfiles\win-x64.pubxml 带上该设置，
    # 但 *.pubxml 被 .gitignore 忽略，云端拿不到就会发布成框架依赖版，缺整个 .NET 运行时（坏包）
    dotnet publish $project -c Release --self-contained true -o $publishDir --nologo -v minimal
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败（退出码 $LASTEXITCODE）" }
    if (-not (Test-Path (Join-Path $publishDir 'ComfyUiRocmLauncher.GUI.exe'))) {
        throw "发布产物缺少 ComfyUiRocmLauncher.GUI.exe"
    }
    # 自包含、XAML 资源索引与应用图标是三类静默坏包：缺了本机能跑，但会出现打不开 / 没图标
    foreach ($required in @('coreclr.dll', 'ComfyUiRocmLauncher.GUI.pri', 'Assets\AppIcon.ico', 'Assets\Agents\comfyui-error-analyst.json')) {
        if (-not (Test-Path (Join-Path $publishDir $required))) {
            throw "发布产物缺少 $required，产物不是可用的自包含包"
        }
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
    throw "未找到 WiX v4 命令行工具，请先执行：dotnet tool install --global wix --version 4.0.5"
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
    -d PublishDir=$publishDir `
    -d ProductVersion=$msiVersion `
    -d Manufacturer=$manufacturer `
    -d UpgradeCode=$upgradeCode `
    -d IconPath=$icon
if ($LASTEXITCODE -ne 0) { throw "wix build 失败（退出码 $LASTEXITCODE）" }

$sizeMB = [math]::Round((Get-Item $msiPath).Length / 1MB, 1)
Write-Output "== 完成：$msiPath （$sizeMB MB）=="

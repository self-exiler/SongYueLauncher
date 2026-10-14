# 松月启动器 · SongYue Launcher

*A ComfyUI Launcher for ROCm* —— 为 **AMD ROCm** 平台用户打造的 ComfyUI 图形化启动器（Windows / WinUI 3）。把原本散落在批处理脚本里的启动、调参、环境变量、日志查看，以及环境安装与组件更新，集中成一个可视化桌面应用。

## 背景

上游社区里，[patientx-cfz](https://github.com/patientx-cfz/comfyui-rocm) 已经为 Windows + AMD ROCm 做了一个非常优秀的 **comfyui-rocm 整合包**，并配套了 `comfyui-rocm.bat`、`install.bat`、`rocm-pytorch-package-updater.bat`、`comfyui-rocm-updater.bat` 等脚本。

本项目基于该整合包与其上游 ComfyUI，做如下三点优化，目标是让 ROCm 用户**不必手写命令行与改脚本**：

1. **调参可视化** —— 在patient整合包中，会根据用户GPU型号进行一系列参数优化（包括python主程序的附加启动参数和启动环境变量），但这也是一家之言，本启动器支持可视化精细调参，可利用外网AI，广泛搜索各类社区结论。
2. **国内镜像** —— 将 GitHub 访问与 Python (PyPI) 源替换为国内镜像，解决国内下载整合包、专用轮子与依赖慢/失败的问题（采用"三分法"作用域，见下）。
3. **uv 管理 Python 项目** —— 用 [uv](https://github.com/astral-sh/uv) 创建 venv（Python 钉死 3.12）并统一执行所有 `pip` 操作，替代原整合包的内嵌 Python 方案，更快更稳。由于部分包安装不适合使用`uv add`，所谓未全局使用。

## 主要特性

- **启动 / 停止 / 打开浏览器**：子进程托管、就绪检测、CTRL_BREAK 优雅停止 + 进程树强杀兜底。
- **启动参数页**：数据驱动的参数控件（互斥组自动处理）、按参数名搜索、命令实时预览。
- **环境变量页**：固定注入层（只读）+ 自定义层（可增删改）；启动前自动 `rocm-sdk init` / 路径推导 / 缓存目录创建。
- **配置管理页**：集中 GPU 架构模板选择，**应用即全量替换**环境变量与参数；支持把当前配置**另存为自定义模板**（路径占位符化，换目录仍可用）。
- **环境与更新页**：工具链检查（git / uv / winget，含 UAC 安装）、Python 虚拟环境创建、核心仓库克隆/热更新（git pull 或 clone+robocopy 双策略）、torch nightly 更新（快照 + 下载验证门 + 尽力回滚）、专用轮子清单、项目依赖同步、通用 PyPI 包管理。
- **日志页**：ANSI 着色实时滚动、落盘、磁盘上限保护；**AI智能分析**——一键抓取日志中的 error 级日志与 Python 解释器报错（含 Traceback 完整堆栈），选择 opencode 内嵌模型后发送给本机 opencode 分析并返回排查建议。

### 镜像"三分法"

镜像作用域是硬规则，避免装不上或 404：

- **GitHub 镜像**：仅作用于 `git clone` 与 GitHub release 轮子下载；
- **PyPI 镜像**：仅作用于通用 PyPI 包与项目依赖安装；
- **AMD 官方源**（`nightly.repo.amd.com` / `rocm.nightlies.amd.com`）：**永远直连**，不走任何镜像。

## 界面与运行

- 界面中文，深色/浅色跟随系统；采用 Windows 11「设置」风格（卡片分组、统一内容宽度与留白）。
- 全局状态机：`Idle / Starting / Running / Stopping / BusyEnv`；环境任务与启动/更新互斥（单任务，不排队）。

## 技术栈

- C# / .NET 10 LTS + WinUI 3（Windows App SDK）
- 解决方案：
  - `src/ComfyUiRocmLauncher.Core` —— 服务层类库（零 UI 依赖，可单测）
  - `src/ComfyUiRocmLauncher.GUI` —— WinUI 3 主程序（页面导航、WinRT 互操作）
  - `test/ComfyUiRocmLauncher.Core.Test` —— xUnit 测试
- 依赖方向：`GUI → Core ← Core.Test`
- 参数清单、GPU 模板、专用轮子清单、显卡架构映射均以 Core 内嵌 JSON 资源维护，便于随上游变更更新。

## 运行环境

- Windows 10 1809+ / Windows 11，x64
- comfyui-rocm 整合包目录（可本地已有，也可由"环境与更新"页从你指定的仓库克隆）
- 外部工具：`git.exe`、`uv.exe`（均可在应用内经 winget 安装）、venv 内的 `rocm-sdk`
- 可选工具：`opencode` CLI（npm 全局或官方脚本安装）——仅日志页「AI智能分析」需要，未安装时该功能提示后退出，不影响其它功能

## 构建与运行

开发期（免打包，无需系统开发人员模式；工程只保留 x64 平台）：

```bash
dotnet build ComfyUiRocmLauncher.sln -c Debug
dotnet run --project src/ComfyUiRocmLauncher.GUI/ComfyUiRocmLauncher.GUI.csproj
```

单元测试：

```bash
dotnet test test/ComfyUiRocmLauncher.Core.Test/ComfyUiRocmLauncher.Core.Test.csproj -c Debug
```

打安装包（MSI，唯一构建入口，本地与云端共用）：

```powershell
.\installer\build.ps1 -Version 0.1.0beta
# → installer\output\SongYueLauncher-windows-x64-0.1.0beta.msi
```

依赖 [WiX Toolset v4](https://wixtoolset.org/) 命令行：`dotnet tool install --global wix --version 4.0.5`。
文件名版本可带非数字后缀，MSI 的 ProductVersion 只取数字段（Windows Installer 限制）。

## 发布流程

推送 `v*` 标签即触发 GitHub Actions 云端编译并自动创建 Release 附上 MSI；
手动运行 `Release` 工作流（填版本号）只出包、不建 Release，用于验证管线。

```bash
git push origin main
git tag v0.1.0beta
git push origin v0.1.0beta
```

MSI 未做代码签名，安装前需自行信任（或右键「以管理员身份运行」）；如需企业分发应替换为签名环节。

## 配置文件与日志

- 配置：`%APPDATA%\ComfyUI ROCm Launcher\config.json`（原子写入；损坏自动备份为 `config.broken.*.json` 并重建）
- 日志：`%APPDATA%\ComfyUI ROCm Launcher\logs\`（`comfyui_*.log` 会话日志、`envtask_*.log` 环境任务日志）

## 已知限制

- 环境安装/更新流程中的外部副作用（clone、robocopy、pip、winget 提权等）依赖真实网络与本机环境，需在具备 ROCm 的 Windows 机器上实测。
- `wheel-catalog` 中 GitHub release 类专用轮子的 **latest 版本动态解析** 目前为引导提示，自动联网取资产 URL 待补。
- custom_nodes（自定义节点）的安装与更新不在本启动器范围，交由 ComfyUI-Manager 或用户自行处理。

## 致谢

- [patientx](https://github.com/patientx) 的 comfyui-rocm 整合包与配套脚本，是本项目的功能蓝本。
- ComfyUI 上游（`comfy/cli_args.py`）提供启动参数定义。
- AMD ROCm、uv 与各国内镜像社区。
- 受到其他启动器的启发：[晓启动器](https://space.bilibili.com/141452636)、[KK启动器](https://space.bilibili.com/397341218)。
- 主体架构由我和Kimi K3进行评审完成，执行由Qwen 3.8 flash完成。

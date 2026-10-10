# ComfyUI ROCm Launcher — 项目长期备忘

- 目标：为 patientx comfyui-rocm 整合包（Windows + AMD ROCm）做图形化启动器，替代手写 bat。技术栈：C# / .NET 10 LTS + WinUI 3（MVVM, CommunityToolkit.Mvvm）。
- 权威需求文档：docs/SRS.md（v1.0，2026-10-06，已经两轮评审收敛）。上游参数快照：docs/cli_args_upstream.py；参考 bat：docs/功能参考脚本/。
- 用户当前阶段偏好：先做 GUI 界面原型（docs/ui-prototype.html），明确「只设计 GUI 不做编码」。正式编码前应与用户确认原型。
- 已产出数据资源（实现时直接复用）：
  - src/ComfyUiRocmLauncher/Assets/cli-args.json（110 参数，已与上游 1:1 核对，含 10 互斥组、bat 默认标记）
  - src/ComfyUiRocmLauncher/Assets/gpu-templates.json（附录 B 三模板）
- 解决方案结构（SRS v1.1 第 3.3 节，已落地）：根目录 ComfyUiRocmLauncher.sln（经典 sln）→ src/ComfyUiRocmLauncher.GUI（WinUI3 exe）、src/ComfyUiRocmLauncher.Core（net10.0 类库，零 UI 依赖）、test/ComfyUiRocmLauncher.Core.Test（xUnit，仅引用 Core）。依赖方向 GUI→Core←Core.Test，Core 禁止引用 Microsoft.UI.*。
- 关键约定（源自 SRS）：配置存 %APPDATA%\ComfyUI ROCm Launcher\config.json（原子写入，损坏备份重建）；日志环形缓冲 5000 行、保留 30 份/200MB；所有 pip 操作走 `uv pip ... --python <exe>`；robocopy 退出码 0–7 算成功；互斥组编号 1–10 见 cli-args.json。
- 构建环境：曾因工作负载集 10.0.303.1 半残导致所有 dotnet 命令报 MSB4242，已用 `dotnet workload config --update-mode manifests` 根治（不再需要 MSBuildEnableWorkloadResolver=false）。
- 启动方式：GUI 默认免打包（csproj 的 UseUnpackaged=true → WindowsPackageType=None + WindowsAppSDKSelfContained=true），`dotnet run --project src/ComfyUiRocmLauncher.GUI/ComfyUiRocmLauncher.GUI.csproj -p:Platform=x64` 即可运行，无需开启系统"开发人员模式"；发布 MSIX 用 `-p:UseUnpackaged=false`（该模式调试部署仍需开发人员模式）。
- **UI 设计语言（用户明确要求）**：全应用统一为「Windows 设置（Settings）」风格 —— 左导航 240px + 28px 半粗页头 + 分组小标题 + 圆角 8 卡片，行布局为「左标题+说明、右控件」，所有 ToggleSwitch 不显示 On/Off 文字。统一资源定义在 GUI/App.xaml（CardBorderStyle、SettingsSectionHeaderStyle、SecondaryTextStyle、CardDividerStyle、MonoFontFamily、SettingsPagePadding=36,4,36,24、SettingsPageMaxWidth=1180）。注意：本机 WinAppSDK 1.8 **没有**内置 SettingsCard/SettingsExpander，不要引用它们，用上述样式手写复刻。
- 注意：项目目录原在 OneDrive 下，并行写文件会偶发丢失，用户已关闭 OneDrive。验证新启动的进程时要留意 Bash 工具会回收子进程（用后台任务或直接跑 bin 下的 exe 验证）。
- 验证 UI 的实用手段：PowerShell + UIAutomationClient 脚本自动点击导航项/控件后检查进程是否存活，可替代人眼看截图（截图无法查看时尤其有用）。

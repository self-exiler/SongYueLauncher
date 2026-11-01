# 松月启动器（A ComfyUI Launcher for ROCm）— 软件需求规格说明书（SRS）

| 项目     | 内容                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                     |
| -------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| 文档版本 | v3.3                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                 |                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                     |
| 日期     | 2026-11-01                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                               |
| 变更说明 | v3.3：**任务输出抽屉体验收收尾（FR-5.1 / FR-9.6）**——① 抽屉左缘新增 8px 拖拽条（悬停显细条，光标 SizeWestEast），宽度以**占宿主宽度的比例**落盘（`taskDrawerWidthRatio` 默认 0.36，下限 320 DIP / 上限 85% 宿主宽，窄于 520 DIP 工具条转纵向），窗口缩放与下次启动按比例还原；实现注记：`Border` 已密封不可派生，拖拽条基类用 `ContentControl`（其默认模板不渲染 `Background`，命中面须由内部 `Grid Background="Transparent"` 承担，且内容对齐要显式 Stretch）。② **托盘图标改为常驻**：启动即注册、恢复窗口后不再移除，仅进程退出时注销；收起动作静默（去掉首次气泡提示与 `ShowBalloon` 链路），`TaskbarCreated` 重注册路径不变。真机验证（1920×1080 @125%）：拖拽/极值收敛/关闭重开/重启还原/窗口缩放五组通过；托盘图标在窗口可见、最小化收起、恢复三态均注册（`Shell_NotifyIconGetRect` hr=0），退出后注销。v3.2：**插件管理方案 C（FR-10.3~10.5 更新检测与批量操作）**——Core 新增 `PluginUpdateService`：`git ls-remote` 只问不下载、4 路并发、结果按路径缓存 5 分钟，`GIT_TERMINAL_PROMPT=0`/`GCM_INTERACTIVE=never` 禁掉会永久挂住 GUI 的凭据弹窗，`http.lowSpeedLimit/Time` 让死地址自裁；**检测与更新一律先剥掉 GitHub 镜像前缀**（真机实测 gh-proxy 不服务 info/refs，走镜像必卡到超时，直连毫秒级返回）；更新改为显式带地址 fetch 到临时 ref 再 `merge --ff-only`（本地改过/分叉时 git 自己拒绝），确认弹窗第二钮提供 `reset --hard` 强制覆盖；更新后按 requirements.txt 内容指纹决定是否自动重装依赖；克隆经镜像装完把 origin 还原为真实地址（否则后续检测更新全卡死）。PluginsPage 增加工具栏第二行（检测更新 / 批量更新 / 多选）、「可更新」徽章与单行更新按钮、多选复选框列与底部批量动作条（启用/禁用/卸载所选）。真机集成验证：9 插件 / 8 git 候选 / 0 失败并检出 1 个真实可更新（临时测试跑完已删）。**遗留**：工具栏第二行三个按钮用脚本合成点击不响应（同卡第一行「刷新」有效），Core 已验证、界面待你手点确认，详见交接文档 §2。v3.1：**插件管理方案 B（FR-10.2 安装）**——Core 新增 `PluginInstallService`：粘贴仓库地址即装（末段取目录名并净化，保证落点必在 custom_nodes 内）、clone 三级降级（浅克隆→完整→关 sslVerify，全程 GIT_LFS_SKIP_SMUDGE=1）、GitHub 镜像前缀只对 github.com 生效、有 requirements.txt 走 uv pip install -r（PyPI 镜像）否则跑 install.py、克隆后非插件结构只警告不拦、失败清残留并逐行输出原因；PluginsPage 增加「安装新插件」卡（地址框 + 自动装依赖开关）与定高 160px 任务输出区（自动滚动/清空/收起/取消），占用全局单任务槽。**真机跑出一个严重缺陷并修复**：插件在外置数据盘（E:）而备份目录在系统盘（C:）时 `Directory.Move` 抛 "must have identical roots"，且旧实现的 `Directory.Delete(recursive)` 遇到 git 只读对象会把源目录删成半损残骸——卸载改为容器目录 + 跨卷「先复制后删源」，新增 `PathHelpers.DeleteDirectoryTree`（解只读、遇 reparse point 只删链接本身不跟随），`TryDeleteDirectory` 一并改用它；散 `.py` 插件的备份改为容器目录承载（否则只枚举目录的备份列表会漏掉它）。新增 27 个单测（解析/净化/镜像/容器/只读删除），Core 共 208 个。v3.0：新增 **§4.9 插件管理页（FR-10，方案 A）**——导航第三项「插件管理」（拼图图标），Core 新增 `PluginService`：扫描 custom_nodes（口径与上游 `nodes.py load_custom_nodes` 逐条对齐：子目录 + 散 `.py`、跳过 `__pycache__` 与非 .py 散文件、`.disabled` 计禁用、同名遮蔽项不显示），插件根目录经 `ComfyDirResolver` 解析故 base-directory 外置后自动跟随；分支/commit/仓库地址直读 `.git` 内 HEAD、松散 ref（回落 packed-refs）与 config，不起子进程；启用/禁用=目录改名加减 `.disabled`（ComfyUI 官方约定，不写配置）；卸载=整体移入 `%APPDATA%\...\plugin_backup\时间戳_名称`，页面底部「卸载备份」可逐项恢复（同名冲突以 `_restoredN` 落回，绝不覆盖）；顶部计数 + 按名称/仓库地址搜索 + 刷新 + 打开插件目录，禁用项排末尾并打「已禁用」徽章，含 requirements.txt 打「含依赖清单」徽章；全部写操作仅 Idle 放行（改名/移动会撞运行中的文件锁），卸载与恢复前确认弹窗。配 8 个单元测试。方案 B（URL 安装 + 自动装依赖）与 C（ls-remote 更新检测 + 批量操作）按用户确认的顺序后续实施。另修正 FR-9.5 实现描述（早已改用 `OverlappedPresenter.PreferredMinimumWidth/Height`，非 WM_GETMINMAXINFO 子类化）。v2.9：三项调整——① **目录外置纯参数化**（FR-1.5 重写）：删除 `DirLinkService` 与 junction 检测/创建/还原/内容迁移全部链路（含 10 个单测），「目录外置（启动参数）」卡只维护 `base-directory` 参数（文本框 + 浏览 + 清除 + 生效回显），不做任何文件系统操作，数据目录解析交给上游 ComfyUI；§5.1 移除 `externalDirLinks` 示例（该字段从未实现）。② **启动恒为主页**（FR-9.3 改版）：不记忆上次页面，`lastPage` 字段作废（残留字段自动忽略）；实现注记：构造期给 NavigationView.SelectedItem 赋值不保证触发 SelectionChanged（ItemsRepeater 未就绪），启动导航显式调用 NavigateToTag。③ 标题栏曾试验左对齐「图标+应用名」自定义布局（对齐 Windows 设置），用户反馈不如原样式，**同日回退**为默认 TitleBar（居中图标+标题）——保持现状不再改。试验记录：TitleBar 自定义内容引用不存在的主题资源（如 `CaptionFontSize`，UWP 遗留键，WinUI 无此资源）会以 combase 异常静默崩溃（退出码 0x802b000a，无托管堆栈）。v2.8：目录外置链接与启动参数目录组联动（FR-1.5 / FR-2.1）——① 外置改为**单次操作**：「选择目录并外置…」挑一个外部数据根，models 与 output 一并迁移并建立 junction（目标 `<数据根>\<目录名>`），全部成功后自动**启用启动参数 `--base-directory`=`<数据根>`**（中途失败不留「参数已外置、目录没链」半态；覆盖旧值前在确认弹窗中警示）；「全部还原」解除两目录链接并搬回内容，每还原一个即检查 `--base-directory`——仍指向该数据根则清除，用户手动改过则保留；卡内新增参数状态行回显 `--base-directory` 当前取值。② 启动参数「目录」分组控件升级：新增 `folderPath` 类型（文本框 + 「浏览…」目录选择器，覆盖 base/output/temp/input/user/models-directory 六个目录参数）与 `filePathList` 类型（多行文件路径 + 「添加文件…」选择器，覆盖 `--extra-model-paths-config`；取代已无消费者的 `repeatableStringList`，ArgsBuilder 传参语义不变）。v2.7：安装体积缩减——发行形态改为「瘦身 Bundle（默认）+ 自包含 MSI（-Full 备选）」双轨——① 体积实测（R2R 均启用）：自包含发布产物 267MB（MSI 78MB）→ 框架依赖发布产物 60MB（MSI 高压缩 13.3MB、Bundle 14MB）；② 默认产物为 WiX v5 Burn Bundle（Bundle.wxs，Bundle UpgradeCode 与 MSI 分离，旧版自包含 MSI 经 MsiPackage 内部 MajorUpgrade 被替换升级，ARP 仅显示 Bundle 一条）：.NET Desktop Runtime 10 以「钉住版共享目录 FileSearch」检测（注册表键本机实测不可靠），缺失时安装期自动下载；Windows App Runtime 1.8 无可靠检测锚点（MSIX 不出注册表、WindowsApps 目录 ACL 拒绝枚举），始终幂等运行官方安装器（已装时秒退）；③ 运行时兜底链：检测漏网时，.NET 由 apphost 自带引导对话框、WASDK 由 Bootstrap 自动初始化的系统级引导对话框接手；④ build.ps1 构建期下载依赖到 installer/cache并计算 SHA1/大小注入 ExePackagePayload（v5 中 RemotePayload 已并入该元素；bal 命名空间为 schemas/v4/wxs/bal；wix 升级 5.0.2 并钉住同版本 util/bootstrapperApplications 扩展）；⑤ CI 同步升级 wix 5.0.2，Release 产物改为 exe+msi 双文件；⑥ MUI 语言目录裁剪目标扩展到 PublishDir（此前只裁 OutDir，-Full MSI 内残留 88 个语言目录）。v2.6：新增 **FR-1.5 目录外置链接**——主页快捷目录区新增「目录外置链接」卡：把 `<ComfyUI目录>\models` / `output` 建立为指向整合包外数据目录的 NTFS junction（`mklink /J`，无特权要求），现有内容先迁往外部（同盘整目录改名瞬时完成；跨盘 `robocopy /E /MOVE /R:1 /W:1`，被占用文件快速跳过），数据从此存放在整合包之外，删除/重装整合包不会带走模型与输出；支持还原（删联接、内容搬回普通目录）；状态以文件系统为真相（已外置/未链接/目录不存在/链接失效）；操作占用全局任务槽（BusyEnv 期间禁启动），运行中/任务中禁入，操作前确认弹窗说明迁移语义；config.json 新增 `externalDirLinks` 字段（§5.1）；Core 新增 `DirLinkService` 并配 10 个单元测试。v2.5：AI 网关首次安装改为浏览器手动下载（FR-5.6）——内置下载在慢镜像下必然撞 5 分钟总超时，且超时与用户取消不可区分，实测表现为「进度窗无声关闭、再次点击从头重新下载」；本版删除全部内置下载代码（多通道重试/超时/进度上报一并移除），改为引导弹窗自动把源码包下载链接（配置镜像优先，其次清单镜像与 GitHub 直连）弹到默认浏览器，文字引导用户把 zip 放入安装目录（弹窗内可一键打开目录），点「我已放置好，继续」后启动器本地解压安装（zip slip 防护、校验 cli 入口、成功后清理 zip；也接受用户自行解压后放入仓库文件夹）。v2.4：轮子清单改为外部文件优先（FR-8.6）——wheel-catalog.json 除编译期内嵌副本外，同时以 Content 形式复制到 exe 同目录；加载顺序为「外部 wheel-catalog.json → 内嵌资源」，外部文件缺失或解析失败时回落内嵌并在环境与更新页提示原因，替换该文件即可更新清单而无需重新发版；「作者专用轮子」卡的版本行改显清单来源（外部文件 / 程序内置 / 回落警告）。v2.3：专用轮子清单 v3，以整合包 install.bat 为权威来源对齐（FR-8.6）——① bitsandbytes 权威源为 0xDELUXA/bitsandbytes_win_rocm（latest 即作者钉的 0.50.2.dev0，win_amd64_all 通用轮，install.bat 对所有支持架构无条件安装），替换 v2.2 误引的 sunxfancy 仓库并撤销架构白名单；② FlashAttention 回归：权威源 0xDELUXA/flash-attention，钉死 tag v2.8.4_win-rocm（该仓库 latest 是 v3.0.0 且无 aiter，作者未采用；flash_attn-2.8.4 为 py3 通用纯轮，走 aiter triton 后端），可选；③ amd_aiter 回归：同一 release 内的 Windows 轮 amd_aiter-0.0.0，是 flash-attn 的后端依赖，可选；④ WheelArchRule 新增 Tag 字段（钉死 release 走 /releases/tags/{tag} 解析），WheelService 相应支持；⑤ v2.2 的 SelectReleaseAsset 无回退、404 降级跳过、无命中架构提示等加固全部保留。v2.2：专用轮子清单 v2 与 release 解析加固（FR-8.6，修复「安装专用轮子」404 报错）——① 清单中 0xDELUXE/bitsandbytes 与 patientx/flash-attention-rocm 已被上游删除（releases/latest 返回 404）：bitsandbytes 换为现役 sunxfancy/bitsandbytes-0.49.2.rocm72（py3-none-win_amd64 通用轮，ROCm 7.2 构建），且仅对 bitsandbytes ROCm 官方支持架构（gfx90a/942/1100/1101/1102/1200/1201）启用——RDNA1/2 装上也无法用，导入失败还会拖垮无条件 import 它的节点；FlashAttention 全 GitHub 无 Windows ROCm 轮（gfx1201 仓库无 release、gfx1200 仓库只发 Linux 轮），条目移除待上游恢复后随发版加回；ROCm/aiter 仅发 manylinux 轮，条目移除。② WheelService.SelectReleaseAsset 行为修正：指定 assetMatch 未命中时返回 null 而不是退回 release 中任意 whl（防止把 Linux 轮丢给 uv）；③ 404 降级为「[跳过] 上游仓库无可用 release（可能已下架）」而非原始 HTTP 异常文本；清单条目在当前架构无任何规则命中时输出「[跳过] 当前架构（xxx）无可用构建」。v2.1：UI 与 Windows 11 设置风格统一（外观重构，功能契约不变）——① 引入 `CommunityToolkit.WinUI.Controls.SettingsControls` 8.2：主页/环境变量页的行卡迁移到 **SettingsCard**（Header / Description / HeaderIcon 三段式，主页关键行带 Fluent 图标列），「通用 PyPI 包」二级页入口卡改 `IsClickEnabled` 整卡可点（自带悬停/按压视觉与右侧 chevron，取代「透明按钮套卡片」自绘）；② 启动参数页分组外壳换 **SettingsExpander**（自带展开动画 / chevron / 悬停），行改「平底行 + 行间分隔线」避免卡中卡，行构建由「首次展开懒建」改为页面载入后逐组让出 UI 线程的渐进构建（SettingsExpander 无 Expanding 事件，展开后再建会闪空内容）；搜索过滤时行与分隔线可见性成对同步；③ 度量令牌统一：新增 `SettingsCardSpacing`（同组相邻卡间距 2，Windows 设置近贴排布）与 `SettingsHeaderPadding`，输入控件宽度统一 `SettingsFieldMinWidth/MaxWidth`（320–480，含搜索框/仓库地址/包名输入），状态徽章圆角 8→12（全圆 pill），AI 分组徽章字号 11→12，空闲状态点改 `TextFillColorSecondaryBrush`（disabled 刷子语义是「控件禁用」），导航「启动参数」图标由 Setting 齿轮改 CommandPrompt（避免与系统设置语义混淆）；④ **新增 FR-9.5 窗口最小尺寸**（主窗口 640×480 / AI 分析窗口 760×560，Win32 `WM_GETMINMAXINFO` 子类化）。实现注记：SettingsCard 的图标属性是 `HeaderIcon`（写成 `Icon` 会触发 WinUI 1.8 XamlCompiler Pass1 静默崩溃 exit 1 无诊断，与此前 Labs MarkdownTextBlock 的 `TextWrapping` 同类坑）；SettingsExpander 只有 `Expanded/Collapsed` 事件且不可在代码中设置 ItemsPanel（WinUI 无 `FrameworkElementFactory`）。v2.0：AI 智能分析去 opencode 化（网关化）+ 错误日志三类化分组 + 联网查证客户端自实现（方案依据 `docs/变更方案-AI诊断网关化与错误日志展示优化.md`）——① **FR-5.6** 入口检测从「定位 opencode CLI」改为「Our Free Model 本地网关就绪检查」：不安装 dsh，首次使用时引导下载上游 tag 源码 zip（钉 v2.5.9，独立服务运行时零依赖）解压至 `<安装目录>\tools\our-free-model\`，经本机 Node.js（^22.19/≥24，新增 `ToolLocator.FindNode`）拉起 `packages/standalone/cli.mjs`（端口取 config 新增字段 `aiGatewayPort` 默认 18900，数据目录 `%APPDATA%\ComfyUI ROCm Launcher\our-free-model`），`/health` 就绪后自动读取网关 settings.json 的 forwardKey 回填 config 新增字段 `aiGatewayApiKey`；缺 Node 引导 `winget install OpenJS.NodeJS.LTS`；下载适用 GitHub 镜像前缀；启动器退出回收服务进程。② **FR-5.7** 错误材料三类化（解释器 / error / warning，显式 WARNING 前缀优先于 ERROR 子串）并按优先级装填字节预算。③ **FR-5.11（新增）** 指纹归并分组视图替代可编辑预览框。④ **FR-5.8** 模型列举改 `GET /v1/models`。⑤ **FR-5.9** 分析请求改 `POST /v1/chat/completions`（SSE 流式），删除临时 agent 配置与 NDJSON 解析。⑥ **FR-5.12（新增）** 联网查证恢复为客户端 function calling 工具循环（webfetch + 域特化 websearch，轮次上限 2，上游 400 自动降级）。⑦ §6 外部接口 `opencode` 行替换为 Our Free Model 本地网关。AiDiagnostics 库重写为 OpenAI 兼容 HTTP 客户端（`AnsiStripper`/`ProcessRunner`/`CommandLine` 保留供 Core 复用）。v1.20：「安装项目依赖」扩为三清单（FR-8.6）——依序安装 requirements.txt、manager_requirements.txt、agent_requirements.txt（缺失的可选清单跳过，全部过滤 torch 系、各自派生 `<源名>_filtered.txt` 过滤件，兼容 updater.bat 命名）。v1.19：初次部署链路入口澄清（FR-8.6）——「作者专用轮子与项目依赖」混合卡拆为「项目通用依赖（requirements.txt）」与「作者专用轮子」两张卡，按 install.bat 部署顺序排列（venv → torch → **安装项目依赖** → 专用轮子）；按钮「同步项目依赖」改名「安装项目依赖」（用户反馈初次装完专用轮子后找不到装通用 whl 包的入口），卡内说明写明「装完 torch 后执行本步」。v1.18：环境与更新页两处调整——① **任务输出区定高 200px + 垂直滚动条**（FR-8.1：原行高 Auto 随输出无限撑高，滚动条形同虚设）；② **通用 PyPI 包改为页内二级页**（FR-8.7）：主页面只留 drill-in 入口卡，列表与安装/卸载移入二级页（页顶面包屑「环境与更新 › 通用 PyPI 包」，点父级段返回，Windows 11 设置风格），任务控制台跨页保持可见，离开页面自动退回主视图。v1.17：GUI 精简——① **移除「启动后自动打开浏览器」开关**（FR-4.1/FR-4.3）：启动器就绪后不再代开浏览器，自动打开只由 ComfyUI `--auto-launch` 参数决定（启动参数页唯一入口），主页「打开浏览器」手动按钮保留；`autoOpenBrowser` 配置字段作废（旧 config.json 中残留字段反序列化自动忽略）；② **模板行去掉选中摘要注解**（FR-7.1 不再展示「N 个环境变量 / M 个启动参数」，只在应用/另存/删除后显示一行操作反馈）；③ **移除「复制脚本内容」按钮**（FR-7.6 只保留导出 .ps1 文件）。v1.16：GUI 布局优化——① **撤销配置管理页**（ConfigPage 删除）：GPU 架构模板（FR-7.1~7.5）与导出启动脚本两按钮（FR-7.6）并入主页「启动控制」卡片，导航变为 主页→环境与更新→启动参数→环境变量→日志（旧配置里 LastPage=ConfigPage 自动回退主页）；② **页头右上角移除版本文字**，仅留运行状态徽章（版本显示仍见主页状态卡片，FR-1.3 不变）；③ **AI 分析窗口**内容与窗口边缘补内边距（间隙打在根 Grid Padding 上——UserControl 的 Padding 不经 WinUI 默认模板生效）。v1.15：第三轮代码审查整改——① **停止序列**：核对官方路由文档与上游 `server.py` 后确认 ComfyUI 没有 `/shutdown` 端点，删除优雅停止请求与等待窗口，停止即强杀进程树（最多等 3 秒收尾）；② **版本显示**：git 检出每次读取本地 HEAD（不再只在 hash 文件缺失时），外部 `git pull` 后不会显示过期版本；并发刷新共用一次查询，git 缺失/失败降级并只记一次警告；③ **FR-8.9**：user 目录解析补上 `--base-directory`（优先级 `--user-directory` → `--base-directory/user` → `<ComfyUI目录>/user`）；④ **AI 分析**：权限矩阵把 `read` 改为 `deny`（日志是不可信文本，防止提示注入读取本机文件），联网次数统一为最多 1 次，行为规则只保留在系统提示词，短指令移入 `ComfyUiDiagnosis`；`RunEventParser` 丢弃截断的 JSON 行，避免污染回答正文；AI 窗口随主窗口关闭；⑤ **检查更新**结果不再被 `RefreshStatus` 覆盖；⑥ **GPU 映射**：RX 5700/5600 XT → gfx1010、RX 5500/5300 → gfx1012、RX 500 系（Polaris）→ gfx803 且标记不可用、RX Vega → gfx900（原误 gfx908）。v1.14：三处缺陷整改 + AI 弹窗按原型 v2 重构——① **FR-1.3 版本显示**补全检测链：`.last_update_hash` 缺失时读 `comfyui_version.py` 的 `__version__`（显示 `v<版本>`），git 检出目录后台补查 `git rev-parse HEAD` 并回填 hash 文件（自愈）；② **FR-4.2 停止序列重写**：废弃 AttachConsole + CTRL_BREAK（实测信号异步派发晚于保护窗口撤销，会把启动器自身打死，日志停在「发送 CTRL_BREAK」即为征兆），改为 `POST <服务地址>/shutdown` 优雅停止、5 秒未退出转进程树强杀、请求未生效直接强杀；`ProcessService` 删除全部 console P/Invoke；③ **AI 智能分析弹窗**按 `docs/prototypes/ai-analysis-dialog-v2.html` 重构为自包含 UserControl（顶部模型栏 / 左日志右回答两栏 / 竖排按钮列 / 底部状态栏圆点），移除「原文」切换，进度事件只进底部状态栏（回答区只放正文），宿主改为独立窗口 `AiAnalysisWindow`（标题栏 X 关闭，取代底部「关闭」按钮行）。v1.13：新增 **FR-8.9 ComfyUI-Manager 数据通道镜像接管**——Core 新增 `ComfyManagerChannelService`，在每次启动前把 `channels.list` / `config.ini` 里指向 `raw.githubusercontent.com/ltdrdata/ComfyUI-Manager` 的通道改写为所选 GitHub 镜像前缀、直连时还原（幂等、首次改写留 `.launcher.bak`、`config.ini` 不新建、失败只告警不阻断）；三分法作用域相应扩为「git clone + GitHub release 轮子 + Manager 数据通道」，界面镜像说明同步文案。依据 `docs/评估-ComfyUI-Manager通道镜像接管.md`（采纳方案 A，未采纳 §6.1 里「凭 folder_paths.py 推断目录并预创建」的做法——只在已存在的 Manager 目录内改写）。v1.12：AI 智能诊断链路拆分为可复用类库——新增 `src/AiDiagnostics`（net10.0、零 UI：opencode 定位/调用、agent 配置运行时生成、NDJSON 解析、进程执行、日志扫描、提示词模板）与 `test/AiDiagnostics.Test`；Core 新增 `Ai/` 领域画像（`ComfyUiDiagnosis`）与门面 `ComfyUiAiService`，日志扫描与命令行引用改为委托，删除随程序发布的 agent 资产与 `build.ps1` 对应断言。依赖方向变为 `GUI → Core → AiDiagnostics`，库内不得出现业务域文案。（实现期精简：`AiDiagnosticsOptions`、`AiPermissions` 与 `AiRequest.TimeoutMs` 等从未被赋过非默认值的可配置面已收成库内常量，`AiAgentProfile` 只留 `Id`/`SystemPrompt`/`Description`。）v1.11：模型列举（FR-5.8）改回默认常驻后台服务并加空列表重试——`--standalone` 为单次查询新建私有服务，provider 目录在插件异步装载完成前不可见，冷启动时 `model.list` 恒为空（GUI 界面模型来自已装载完成的常驻服务，不受影响）；`run`（FR-5.9）保留 `--standalone`（其启动流程会等待 provider 就绪，实测请求可正常到达上游）。v1.10：修复日志页 AI 智能诊断的 OpenCode v2 集成——诊断与模型列举均使用独立服务（`--standalone`），通过 `OPENCODE_CONFIG` 显式指向临时 agent 配置并校正 `PWD`，避免复用后台服务/父进程环境导致自定义 agent 丢失；子进程 stdin 写入、等待与输出冲刷纳入同一超时/取消语义，防止 GUI 长时间无响应。v1.8：代码审查整改（依据《SongYueLauncher 代码审查报告》，commit 8dd90cd）——**删除 MSIX 打包路径**（`Package.appxmanifest`、8 张磁贴/商店图、`Microsoft.Windows.SDK.BuildTools.WinApp` 包、Package launch profile 与 csproj 相关片段），只保留「免打包自包含 + MSI」一条链路；删除重构残留死代码（`bat 默认`徽章与周边字段、无 UI 的 `CustomWheels`、只写不读的 JSON 版本字段、无数据可达的分支等）；收敛重复实现（ANSI 色刷、内嵌 JSON 加载器、命令行引用、工具路径解析、状态机、日志行数上限等）。运行行为与功能契约不变，除「应用 bat 默认参数」与「自定义轮子」两项功能按决策正式移除（GUI 早已无入口）。v1.7：配置管理页移至导航第三（主页→环境与更新→配置管理→启动参数→环境变量→日志）；启动参数页移除「bat 默认参数」「恢复默认（全不启用）」两个按钮（保留搜索 / 命令预览 / 结构化参数）；工具链状态行改为版本+路径分行并省略号裁剪，避免长路径溢出。v1.6：复审修正——torch 更新改为「先下载验证后卸载 + 尽力回滚」（AMD nightly 旧版本可能下架，快照语义降级）；ComfyUI 项目更新按目录形态双策略（git 检出→pull --ff-only，非检出→clone+robocopy）；导航顺序再调整为 主页→环境与更新→启动参数→环境变量→配置管理→日志；wheel-catalog 仅随软件发版更新；修复 FR-8.5「刷新主页版本显示」错误。v1.5：新增 §4.7 环境与更新页（FR-8），重建 v1.3 移除的环境安装/更新能力：Python 环境（uv 建 .venv，钉死 3.12）、镜像三分法、工具链检查（winget + UAC）、核心仓库安装/更新（仓库地址用户指定）、torch nightly 快照回滚、专用轮子内置清单 + 自定义、项目依赖同步、通用 PyPI 包管理；恢复 BusyEnv 状态与全局单任务互斥；作废「启动器不做网络访问」约束（NFR-4 修订）。v1.4：新增 §4.6 配置管理页（FR-7）集中 GPU 模板选择/应用（全量替换语义）/另存与删除自定义模板；ArgsPage 与 EnvVarsPage 移除模板入口，config.json 增加`customTemplates`。v1.3：移除 FR-6 环境管理（ROCm/PyTorch 更新、依赖安装页）与 FR-4.1 启动前追踪包同步，删除 BusyEnv 状态与 UpdateService/RequirementsFilter/GpuInfoService.DetectArch，功能待重建（v1.5 已重建）。v1.2：移除 FR-6.3 程序更新（上游 patientx-cfz/comfyui-rocm 仓库已删除，整合包不再 git 自更新）。v1.1：新增 3.3 解决方案结构（GUI / Core / Core.Test 三项目划分与依赖约束）；参数清单与 GPU 模板定为 Core 内嵌资源（EmbeddedResource） |
| 状态     | 已评审（v1.5 经 grill-me 三轮需求评审收敛，决策见`docs/需求变更记录.md`）                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                              |
| 应用名称 | ComfyUI ROCm Launcher（暂定）                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                            |
| 技术栈   | C# / .NET 10 LTS + WinUI 3（Windows App SDK）                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                            |
| 参考依据 | `docs/功能参考脚本/`（comfyui-rocm.bat、install.bat、comfyui-rocm-updater.bat、rocm-pytorch-package-updater.bat）、ComfyUI 上游 `comfy/cli_args.py`（`docs/cli_args_upstream.py`）                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                 |

---

## 1. 引言

### 1.1 目的

为 patientx 的 **comfyui-rocm 整合包**（Windows + AMD ROCm）提供一个图形化启动器，替代手写批处理的启动、参数配置、日志查看与环境维护工作。

### 1.2 范围

- **包含**：启动/停止 ComfyUI、启动参数配置、环境变量配置、GPU 架构模板与自定义模板管理（主页启动控制区）、实时日志、环境与更新页（工具链检查与安装、Python 虚拟环境创建、ComfyUI 项目安装与更新、torch nightly / 专用轮子 / 项目依赖 / 通用 PyPI 包管理）。
- **不包含**：NVIDIA/DirectML/CPU 等其他后端的支持、custom_nodes 的安装与更新管理（由 ComfyUI-Manager 或用户自行处理）、第三方模型/权重的下载管理。

### 1.3 定义

| 术语   | 含义                                                                                                                                    |
| ------ | --------------------------------------------------------------------------------------------------------------------------------------- |
| 整合包 | 用户指定仓库（或本地已有目录）的 ComfyUI 目录内容                                                                                       |
| venv   | 启动器用 uv 创建的 Python 虚拟环境，位于`<ComfyUI目录>\.venv`，Python 版本钉死 3.12                                                   |
| 模板   | 按 GPU 代际预设的一组环境变量 + 启动参数                                                                                                |
| 三分法 | 镜像作用域硬规则：GitHub 镜像应用于 git clone、GitHub release 轮子下载与 ComfyUI-Manager 数据通道（FR-8.9）；PyPI 镜像仅作用于通用 PyPI 包与项目依赖；AMD 官方源永远直连 |

---

## 2. 总体描述

### 2.1 产品定位

单用户、单机 Windows 桌面工具。启动器 exe 可位于任意位置，与 ComfyUI 目录解耦（全部使用绝对路径配置）。

### 2.2 运行环境

- Windows 10 1809+ / Windows 11，x64
- 整合包既可以是用户已有的本地目录（含 `main.py`、`requirements.txt`），也可由环境与更新页从用户指定仓库克隆安装
- venv 由环境与更新页用 uv 创建（Python 3.12）；也可识别用户已有的 `<ComfyUI目录>\.venv`
- 外部可执行依赖：`git.exe`、`uv.exe`（均可经环境与更新页用 winget 安装）、`winget.exe`（可选，缺失时降级为手动引导）、venv 内的 `rocm-sdk`

### 2.3 约束

- 配置文件存 `%APPDATA%\ComfyUI ROCm Launcher\config.json`；日志存 `%APPDATA%\ComfyUI ROCm Launcher\logs\`。
- 仅支持 ROCm；所有 ROCm 特有步骤（rocm-sdk init、HIP_PATH 推导、MIOpen 变量）默认启用，不做平台开关。
- venv 的 Python 版本钉死 3.12（cp312 专用轮子硬约束），不提供版本选择。
- 单实例运行。

### 2.4 全局状态机

```
Idle ──启动──▶ Starting ──就绪/超时──▶ Running ──停止──▶ Stopping ──退出──▶ Idle
Idle ──环境任务──▶ BusyEnv ──完成/取消/失败──▶ Idle
```

- `Running / Starting / Stopping` 期间：禁用启动按钮（仅可停止），并禁用环境与更新页全部操作。
- `BusyEnv` 期间：禁用「启动 ComfyUI」与主页启动控制区的模板应用/另存；同一时间仅允许一个安装/更新任务（不排队、不并行）。
- 应用退出时若 ComfyUI 仍在运行，提示并执行停止流程；退出时若有环境任务运行，提示并终止子进程。

---

## 3. 系统架构

### 3.1 分层

```
Views (WinUI 3 XAML)
 ├─ HomePage          主页
 ├─ EnvUpdatePage     环境与更新（工具链检查 / ComfyUI 项目 / Python 轮子 三标签）
 ├─ ArgsPage          启动参数
 ├─ EnvVarsPage       环境变量（可并入 ArgsPage 为两个 Tab，实现时定）
 └─ OutputDrawer      任务输出抽屉（全局 UserControl，覆盖式右滑，非 Page）
ViewModels (MVVM, CommunityToolkit.Mvvm)
Services
 ├─ ConfigService      config.json 读写、默认值、迁移
 ├─ ProcessService     启动/停止/进程树管理、输出重定向
 ├─ LaunchEnvBuilder   固定注入 + rocm-sdk 推导 + 模板/自定义变量合成
 ├─ ArgsBuilder        结构化参数 + 额外参数 → 命令行
 ├─ LogService         内存环形缓冲(5000行) + ANSI 解析 + 落盘 + 磁盘保护
 ├─ TemplateCatalog    内置 + 自定 GPU 模板解析、应用（全量替换）、捕获
 ├─ GpuInfoService     WMI 读取显卡型号 + 名称→arch 内置映射（不依赖项目文件/venv）
 ├─ ToolchainService   git / uv / winget 检测；winget 安装（UAC 提权子进程）；uv 用户级安装
 ├─ RepoService        核心仓库 ls-remote 检查 / shallow clone / robocopy 热更新（GitHub 镜像适用）
  ├─ VenvService        uv venv --python 3.12 创建 + 构建工具链补装
 ├─ WheelService       torch nightly 更新（快照/回滚）、专用轮子（内置清单+自定义）、依赖同步、通用包（PyPI 镜像适用）
 ├─ MirrorPolicy       三分法实施：按操作类型决定镜像前缀 / 直连
 ├─ ComfyManagerChannelService  ComfyUI-Manager 数据通道接管：改写 channels.list / config.ini 的 raw 通道（FR-8.9）
 ├─ ComfyDirResolver   数据目录生效路径解析：*-directory 覆盖 → base-directory 公共根 → ComfyUI 目录（FR-1.4 / FR-1.5 / FR-10 共用）
 ├─ PluginService      插件管理：custom_nodes 扫描（口径对齐上游加载器）、.git 元数据直读、.disabled 改名开关、卸载备份与恢复（跨卷安全）（FR-10）
 ├─ PluginInstallService 插件安装：仓库地址净化、GitHub 镜像、clone 三级降级（跳 LFS）、requirements.txt / install.py 自动装依赖（FR-10.2）
 ├─ PluginUpdateService  插件更新：ls-remote 并发检测（剥镜像、禁凭据弹窗、低速超时、TTL 缓存）+ ff-only / 强制覆盖两种更新 + 依赖变更重装（FR-10.3~10.5)
 └─ TaskRunner         长任务调度（单任务互斥、取消、进度、输出流复用日志设施）
```

### 3.2 进程模型

- ComfyUI 以子进程启动：`FileName=<python.exe>`，`Arguments=main.py <args>`，`WorkingDirectory=<ComfyUI目录>`，`UseShellExecute=false`，重定向 stdout/stderr，`CreateNoWindow=true`。停止时强杀整个进程树（FR-4.2）——上游 ComfyUI 未提供 `/shutdown` 端点，也不使用控制台信号：启动器与子进程不共享控制台，AttachConsole + CTRL_BREAK 会把信号广播给启动器自己。

### 3.3 解决方案结构

```
ComfyUiRocmLauncher.sln（根目录，经典 sln 格式）
├─ src/ComfyUiRocmLauncher.GUI         WinUI 3 主程序
│    net10.0-windows10.0.26100.0，UseWinUI，免打包自包含单形态（不启用 MSIX 打包）
│    Views（五页 XAML）/ ViewModels / App.xaml / 导航 / 文件与目录选择器等 WinRT 互操作
├─ src/ComfyUiRocmLauncher.Core        服务层类库
│    net10.0，3.1 节全部服务 + 数据模型 + 状态机
│    Ai/（ComfyUI 领域画像：诊断 agent 人设、日志噪声词、提问模板，约 60 行）
│    Resources/cli-args.json、Resources/gpu-templates.json、Resources/wheel-catalog.json、Resources/gpu-arch-map.json 以 EmbeddedResource 内置
├─ src/AiDiagnostics                   AI 诊断通用类库（可独立复用到其他 .NET 宿主）
│    net10.0，零 UI 依赖：OpenAI 兼容网关客户端（SSE 流式 + function calling 工具循环）、错误日志三类扫描与指纹归并、提示词模板；AnsiStripper/ProcessRunner/CommandLine 供 Core 复用
├─ test/ComfyUiRocmLauncher.Core.Test  xUnit 测试项目（net10.0），仅引用 Core
└─ test/AiDiagnostics.Test             xUnit 测试项目（net10.0），仅引用 AiDiagnostics
```

**约束（NFR-5 的编译级保证）**

- 依赖方向固定为 `GUI → Core → AiDiagnostics`，测试工程各自只引用被测库；不允许反向或跨层引用。
- `AiDiagnostics` 不得引用 Core / GUI，也不得引用任何 `Microsoft.UI.*` / `Microsoft.Windows.App.*` 类型；业务知识（agent 人设、日志关键词、提示词模板）只能由宿主以参数注入，库内不出现 ComfyUI 字样。
- Core 不得引用任何 `Microsoft.UI.*` / `Microsoft.Windows.App.*` 类型；服务与 UI 之间一律通过 `event` / `Action<T>` 回调通信，日志与进度由 GUI 侧切回 UI 线程渲染。
- 不引入独立的 Packaging 项目，也不启用 MSIX 打包工具链；ViewModels 不独立成程序集。
- 参数清单与 GPU 模板统一放在 Core 的内嵌资源中，GUI 与单元测试共用同一份数据；发版时对照上游 `cli_args.py` 更新（参见第 8 节风险表）。
- Core 与 AiDiagnostics 的目标框架均保持 `net10.0`（不带 WinUI），以保证单元测试无需 WindowsAppSDK 即可在 CI 运行。

**开发期启动方式（v1.1 补充）**

- GUI 项目固定按**免打包（unpackaged）**方式构建与启动：工程内直接设置 `WindowsPackageType=None`、`WindowsAppSDKSelfContained=true`，运行时随程序输出，**无需开启系统"开发人员模式"**。开发命令：`dotnet run --project src/ComfyUiRocmLauncher.GUI/ComfyUiRocmLauncher.GUI.csproj`（工程只保留 x64 平台）。
- **发行形态为瘦身 Bundle（默认）+ 内嵌 MSI 双产物**（WiX v5，`installer/build.ps1` + `Bundle.wxs`/`Product.wxs`，详见 v2.7 变更记录）：框架依赖发布产物经 MSI 高压缩内嵌，`.NET Desktop Runtime` 与 `Windows App Runtime` 安装期按需下载；`-Full` 可出旧版自包含 MSI。
- 免打包发布需显式把 XAML 资源索引 `$(AssemblyName).pri` 补进 publish 清单（工程内 `IncludeUnpackagedPriInPublish` 目标），缺它应用启动即静默退出。
- 应用图标 `Assets/AppIcon.ico` 必须随程序发布（csproj 的 `Content` 设 `CopyToOutputDirectory` + `CopyToPublishDirectory`）：MSI 收割后与 exe 同级，供 `AppWindow.SetIcon`（标题栏 / 任务栏 / Alt-Tab）与 `TitleBar.IconSource`（相对 URI）取用；`build.ps1` 已把该文件与 `coreclr.dll`、`.pri` 并列为发布产物必需项，缺失即构建失败。exe 内嵌图标由 `<ApplicationIcon>` 承担（资源管理器里的文件图标）。
- MSI 的安装包图标（资源管理器与「应用和功能」）由 `installer/Product.wxs` 的 `Property Id="ARPPRODUCTICON"` 声明，且 `Icon/@Id` 以 `.exe` 结尾——只有 `.exe`/`.dll` 结尾的 Id 才会按 32×32 渲染（`.ico` 结尾系统只取 16×16）。注意 WiX v4 下 `WixVariable Id="ARPPRODUCTICON"` 是空操作，写它不会产生该属性。
- **MSIX 路径已移除（v1.8）**：未签名 MSIX 需要目标机开启开发人员模式或预装证书，实际从未维护（manifest 仍是 `CN=AppPublisher` 占位），故连同 `Package.appxmanifest`、8 张 MSIX 磁贴/商店图与 `Microsoft.Windows.SDK.BuildTools.WinApp` 包一并删除，只保留免打包 + MSI 一条链路。

---

## 4. 功能需求

### 4.1 主页（HomePage）

**FR-1.1 路径设置**

- ComfyUI 目录：文本框 + 浏览按钮。校验：目录存在且含 `main.py`。
- Python 解释器：文本框 + 浏览按钮。默认值：`<ComfyUI目录>\.venv\Scripts\python.exe`（ComfyUI 目录变更时若用户未手动改过则联动刷新）。校验：文件存在；点击"验证"执行 `<python> --version` 显示版本。
- uv.exe：自动从 PATH 发现并在主页展示可用性；未找到时给出安装引导（不内置下载）。

**FR-1.2 操作按钮**：启动、停止、打开浏览器（按状态机启停）。

**FR-1.3 状态显示**

- 运行状态指示（停止/启动中/运行中/停止中）。
- 当前版本：目录是 git 检出时后台执行 `git rev-parse HEAD` 读取真实 HEAD 并回填 `.last_update_hash`（每次打开/刷新都读，外部 `git pull` 后不会显示过期版本）；非 git 检出优先读 `.last_update_hash` 前 10 字符，缺失时读 `comfyui_version.py` 的 `__version__`（显示为 `v<版本号>`）；皆无显示 `unknown`。并发刷新共用一次查询；git 缺失或读取失败降级为现有 hash 文件，并只记一次警告。
- GPU 型号：WMI `Win32_VideoController` 中 AMD/ATI 适配器的 Name；读取失败显示"未知"。

**FR-1.4 快捷目录**（资源管理器打开，目录不存在时按钮禁用；按钮 ToolTip 回显解析后的生效路径）
`output`、`models`、`user\workflow`、`custom_nodes`、`input`

- 打开目标经 `ComfyDirResolver` 按**生效路径**解析（与上游 `folder_paths.py` 取值规则一致）：`*-directory` 单项参数给出目录本身（优先级最高，相对值按 ComfyUI 目录展开）→ `base-directory` 作为公共根拼接 → 都未启用时落在 ComfyUI 目录下。启用目录外置后，五个按钮自动打开外置数据根下的对应目录。

**FR-1.5 目录外置（纯启动参数）**（快捷目录区，「目录外置（启动参数）」卡）

- 本卡**只做一件事**：维护结构化启动参数 `base-directory`。**不检测、不创建、不还原目录联接，不移动/复制任何文件**（v2.9 移除 `DirLinkService` 与 junction 全部链路）。
- 控件：路径文本框（可手输）+「浏览…」（FolderPicker 回填）+「清除」（禁用并删值）。取值即时写入 `args.structured` 并落盘，与启动参数页「目录」分组中 `--base-directory`（folderPath 型）同源双向。
- 语义回显：未启用时提示「ComfyUI 使用整合包内目录」；启用时提示「模型/输入/输出/临时目录按 <取值> 解析」。相对路径注明按 ComfyUI 目录解析；取值位于整合包内时给出「删除整合包数据仍会丢失」警告；路径非法时提示可能导致启动失败。
- 数据外置的实际效果由上游 ComfyUI 负责：`--base-directory` 使其按该根解析 `models` / `input` / `output` / `temp` 等子目录，数据存放在整合包之外，删除或重装整合包不丢数据。

### 4.2 启动参数页（ArgsPage）

**FR-2.1** 以分组结构化控件枚举上游 `main.py` 全部参数（完整清单见**附录 A**）。控件形态：

- flag 型 → ToggleSwitch
- 带值型 → TextBox / NumberBox / 下拉框（枚举型如下拉：`--preview-method`、`--default-hashing-function`、`--fast` 多选、`--verbose` 级别）
- 目录路径型（`folderPath`，「目录」组六个目录参数）→ TextBox + 「浏览…」目录选择器（路径仍可手输；开关未启用时整行控件禁用）
- 文件路径清单型（`filePathList`，`--extra-model-paths-config`）→ 多行 TextBox（每行一条）+ 「添加文件…」选择器（yaml/yml，按行追加、已存在不重复）
- 互斥组（附录 A 中标注）→ 同组开启一个自动关闭其余，或组内 RadioButton

**FR-2.2** 每组可折叠；页面顶部提供搜索框按参数名过滤。**手风琴语义**：展开某个分组时自动收起其他分组，任何时刻最多一个分组展开（避免页面过长）；搜索时例外——命中的分组可同时展开，以便横向比较。

**FR-2.3 额外参数**：单行自由文本，原样拼接在结构化参数之后（放最后，允许覆盖前者）。

**FR-2.4 命令预览**：只读文本实时显示最终命令行 `<python.exe> main.py <args>`。

**FR-2.5** 参数随配置持久化（仅存"非默认"项）；页面提供按参数名搜索与命令实时预览。GPU 模板的批量参数应用见 §4.6（FR-7，主页启动控制区）。

### 4.3 环境变量页（EnvVarsPage）

两层结构（固定注入层 + 自定义层），自上而下合成最终环境；GPU 模板见 §4.6（FR-7，主页启动控制区），应用后写入自定义层。

**FR-3.1 固定注入层**（展示为只读列表，不可删除/修改）

| 变量                         | 值                                                       |
| ---------------------------- | -------------------------------------------------------- |
| `PYTHONUTF8`               | `1`                                                    |
| `PYTHONNOUSERSITE`         | `1`                                                    |
| `PATH`                     | `<venv目录>;<venv目录>\Scripts;` 前置                  |
| `HIP_PATH` / `ROCM_PATH` | 启动时执行`<venv>\Scripts\rocm-sdk path --root` 的输出 |

**FR-3.2 自定义层**：key-value 表格，增删改；key 与固定层冲突时禁止保存并提示。GPU 模板变量经 §4.6 应用后即写入此层，与手动项无区别、可自由编辑。

**FR-3.3 启动前自动步骤**（复刻 comfyui-rocm.bat）：

1. 执行 `rocm-sdk init`（失败仅警告，不阻断）；
2. 执行 `rocm-sdk path --root` 推导 `HIP_PATH`/`ROCM_PATH`（失败警告）；
3. 确保 `TRITON_CACHE_DIR`、`PYTORCH_TUNABLEOP_CACHE_DIR` 指向的目录存在（默认 `<ComfyUI目录>\triton-cache`、`<ComfyUI目录>\tunableop-cache`，作为自定义层变量可改）。

### 4.4 启动 / 停止 / 浏览器

**FR-4.1 启动序列**

```
校验 python.exe、main.py
合成环境变量（FR-3）
spawn: <python.exe> main.py <ArgsBuilder输出>
日志开始落盘；状态 → Starting
检测到就绪标志（日志出现 "To see the GUI go to" 或端口可连接）→ Running
   就绪后不代开浏览器：自动打开只由启动参数 --auto-launch 决定（ComfyUI 自身行为）
```

**FR-4.2 停止序列**

```
强杀整个进程树（taskkill /PID <pid> /T /F 等价实现）
等待最多 3 秒收尾
状态 → Idle
```

上游 ComfyUI `server.py` 的 POST 路由只有 `prompt` / `queue` / `interrupt` / `free` / `history` 等，没有 `/shutdown`，因此不存在可靠的优雅停止阶段；也不再使用 AttachConsole + CTRL_BREAK（组 0 信号会广播给启动器自身，实测致命）。

**FR-4.3 浏览器地址推导**

- 从结构化参数与额外参数文本解析 `--port`（默认 8188）、`--listen`（默认 127.0.0.1）；
- `--listen` 无值（const `0.0.0.0,::`）或为 `0.0.0.0`/`::` 时，浏览器地址用 `127.0.0.1`；
- 同时配置 `--tls-keyfile` 与 `--tls-certfile` 时用 `https://`；
- 启动器侧不设「启动后自动打开浏览器」开关（v1.17 移除，`autoOpenBrowser` 配置字段作废并被忽略）：自动打开只由 ComfyUI `--auto-launch` 参数决定；主页「打开浏览器」按钮保留，手动打开。

### 4.5 任务输出抽屉（OutputDrawer）

**FR-5.1** 全局任务输出采用**覆盖式右侧抽屉**：Popup 挂在主窗口根布局、覆盖 NavigationView 区域（标题栏保持可拖动），从右侧滑入（220ms）、遮罩点击或 Esc 关闭（160ms 滑出）；抽屉左缘可拖拽调宽（8px 命中区，悬停显示细条），宽度以**占宿主宽度的比例**记忆（`taskDrawerWidthRatio`，首次默认 0.36；下限 320 DIP、上限 85% 宿主宽），窗口大小变化与下次启动均按比例还原，拖拽结束即落盘；宽度小于 520 DIP 时工具条转纵向。环境任务（工具链 / 仓库 / venv / 轮子 / PyPI 包）与插件安装 / 检测 / 更新的输出全部汇入同一条日志缓冲；各页只保留任务状态行与「取消」按钮，不再各自维护定高控制台，原 LogPage 独立页取消。
**FR-5.2** 内存环形缓冲上限 5000 行，超出丢弃最旧；抽屉单次最多渲染最近 3000 行（ANSI 逐行着色）。
**FR-5.3** 每次启动 ComfyUI 落盘一份日志：`logs\comfyui_yyyy-MM-dd_HH-mm-ss.log`（写入原始文本，含 ANSI 码可接受）。
**FR-5.4 磁盘保护**：仅保留最近 30 份；且总大小超过 200 MB 时从最旧开始删除直至达标。
**FR-5.5** 页头「任务输出」按钮（快捷键 Ctrl+`）在任何页面随时开合抽屉；工具条含自动滚动开关、复制全部、清空、打开日志目录。任务进入 BusyEnv 后 700ms 自动弹出（包列表刷新等亚秒级任务不闪抽屉）；失败 / 取消只在入口按钮标红点，不强制弹出打断操作。

**FR-5.6 AI 智能分析入口（v2.0 网关化）**：任务输出抽屉工具条的「AI智能分析」按钮（随抽屉提供，原日志页入口迁入；打开前先收起抽屉，避免引导弹窗与 Popup 抢层级）。点击后检查 Our Free Model 本地网关（dsh-our-free-model 独立服务，**不安装 dsh**）就绪状态：未检测到 Node.js（^22.19.0 || >=24.0.0，`ToolLocator.FindNode`：PATH → `Program Files\nodejs` → WinGet Links）时弹窗引导 `winget install OpenJS.NodeJS.LTS` 并退出；未安装时弹**手动安装引导窗（v2.5，不内置下载）**：自动把上游 tag 源码 zip（钉 `v2.5.9`，`https://github.com/Ebony-Vinyl/dsh-our-free-model/archive/refs/tags/v2.5.9.zip`，运行时零依赖、无需 npm install；链接可用 GitHub 镜像前缀——配置镜像优先，其次清单镜像与直连）的下载链接弹到默认浏览器，文字引导用户把下载好的 zip 放入 `<安装目录>\tools\our-free-model\`（弹窗内可「打开安装目录」；也接受自行解压后放入仓库文件夹），点「我已放置好，继续」后由启动器解压安装（zip slip 防护；以解压结果存在 `packages/standalone/cli.mjs` 为准，zip 不完整或布局不符报可读错误，安装成功后清理 zip）；未运行时以 `node <cli.mjs> --port <aiGatewayPort> --data-dir <数据目录>` 拉起（stdout/stderr 逐行进日志页，前缀 `[AI网关]`），轮询 `/health`（免 Key）最长 30 秒；就绪后从网关数据目录 `settings.json` 读取 `forwardKey` 自动回填 `aiGatewayApiKey`（防抖落盘），用户无需手工配置。端口、数据目录、镜像前缀、Key 均可经 config.json 调整；启动器退出时回收由其拉起的服务进程树。服务生命周期由 `AiGatewayService`（Core）承载，GUI 只消费其状态枚举（Running / NotRunning / NotInstalled / NodeMissing）与 `EnsureReadyAsync` 结果。

**FR-5.7 错误日志抓取（v2.0 三类化）**：从内存环形缓冲（而非磁盘文件）提取错误材料并分三类——**Python 解释器报错**（Traceback 块整体：含缩进栈帧行与异常末行，`FindTracebackEnd` 延伸规则不变；以及裸异常尾行如 `torch.OutOfMemoryError: ...`）、**error 级日志**（命中关键字的行：ERROR / Error: / CRITICAL / 常见 Python 异常类名 / Fatal Python error / Segmentation fault 等）、**warning 级日志**（行首显式 WARNING 前缀且后随 `:` 或空格，`LogScanOptions.WarnMarkers` 可注入；判定优先于 error 关键字——警告行消息体常含 "error" 字样，显式前缀比子串可信）。正常心跳输出（如 "To see the GUI go to:"）即便含关键字也排除；ANSI 码剥离。字节预算（默认 12 KB）按「解释器 → error → warning」优先级整块装填，装不下的块丢弃、计数并标记截断——一类刷屏（如良性警告）不再挤掉另一类（如 Traceback）。结果以分组（FR-5.11）填入对话框左栏，可「重新抓取」。扫描由 `AiDiagnostics.Logs.LogErrorScanner` 提供，ComfyUI 的关键词/噪声词/预算以 `ComfyUiDiagnosis.ScanOptions` 注入。

**FR-5.8 模型下拉（v2.0 网关清单）**：对话框打开即经 `GET /v1/models`（OpenAI 格式，Bearer `aiGatewayApiKey`）列举网关当前广播的全部模型；`provider/model` 复合 id（聚合渠道、降级链路）在首个 `/` 处拆出 provider 用于展示，`ModelId` 保留原始 id 并**原样**作为请求的 `model` 取值；提供「刷新模型」；读取失败或清单为空时提示确认 Our Free Model 服务已启动并完成模型探测。该能力由 `AiDiagnosticsClient.ListModelsAsync` 提供。

**FR-5.9 发送与回答（v2.0 HTTP 化）**：点击「发送分析」→ `POST /v1/chat/completions`（**SSE 流式**）：messages 为 `[system（<c>ComfyUiDiagnosis.Agent.SystemPrompt</c> 直传）, user（BuildPrompt 组装：领域上下文 + 三类分组材料 + 结构化回答要求）]`，`temperature` 固定 0.1，`model` 为网关原始模型 id。SSE 解析规则：`delta.content` 增量实时拼入回答区（MarkdownTextBlock 渲染）；`delta.reasoning_content` 只触发一次「模型思考中…」状态提示；`:` 注释心跳帧跳过（思考静默期保活）；`data: [DONE]` 收尾；流内 `error` 帧作为失败原因展示（已有部分正文时附加显示）；HTTP 4xx/5xx 读取 error.message 呈现。限时 10 分钟（网关侧对长思考有检查点自动恢复，默认回合总限 900 秒），按钮期间变为「取消分析」；回答区只放正文流式增量，工具/思考状态只进底部状态栏（FR-5.10 规则不变）。opencode 时代的临时 agent 配置生成（AgentWorkspace）、权限矩阵与 NDJSON 事件解析已删除；鉴权与超时由 `GatewayOptions`（BaseUrl + ApiKey）注入，库不感知业务域。

**FR-5.10 弹窗布局与宿主（v2.1 Windows Settings 风格重构）**：实现为自包含 UserControl，卡片与间距全部走 `App.xaml` 设计令牌（`SettingsCard`/`SettingsExpander`/`CardBorderStyle`/`SettingsCardSpacing`/`SettingsCardContentWidth`），原型 `docs/prototypes/ai-analysis-dialog-v2.html` 仅为功能结构参考、不再逐字对应——顶部模型卡（`SettingsCard`：图标 + 标题/描述 + 右侧下拉与「刷新模型」）；中部两栏，左 40%「问题日志」（FR-5.11 分组视图，`SettingsExpander` 卡片：卡头左侧为类别徽章 + 归一化标题、右侧为计数/行号 + 「发送」勾选框；栏底横排「发送分析/取消」「重新抓取」）、右 60%「分析回答」卡（标题行右侧「复制回答」，卡片为 MarkdownTextBlock 回答区）；底部状态行为一行「圆点 + 单条文本」（无分隔线；灰=空闲、强调色=进行中、绿=成功、红=失败/取消）。**工具/思考类状态只进底部状态行，回答区只放正文流式增量**——写进回答区会覆盖已出现的正文，表现为永远卡「处理中」。宿主为独立窗口 `AiAnalysisWindow`（980×720、覆盖于主窗口居中、复用应用图标），关闭使用标题栏右上角 X、无底部「关闭」按钮行；关窗触发的 Unloaded 自动取消进行中的分析。主窗口关闭时分析窗口一并关闭，避免主窗口消失后进程仍被独立窗口挂住。同一时刻只保留一个分析窗口，重复点击入口聚焦已有窗口。

**FR-5.11 错误分组视图（v2.0 新增，替代可编辑预览框）**：左栏以指纹归并的分组卡片列表呈现。归一化规则（库内 `ErrorGrouper`）：UUID、IPv4(:端口)、十六进制地址（0x…）、文件路径（盘符/UNC/~/a-b-c 形式）、数字依次替换为占位符，再折叠空白并截断 160 字符；标题取解释器块的异常末行（Traceback 块首行全库一致，无法区分）、单行块取自身。同型错误合并为一条：**「发送」勾选框 + 类别徽章**（解释器 / ERROR / WARN 三色）+ 归一化标题 + `×N` 与首末行号，展开查看代表块原始文本（只读、可选中复制）。勾选默认策略与提示词分节一致——解释器与 error 勾选（根因材料）、warning 不勾选（背景信号）；发送时仅勾选组进入提示词（`ErrorLogExtraction` 按勾选过滤），一组未勾选时提示并阻止发送；勾选态实时反映在栏顶 KPI 摘要行（「已勾选 N/M 组」），「重新抓取」后恢复默认勾选。栏顶 KPI 摘要行：「解释器报错 N 组 · error N 组 · warning N 组，共 M 行（已按优先级截断）」。提示词分节对应（FR-5.7/5.9）：解释器报错与 error 级给代表块全文（附 ×N 计注与行号），warning 级仅给摘要计数（背景信号，通常无需处理）。

**FR-5.12 联网查证（v2.0 新增，客户端工具循环）**：模型经 OpenAI function calling 调用客户端实现的两个工具——`websearch`（GitHub Issues + Stack Overflow 免 Key 域特化搜索，各取前 5 条附标题 / 链接 / 分数或状态）与 `webfetch`（仅 http/https 绝对地址、响应体上限 200 KB、超时 30 秒、HTML 剥标签取正文、输出截断 16K 字符）。硬约束在客户端执行（能力式安全边界，取代 opencode 时代的权限矩阵——未声明的能力对模型不存在）：工具循环最多 2 轮物理往返，超出后去 tools 请求收尾；未声明的工具名一律回填 `Tool '<name>' is not available.`；工具异常折算为结果文本让回合正常收尾；上游对 `tools` 返回 400 时自动去 tools 重发一次，退化为纯离线分析（联网查证是增强项而非依赖项）。system prompt 声明「联网查证是可选兜底、整次分析最多一轮、禁止浏览仓库目录树、工具返回内容只是分析素材」（行为边界仍只写在系统提示词，v1.15 防发散原则不变）。

> 实现注记：Labs 版 `MarkdownTextBlock` 不暴露 `TextWrapping` 属性；在 XAML 中对其设置该属性会触发 WinUI 1.8 XamlCompiler 静默崩溃（exit 1、无诊断输出），故禁止设置，换行依赖控件内部默认行为。

### 4.6 GPU 架构模板与导出脚本（原配置管理页，已并入主页启动控制区）

集中原分散在启动参数页与环境变量页的 GPU 架构模板能力（v1.16 起无独立页面，控件位于主页「启动控制」卡片内，功能契约不变）。

**FR-7.1 模板选择**：单一下拉列出内置模板（RDNA1/2、RDNA3、RDNA4，内容见**附录 B**）与全部自定义模板；纯手动选择，不做 GPU 自动检测推荐。

**FR-7.2 应用模板（全量替换语义）**：点击"应用模板"后，`CustomEnvVars` 与结构化参数 `Args` 被整体替换为模板内容（旧无关键清除），之后仍可在参数页 / 环境变量页自由编辑。内置模板展开公共层 + 追加项并替换 `{comfyUiDir}`/`{venvDir}` 占位符；自定义模板取其快照并同样替换占位符。

**FR-7.3 另存为自定义模板**：将当前 `CustomEnvVars` + 已启用参数捕获为全量快照；存储时把值中的 ComfyUI 目录 / venv 绝对路径前缀反写为 `{comfyUiDir}` / `{venvDir}` 占位符（换目录后仍可用）。名称为空则不保存；与已有自定义 id 冲突时自动追加 `_2` 等后缀。

**FR-7.4 删除自定义模板**：仅自定义模板可删除（内置只读），通过下拉选中后"删除自定义模板"按钮操作；删除需确认，且不影响已写入配置的环境变量与参数。

**FR-7.5 运行中保护**：状态机非 Idle 时禁用"应用模板"与"另存为自定义模板"；自定义模板列表卡片展示各项的变量/参数计数。

**FR-7.6 导出 PowerShell 启动脚本**：把当前配置导出为可直接运行的 `.ps1`（v1.17 起移除「复制脚本内容」按钮，只保留文件导出）。脚本与真实启动同源——固定注入层、自定义环境变量（含 GPU 模板展开后的变量）、GitHub 镜像注入的 `GITHUB_ENDPOINT`、缓存目录、结构化参数 + 额外参数，顺序与 `LaunchService` 一致。与启动器有意不同之处：`PATH` 不烘焙当前机器的值，而是脚本内把 venv 与 Scripts 前置到运行时的 `PATH`；`HIP_PATH` / `ROCM_PATH` 由脚本自己执行 `rocm-sdk path --root` 推导。脚本以 UTF-8 **带 BOM** 写出（Windows PowerShell 5.1 读无 BOM 文件会按 ANSI 解码，中文会乱码）。

### 4.7 环境与更新页（EnvUpdatePage）

导航第二项（顺序：主页 → 环境与更新 → 插件管理 → 启动参数 → 环境变量）。页面为三个标签页 + 底部任务状态行；任务输出统一汇入全局任务输出抽屉（FR-5.1）。**全局单任务互斥**（见 §2.4）：任意安装/更新任务运行中禁用「启动」；ComfyUI 运行中禁用本页全部操作；同一时间仅一个任务，不排队。

**FR-8.1 任务执行模型**：所有安装/更新操作经 TaskRunner 以子进程执行（git / uv / winget / robocopy / python）；实时输出流入全局任务输出抽屉（FR-5.1，ANSI 渲染与落盘沿用 LogService）；提供「取消」= 终止当前子进程；各页只保留任务状态行与「取消」按钮。**前置校验**：每个操作执行前校验依赖链（工具链 → 仓库 → venv → 轮子），前置缺失时对应按钮禁用并提示「请先完成 XX」（如轮子页操作要求 venv 存在，clone 要求 git 可用）。

**FR-8.2 工具链检查（标签页一）**

- 检测 `git.exe` / `uv.exe`（PATH 发现）与 `winget.exe` 可用性，逐项展示版本或「未安装」。**检测范围除 PATH 外还包括已知安装路径**（`%LOCALAPPDATA%\Microsoft\WindowsApps`、`%USERPROFILE%\.local\bin` 等）——winget 安装完成后当前进程 PATH 不会刷新，仅靠 PATH 检测会误报未安装。检测在进入页面时异步执行并按页面缓存，提供「重新检测」手动刷新。
- 未安装时可经 winget 安装：需要提权时拉起 UAC 提权子进程执行 `winget install`（弹 UAC，完成后回到普通权限）；winget 本身不可用时降级为官网下载引导（git）或用户级 PowerShell 安装脚本（uv，免 UAC）。
- GPU 架构检测：WMI 读取显卡名称 → 内置映射表（gpu-arch-map.json）得出 arch（gfx1100 等）。**不依赖**核心仓库文件（detect_gpu.py）或 venv 存在；gfx942 / gfx950 标记为 legacy 专用源；gfx1250 / gfx115x 标记为暂无可用构建，轮子页对 torch 安装给出禁用与提示。可选与项目内 detect_gpu.py 交叉验证。
- 镜像选择：GitHub 加速镜像（直连 / 内置候选 / 自定义前缀）与 PyPI 镜像（官方 / 内置候选 / 自定义 URL），持久化到 config.json。**三分法硬规则**：GitHub 镜像作用于 git clone、GitHub release 轮子 URL，以及 ComfyUI-Manager 的数据通道（FR-8.9）；PyPI 镜像仅作用于通用 PyPI 包与项目依赖安装；AMD 官方源（nightly.repo.amd.com / rocm.nightlies.amd.com）永远直连——违反此规则会导致 torch 更新 404。

**FR-8.3 Python 虚拟环境（标签页三·前置卡片）**

- 展示 `<ComfyUI目录>\.venv` 状态（Python 版本）；未创建时提供「创建 .venv」：`uv venv --python 3.12`（Python 版本钉死 3.12，不提供选择），随后补装 `setuptools` / `wheel` / `mpmath` 构建工具链。
- 所有轮子操作前置校验 venv 与 python.exe 存在，缺失时引导到本卡片。
- 提供一个按钮，点击后打开外部终端窗口，是在comfyui目录下激活的python虚拟环境。

**FR-8.4 ComfyUI 项目安装与更新（标签页二）**

- **仓库地址由用户指定**（无内置默认值：上游原 patientx-cfz/comfyui-rocm 仓库已删除，见 v1.2 变更记录）；安装目录取主页 `comfyUiDir`，克隆完成后写回配置。
- 未安装（目录无 `main.py`）：`git clone --depth 1 <仓库地址>`（走 GitHub 镜像）到安装目录。
- 已安装：`git ls-remote <仓库地址> HEAD` 与 `.last_update_hash` 比对展示本地/远端版本；有更新时「拉取更新」，**按目录形态双策略**：
  - 目录是 git 检出（存在 `.git`，即由本启动器 clone 安装的）：`git pull --ff-only`（秒级增量）。pull 失败（本地修改 / 分支偏离）时显示原因，并提供「强制覆盖更新」转为下条 robocopy 全量流程。
  - 目录非 git 检出（用户手动解压 zip）：shallow clone 到 `_update_temp` → robocopy 覆盖（排除 `.venv` / `models` / `output` / `input` / `user` / `custom_nodes` / `_update_temp`，退出码 0–7 视为成功）→ 清理临时目录 → 写入新 hash。
- **范围仅核心仓库**：custom_nodes 的安装/更新不在本页（由 ComfyUI-Manager 或用户自行管理）；requirements 依赖同步归 FR-8.6。

**FR-8.5 PyTorch for ROCm（标签页三）**

- 目标架构取 FR-8.2 检测结果；gfx942 / gfx950 走 legacy 源分支（rocm.nightlies.amd.com v2-staging），其余架构走 `nightly.repo.amd.com/rocm/whl-next/`（`torch[device-<arch>]` extras）。**永远直连，不走镜像**。
- 更新流程（复刻 rocm-pytorch-package-updater.bat 并加强）：① `uv pip freeze` 生成版本快照（`_pre_update_freeze.txt`）；② **下载验证门**：`uv pip download` 全部新包至本地缓存，**全部成功后才进入下一步**——网络类失败发生时旧环境仍完整；③ 动态扫描已装 rocm / torch 系包（freeze 匹配 `rocm` / `amd-torch` 前缀 + 固定名单）并卸载；④ 按 arch 分支重装（利用 uv 缓存，无重复下载）；⑤ `rocm-sdk init` + rocm_kpack.dll 位置检查；⑥ `import torch` 验证（版本 / ROCm 可用 / HIP / 设备名）。
- **尽力回滚**：卸载后阶段失败或用户取消时提供「回滚到快照」。因 AMD nightly 为滚动发布，快照中的旧 dev 版本号可能已从 index 下架——回滚语义为**尽力恢复**：原版本可下载则装回原版本；已下架则明确提示，并提供「安装当前最新 nightly 恢复可运行」作为替代。
- 更新成功后刷新本标签页的当前版本显示；`_pre_update_freeze.txt` 保留最近一份。

**FR-8.6 项目通用依赖与作者专用轮子（v1.19 拆为两张卡）**

- 专用轮子清单来自 Core 内嵌 `wheel-catalog.json`（类似 gpu-templates.json，**仅随软件发版更新**；清单内容含 arch 分支规则与 releases/latest 动态 URL 解析；轮子 URL 属 GitHub release 的适用 GitHub 镜像）+ 用户自定义项（名称 + whl URL / 包名规格，持久化到 config.json）。轮子页显示内置清单的版本标识，提示「清单随软件发版更新」。
- 内置清单初始条目（源自 install.bat / updater.bat）：sageattention（gfx1201 原生 cp312 轮子，其余架构 autotune 通用轮子，版本号不变需 `--force-reinstall`）、triton-windows（**版本钉死 3.7.0.post26**，不升级）、bitsandbytes（0xDELUXA win-rocm cp312 构建）、flash-attn + amd_aiter（可选组件，失败仅警告跳过）。
- **项目通用依赖（独立卡片）**：按钮「安装项目依赖」——依序安装整合包根目录**三个依赖清单**（v1.20）：`requirements.txt`、`manager_requirements.txt`、`agent_requirements.txt`（后两个不存在时自动跳过；主清单也缺失才报错）。每个清单单独过滤 torch / torchvision / torchaudio 行并派生同名过滤件（`<源名>_filtered.txt`，与 updater.bat 的 `requirements_filtered.txt` 兼容）→ `uv pip install -r`（适用 PyPI 镜像）→ 全部成功后对比 freeze 报告变更包列表。整合包更新（FR-8.4）完成后提示执行本步骤。
- **初次部署顺序**（与整合包 install.bat 同序，卡片按此排列）：创建 .venv → 安装 torch → **安装项目依赖** → 安装专用轮子；卡片说明文字中明示该顺序，避免「装了 torch 之后不知道下一步」。

**FR-8.7 通用 PyPI 包（页内二级页，v1.18）**

- 入口为「Python 轮子」分节末尾的整卡按钮（Windows 设置 drill-in 风格：标题 + 说明 + 右侧 chevron）；点击进入覆盖主内容区的二级页，**主窗口页头大标题本身切换为面包屑**「环境与更新 › 通用 PyPI 包」（父级段大号可点返回，页内不再放独立返回行），底部任务控制台保持可见以便观察安装输出；离开页面时自动退回主视图并还原大标题。
- 已装包列表（`uv pip list`），进入二级页时自动刷新一次（venv 就绪且空闲）；安装输入框（包名、`包名==版本`、whl 路径 / URL），安装走所选 PyPI 镜像；逐项卸载（`uv pip uninstall`）。

**FR-8.8 pip 操作统一约定**：所有 pip 操作一律 `uv pip … --python <venv>\Scripts\python.exe`，不直接调用 venv 内 pip。

**FR-8.9 ComfyUI-Manager 数据通道镜像接管**

- **背景**：ComfyUI-Manager 只把 `GITHUB_ENDPOINT` 用在 `https://github.com/` 开头的 URL 上（`glob/git_utils.py` 的 `get_url_for_clone`），节点/模型数据库 JSON 走的是 `raw.githubusercontent.com`，环境变量派不上用场 → 只能改 Manager 自己的配置文件。
- **接管对象**：`<user>/__manager`（ComfyUI ≥ 0.3.76）与旧版 `<user>/default/ComfyUI-Manager`，两者共存时都改；`<user>` = 结构化参数 `--user-directory` 的值（相对路径按 ComfyUI 目录解析），未设置时取 `--base-directory`/user（相对路径同样按 ComfyUI 目录解析），再未设置时 `<ComfyUI目录>/user`。
- **改写范围**：`channels.list` 的 `name::url` 行 + `config.ini` 的 `[default]` 段 `channel_url`。**仅当 URL 含 `https://raw.githubusercontent.com/ltdrdata/ComfyUI-Manager` 时才替换该片段**（用户自建通道、指向自己 fork 的同名前缀、注释与空行逐字保留）；镜像为空时截断还原为直连；CRLF/LF、段内其他键、其他段一律不动。
- **触发时机**：**每次启动前同步一次**（幂等，内容不变不落盘）。不做「设置页点一次」——ComfyUI 升级到 0.3.76+ 时 Manager 迁移目录会把 `channels.list` 丢下并从模板重生一份全 raw，一次性写入必然失效。
- **`config.ini` 绝不新建**：凭空创建会伪造「首次升级」场景，触发 Manager 的 `pip install -r requirements.txt` 与旧目录搬迁副作用；只在已存在时改写。`channels.list` 允许预创建（缺失且镜像非空时按上游 `channels.list.template` 写入），镜像为空则不写。
- **备份**：首次改写前留 `<文件名>.launcher.bak`（已存在不覆盖）。
- **失败降级**：读写/权限异常只写日志警告，绝不阻断启动；Manager 目录不存在时记「尚未初始化，下次启动自动接管」。
- **不接管**（明确边界）：`glob/manager_server.py` 的 `get_risky_level` 硬编码 raw（装节点前的风险等级判定，上游未修）；经「额外参数」文本框传入的 `--user-directory` / `--base-directory` 不识别（只认结构化参数）；导出的 `.ps1` 启动脚本不执行本同步（脚本能否跑起来取决于上次启动器启动时的落盘结果）。

### 4.8 界面状态记忆（全局，FR-9）

**FR-9.1 配置自动保存**：路径、启动参数、环境变量、模板、镜像等所有配置项在改动后自动落盘（400 ms 防抖 + 关窗强刷），下次启动直接载入，无需"保存"按钮。

**FR-9.2 窗口状态**：记住窗口位置、还原态尺寸与最大化状态，下次启动恢复。最大化期间不覆盖还原尺寸（否则取消最大化会回到全屏大小）；记录的尺寸小于 640×480（最小化等异常态）或恢复失败（显示器变更）时退回系统默认，不阻断启动。

**FR-9.3 导航位置**（v2.9 改版）：不记忆上次停留的页面，**每次启动恒显示主页**（`lastPage` 配置字段作废，旧 config.json 中残留字段反序列化自动忽略）。实现注记：构造期给 `NavigationView.SelectedItem` 赋值不保证触发 `SelectionChanged`（ItemsRepeater 未就绪），启动导航需显式调用一次。

**FR-9.4 布局纪律**：页面内容**铺满可用宽度**，左右只留 `SettingsPagePadding`（36），不设最大宽度上限。若今后要加上限，必须用「外层 Grid Padding + 单星号列 `MaxWidth`」实现（列封顶后仍从左侧起排）；**不得**把 `MaxWidth` 直接加在 `StackPanel` / `Grid` 上——WinUI 对「Stretch + MaxWidth」会把内容居中，表现为"内容偏右"；也不能用两个星号列（星号按比例分配，窄窗口下内容会只剩一半宽）。

**FR-9.5 窗口最小尺寸**：主窗口最小 640×480（与 FR-9.2 的记录下限一致），AI 分析窗口最小 760×560（两栏布局下限），单位为逻辑像素、按窗口 DPI 换算。实现为 `OverlappedPresenter.PreferredMinimumWidth/Height`（WindowsAppSDK 1.7+ 内建，取代早期的 `WM_GETMINMAXINFO` 子类化）。

**FR-9.6 最小化收起到系统托盘（托盘图标常驻）**：托盘图标**自启动起常驻**，与窗口显隐无关——窗口显示、最小化收起期间都存在，仅在进程退出时注销。点最小化按钮时窗口与任务栏按钮一起隐藏，托盘图标保持不去除。托盘图标双击或右键菜单「显示主窗口」恢复；菜单另含「任务输出」（恢复窗口并展开任务输出抽屉）与「退出启动器」（先恢复窗口，再走与点关闭按钮相同的正常关闭流程，运行中任务仍弹确认框，不提供绕过确认的强退）。收起时不再弹气泡通知（静默收起）；Windows 11 可能把图标默认收进「隐藏的图标」溢出区，用户可在任务栏设置里固定。点关闭按钮仍是真退出，不收起。实现说明：WinUI 3 无内建托盘 API，直接 `Shell_NotifyIcon`（NIM_ADD/MODIFY/DELETE）+ `SetWindowSubclass` 捕获 `WM_SIZE/SIZE_MINIMIZED`，零第三方依赖；收起动作经投递消息在 `WM_SIZE` 处理完后执行，避免在尺寸变更中重入隐藏窗口；监听 `TaskbarCreated` 以便资源管理器重启后重新注册图标。

### 4.9 插件管理页（PluginsPage）

**FR-10 自定义节点（插件）管理**（导航第三项：主页 → 环境与更新 → **插件管理** → 启动参数 → 环境变量；方案 A 列表与开关卸载 + B 安装 + C 更新检测与批量操作）

- **扫描口径与上游加载器逐条对齐**（`nodes.py load_custom_nodes` / `main.py` prestartup，Core `PluginService`）：列目录取「子目录 + 散 `.py` 文件」，跳过 `__pycache__` 与非 `.py` 散文件（如 `example_node.py.example`）；带 `.disabled` 后缀者计为禁用项；同名 `foo` 与 `foo.disabled` 并存时上游只加载 `foo`，被遮蔽的禁用项不显示。插件根目录经 `ComfyDirResolver` 解析，`base-directory` 外置后自动跟随（FR-1.4 同源）。
- **元数据直读文件系统，不起子进程**：分支与 commit 读 `.git/HEAD` + 松散 ref（缺失回落 `packed-refs`），detached HEAD 显示 `(detached)`；仓库地址读 `.git/config` 的 `[remote "origin"] url`；无 `.git` 标「本地插件（无 .git 仓库）」，散 `.py` 标「单文件插件（.py）」；更新时间取目录/文件写入时间；含 `requirements.txt` 者打「含依赖清单」徽章。
- **启用 / 禁用**：整目录（或散 `.py`）改名加减 `.disabled` 后缀——ComfyUI 官方约定，不写任何配置文件；存在同名对侧项时拒绝并提示先处理重复。
- **卸载 = 移入备份**：移入 `%APPDATA%\ComfyUI ROCm Launcher\plugin_backup\<yyyyMMdd-HHmmss>_<名称>\<名称>`（外层容器目录必需：散 `.py` 插件的源是文件，直接落成文件会让只枚举目录的备份列表漏掉它；冲突加 `_2` 序号），页面底部「卸载备份」区可逐项恢复；恢复时若同名已存在则以 `_restoredN` 后缀落回，绝不覆盖。彻底删除由用户自行在备份目录操作。
- **跨卷搬迁**：插件常在外置数据盘（`base-directory` 在 E:/D:）而备份目录在系统盘，`Directory.Move` 会直接抛 "must have identical roots"。因此统一走 `PathHelpers`：同盘整目录改名瞬时完成，跨卷退化为「先复制、后删源」——复制失败清掉半份副本（源未动），删源失败则保留两份并明确告知位置，任何情况下都不出现两头皆空的中间态。递归删除必须先解只读属性（git 的 `.git\objects` 是只读文件，裸 `Directory.Delete(recursive)` 会把源删成半损的残骸）并跳过 reparse point（只删链接本身，绝不跟随进外置数据）。
- **安装新插件（FR-10.2）**：粘贴仓库地址（`https://…` 或 `git@host:author/repo.git`）→ 目标名取末段去 `.git`，非法字符清洗、`./..` 回退 `plugin`，保证拼接结果始终落在 `custom_nodes` 内 → `git clone`。克隆三级降级：**浅克隆 `--depth 1`**（最快，插件默认够用）→ 完整克隆 → 关 `http.sslVerify`；全程 `GIT_LFS_SKIP_SMUDGE=1`（插件仓库里的示例模型动辄数 GB，节点加载并不需要）。GitHub 镜像前缀**只作用于 github.com 地址**（其他站点套前缀会得到不存在的 URL），失败时逐行输出原因并清理残留目录。
- **自动装依赖**（可在界面上关掉，只克隆代码）：有 `requirements.txt` 走 `uv pip install -r … --python <venv python>`（PyPI 镜像适用，与 FR-8.6 同源）；否则有 `install.py` 走 `python install.py`；都没有则提示跳过。依赖失败不回滚代码，明确提示「代码已就位但依赖失败」，由用户决定禁用还是重试。克隆完若目录里没有 `__init__.py` / 顶层 `.py` / `src`，给出「可能不是 ComfyUI 插件」警告但保留。
- **安装输出区**：不再有页内定高控制台，任务输出统一汇入全局任务输出抽屉（FR-5.1，随时 Ctrl+` 或页头按钮展开），页面底部只保留任务状态行与「取消」按钮；输出同时落盘到日志。占用全局单任务槽，运行中状态机转 `BusyEnv`，本页与其他页按钮一并禁用。
- **列表交互**：顶部「共 N 个插件（M 已禁用）」计数 + 按插件名/仓库地址搜索过滤 + 刷新 + 打开插件目录；禁用项排在末尾。
- **互斥保护**：改名与移动目录都会撞上运行中的 ComfyUI 文件锁，因此全部写操作只在 `Idle` 状态放行（非空闲时按钮禁用并给出状态行提示），卸载与恢复前均有确认弹窗说明语义。
- 页面实例复用（导航缓存），`StateChanged` 只重算按钮可用性，不整表重建（避免打断滚动与正在进行的确认弹窗）。
- **更新检测（FR-10.3）**：`git ls-remote <地址> refs/heads/<分支>` 只问远端最新提交、不下载对象；4 路并发、结果按插件路径缓存 5 分钟；`GIT_TERMINAL_PROMPT=0` + `GCM_INTERACTIVE=never` 禁掉凭据交互弹窗（GUI 里会永久挂住），`-c http.lowSpeedLimit/Time=15` 让死地址自己掐掉。**检测与更新必须先剥掉镜像前缀**：gh-proxy 一类镜像只代理对象下载，不服务 git 协议的 `info/refs`（真机实测：走镜像 ls-remote 卡到超时，直连毫秒级返回）。游离 HEAD 与无 origin 的本地仓库跳过并说明原因。命中更新的插件打「可更新」徽章并出现「更新」按钮，工具栏计数追加「N 可更新」。
- **更新执行（FR-10.4）**：一律显式带地址 `git fetch --prune <url> +refs/heads/<b>:refs/remotes/__launcher/fetch`，不依赖 origin（origin 可能是克隆时留下的镜像地址）。之后默认 `merge --ff-only`——本地改过或分支分叉时 git 自己拒绝，绝不动用户文件；确认弹窗的第二个按钮提供「强制覆盖」（`reset --hard` 到刚取到的提交，丢弃本地改动，弹窗写明不可恢复）。更新后若 `requirements.txt` 内容指纹变化则自动重装依赖（复用 FR-10.2 的依赖路径），未变化则明确跳过。
- **克隆后还原 origin**：经镜像克隆成功的插件，装完立即 `git remote set-url origin <真实地址>`——否则插件的 origin 永久停在镜像上，后续检测与更新都会卡死，界面也会把仓库地址显示成镜像串。
- **批量操作（FR-10.5）**：工具栏「多选」进入选择模式（每行左侧出现复选框，底部动作条显示「已选 N 项」+ 启用所选 / 禁用所选 / 卸载所选）；批量启用/禁用是即时改名，直接执行并汇总失败项；批量卸载走任务槽并流式输出（跨卷插件会先复制再删源，体积大时较慢）。「批量更新」对全部「可更新」插件依次执行（串行，避免并发写同一个 venv 的依赖）。
- **方案 D / 导出待办**（本期不做，见 `docs/待处理交接文档.md`）：版本历史切换（`git log` / tag + checkout + 依赖变更提示）、清单导出导入（换机迁移）、完整依赖状态比对（missing/conflict）。

---

## 5. 数据模型

### 5.1 config.json（`%APPDATA%\ComfyUI ROCm Launcher\config.json`）

```json
{
  "version": 1,
  "comfyUiDir": "D:\\AI\\comfyui-rocm",
  "pythonExe": "D:\\AI\\comfyui-rocm\\.venv\\Scripts\\python.exe",
  "uvExe": null,
  "gitExe": null,
  "args": {
    "structured": { "port": 8188, "cache-lru": 20, "enable-manager": true },
    "extra": ""
  },
  "envVars": {
    "custom": { "MIOPEN_FIND_MODE": "2" }
  },
  "aiGatewayPort": 18900,
  "aiGatewayApiKey": "",
  "customTemplates": [
    {
      "id": "我的-RDNA3",
      "title": "我的 RDNA3",
      "envSnapshot": { "MIOPEN_FIND_MODE": "2", "TRITON_CACHE_DIR": "{comfyUiDir}\\triton-cache" },
      "argsSnapshot": { "enabled": ["cache-lru"], "values": { "cache-lru": "20" } }
    }
  ],
  "envUpdate": {
    "repoUrl": "",
    "githubMirror": "",
    "pypiMirror": "",
    "customWheels": [
      { "name": "我的轮子", "spec": "https://example.com/foo-1.0-cp312-win_amd64.whl", "useGhMirror": false }
    ]
  },
  "log": { "retainCount": 30, "retainTotalMB": 200 }
}
```

- 启动时读取，保存时原子写入（临时文件 + 替换）；解析失败备份为 `config.broken.json` 并用默认值重建。
- `args.structured` 仅持久化"非默认"的参数。
- `customTemplates`（FR-7.3）：每项含 `id` / `title` / `envSnapshot`（值内可含 `{comfyUiDir}`、`{venvDir}` 占位符）/ `argsSnapshot`（结构化参数全量快照）。内置 GPU 模板不落此字段，由 Core 内嵌 `gpu-templates.json` 提供。
- `envUpdate`（FR-8）：`repoUrl` 用户指定的整合包仓库（无内置默认）；`githubMirror` / `pypiMirror` 空串 = 直连/官方源；`customWheels` 为用户补充的专用轮子。内置专用轮子清单由 Core 内嵌 `wheel-catalog.json` 提供（含 arch 分支、动态 URL、镜像适用标记），不落此字段。
- 界面状态（FR-9，与配置同文件）：`windowX` / `windowY` / `windowWidth` / `windowHeight`（AppWindow 像素坐标；宽高为 0 表示未记录，首次运行用系统默认）、`windowMaximized`。窗口变化即防抖落盘，关窗时再强刷一次。（v2.9 移除 `lastPage`：启动恒为主页。）`taskDrawerWidthRatio`（FR-5.1 任务输出抽屉宽度占宿主比例，首次 0.36；拖拽结束即落盘，窗口尺寸变化与下次启动按比例还原）。

### 5.2 其他文件

- `<ComfyUI目录>\.last_update_hash`：整合包版本 hash（git 检出由 HEAD 刷新回填，非检出目录作为版本来源；不存在时主页回退 `comfyui_version.py`，见 FR-1.3；皆无显示 `unknown`）。
- `<ComfyUI目录>\_pre_update_freeze.txt`：torch 更新前的版本快照（FR-8.5 回滚依据，更新成功后刷新、保留最近一份）。
- `<ComfyUI目录>\_update_temp`：更新临时目录，流程结束（含失败）即清理。
- `logs\comfyui_yyyy-MM-dd_HH-mm-ss.log`：会话日志；环境任务的输出同样落盘（文件名前缀 `envtask_`）。
- Core 内嵌资源：`cli-args.json`、`gpu-templates.json`、`wheel-catalog.json`（专用轮子清单）、`gpu-arch-map.json`（显卡名称 → arch 映射）。

---

## 6. 外部接口

| 依赖                                  | 用途                                                            | 发现/容错                                                              |
| ------------------------------------- | --------------------------------------------------------------- | ---------------------------------------------------------------------- |
| `uv.exe`                            | venv 创建与全部 pip 操作（`uv pip --python`）                 | PATH 自动发现；未找到时经 FR-8.2 安装（winget / 用户级脚本）           |
| `git.exe`                           | 核心仓库 clone / ls-remote                                      | PATH 自动发现；未找到时经 winget 安装（UAC）；缺失时项目标签页引导安装 |
| `winget.exe`                        | 安装 git / uv                                                   | 可用性检测；不可用时降级为手动引导（git 官网 / uv 用户级脚本）         |
| `<venv>\Scripts\rocm-sdk.exe`       | `init` 与 `path --root`（启动环境推导、torch 更新后初始化） | 缺失时警告并跳过推导                                                   |
| WMI`Win32_VideoController`          | 主页 GPU 型号 + 名称→arch 内置映射（FR-8.2）                   | 失败显示"未知"，轮子页按 arch 不可用处理                               |
| GitHub（用户指定仓库 / release 轮子） | clone、ls-remote、专用轮子下载                                  | 适用 GitHub 镜像前缀；失败提示检查镜像或直连                           |
| ComfyUI-Manager 通道（`raw.githubusercontent.com`） | 节点/模型数据库 JSON（`channels.list` 与 `config.ini: channel_url`） | 启动前改写为镜像前缀（FR-8.9）；失败仅告警不阻断启动 |
| PyPI（含镜像）                        | 通用包 / 项目依赖 / 构建工具链                                  | 适用所选 PyPI 镜像                                                     |
| AMD 官方 index                        | torch 系 nightly / legacy 轮子                                  | **永远直连**（三分法硬规则），无镜像                             |
| `Our Free Model 本地网关`（dsh-our-free-model 独立服务，HTTP） | 任务输出抽屉 AI 智能分析：模型列举 + 错误日志咨询 + 联网查证工具（FR-5.6~5.12） | 启动器安装并拉起（回环监听，默认 `127.0.0.1:18900`）；`/health` 免 Key 探活，其余接口 Bearer forwardKey；未就绪时引导安装（FR-5.6），不影响其它功能 |

---

## 7. 非功能需求

- **NFR-1 性能**：UI 线程不阻塞；日志渲染 5000 行内滚动流畅（虚拟化列表）；启动器冷启动 < 3s。
- **NFR-2 可用性**：界面中文；深色/浅色跟随系统；所有危险操作（停止/强杀）有状态反馈；报错信息给出可执行的下一步引导。
- **NFR-3 可靠性**：子进程输出读取异常不崩溃；配置损坏可重建。
- **NFR-4 安全/隐私**：无遥测、无数据上报。联网仅发生在环境与更新功能（FR-8）触发的操作中（git / uv / pip / winget / 轮子下载），全部为用户显式点击发起；其余功能不发起外网请求——停止（FR-4.2）与就绪探测仅操作本机进程/回环地址，不算外联。
- **NFR-5 可维护性**：MVVM 分层；服务层无 UI 依赖可单测；参数清单（附录 A）以数据驱动方式维护，便于跟随上游 ComfyUI 变更。

---

## 8. 风险与缓解

| 风险                                                                         | 影响                                      | 缓解                                                                                                             |
| ---------------------------------------------------------------------------- | ----------------------------------------- | ---------------------------------------------------------------------------------------------------------------- |
| ComfyUI 没有优雅停止端点（上游无 `/shutdown`）                     | 停止只能强杀，未保存的生成任务会中断      | 已核对官方路由文档与 `server.py`，停止直接强杀进程树并最多等 3 秒收尾；绝不用控制台信号（旧 AttachConsole+CTRL_BREAK 方案实测会打死启动器自身，已移除）                      |
| 上游 ComfyUI 参数变更                                                        | 参数页过时                                | 参数清单数据驱动（内置 JSON），发版时同步`cli_args.py`                                                         |
| 用户 venv 无 pip 且 uv 缺失                                                  | 无法装依赖                                | 报错引导安装 uv（winget install astral-sh.uv）                                                                   |
| uv venv 与上游 python_env 行为差异（embeddable 特有配置、rocm-sdk 落盘布局） | torch / 专用轮子在 .venv 下安装或运行异常 | 实现前先在 uv venv（Python 3.12）手工完整走通 install.bat 的轮子流程作为验证项；不通过则回退复刻 python_env 方案 |
| UAC 提权子进程与 GUI 的状态回传                                              | winget 安装结果不可知                     | 提权子进程以退出码 + 输出文件回传；超时视为失败并提示用户重新检测                                                |
| torch 更新中断留下残废环境                                                   | 无法启动 ComfyUI                          | 下载验证门（下载全成功才卸载）使网络类失败发生在卸载前；卸载后失败走尽力回滚（FR-8.5）                           |
| AMD nightly 旧 dev 版本从 index 下架                                         | 快照回滚 404                              | 回滚语义为「尽力恢复」：原版本可下则装回，下架则提示改装最新 nightly 恢复可运行                                  |
| wheel-catalog 钉死版本随上游策略过时                                         | 专用轮子装错版本                          | 清单仅随软件发版更新（用户已明确接受）；轮子页显示清单版本标识提示                                               |
| AMD nightly / legacy 源结构变更                                              | torch 更新失败                            | 失败信息透传原始错误；wheel-catalog.json 数据驱动可随发版更新                                                    |
| 第三方 GitHub 镜像失效                                                       | clone / 专用轮子下载失败                  | 镜像候选可自定义；失败时提示切换直连重试                                                                         |

---

## 附录 A：启动参数完整清单

来源：ComfyUI 上游 `comfy/cli_args.py`（2026-10 快照，`docs/cli_args_upstream.py`）。分组即页面分组；`[互斥N]` 表示同一 mutually exclusive 组。**加粗** = comfyui-rocm.bat 默认启用。

**网络**

| 参数                 | 类型          | 默认                           | 说明                        |
| -------------------- | ------------- | ------------------------------ | --------------------------- |
| --listen             | 可选值 IP     | 127.0.0.1（无值时 0.0.0.0,::） | 监听地址，逗号分隔多地址    |
| --port               | int           | 8188                           | 监听端口                    |
| --tls-keyfile        | str           | —                             | TLS 私钥（需配合 certfile） |
| --tls-certfile       | str           | —                             | TLS 证书                    |
| --enable-cors-header | 可选值 ORIGIN | 无（无值时 *）                 | 开启 CORS                   |
| --max-upload-size    | float         | 100                            | 上传大小上限 MB             |

**目录**

| 参数                                                      | 类型          | 说明                                                     |
| --------------------------------------------------------- | ------------- | -------------------------------------------------------- |
| --base-directory                                          | folderPath    | 覆盖 models/custom_nodes/input/output/temp/user 的基目录（主页「目录外置（启动参数）」卡同值维护，见 FR-1.5） |
| --extra-model-paths-config                                | filePathList  | 加载 extra_model_paths.yaml（多行 + 文件选择器，每行单独传参） |
| --output-directory / --temp-directory / --input-directory | folderPath    | 分别覆盖对应目录                                         |
| --user-directory / --models-directory                     | folderPath（须存在） | 覆盖 user / models 目录                                  |

**设备与后端**

| 参数                                                        | 类型         | 说明                                 |
| ----------------------------------------------------------- | ------------ | ------------------------------------ |
| --cuda-device                                               | str          | 可见设备 id 列表或 all               |
| --default-device                                            | int          | 默认设备 id                          |
| --cuda-malloc / --disable-cuda-malloc                       | flag [互斥1] | cudaMallocAsync 开关                 |
| --directml                                                  | 可选 int     | torch-directml（本项目不用，仅枚举） |
| --oneapi-device-selector                                    | str          | oneAPI 设备（同上）                  |
| --supports-fp8-compute                                      | flag         | 声称支持 fp8 计算                    |
| --enable-triton-backend /**--disable-triton-backend** | flag         | comfy-kitchen Triton 后端开关        |

**精度**

| 参数                                                                                                             | 类型         | 说明                        |
| ---------------------------------------------------------------------------------------------------------------- | ------------ | --------------------------- |
| --force-fp32 / --force-fp16                                                                                      | flag [互斥2] | 强制全局精度                |
| --fp32-unet / --fp64-unet / --bf16-unet / --fp16-unet / --fp8_e4m3fn-unet / --fp8_e5m2-unet / --fp8_e8m0fnu-unet | flag [互斥3] | UNet 精度                   |
| --fp16-vae / --fp32-vae / --bf16-vae                                                                             | flag [互斥4] | VAE 精度                    |
| --cpu-vae                                                                                                        | flag         | VAE 跑 CPU                  |
| --fp8_e4m3fn-text-enc / --fp8_e5m2-text-enc / --fp16-text-enc / --fp32-text-enc / --bf16-text-enc                | flag [互斥5] | 文本编码器精度              |
| --fp16-intermediates                                                                                             | flag         | 节点间中间张量 fp16（实验） |
| --force-channels-last                                                                                            | flag         | channels_last 推理          |

**缓存与内存**

| 参数                              | 类型                     | 说明                              |
| --------------------------------- | ------------------------ | --------------------------------- |
| --cache-ram                       | 0~2 个 float             | RAM 压力缓存（默认模式），阈值 GB |
| --cache-classic / --cache-none    | flag [互斥6]             | 旧式缓存 / 不缓存                 |
| **--cache-lru**             | int [互斥6]（bat 值 20） | LRU 缓存 N 条结果                 |
| --high-ram                        | flag [互斥6]             | 高内存模式                        |
| --reserve-vram                    | float                    | 为系统保留显存 GB                 |
| --vram-headroom                   | float                    | DynamicVRAM 额外余量 GB           |
| --disable-nvml-pressure           | flag                     | 用 CUDA 代替 NVML 测压            |
| **--disable-smart-memory**  | flag                     | 激进 offload 到内存               |
| **--disable-pinned-memory** | flag                     | 禁用 pinned memory                |

**显存模式**

| 参数                                                   | 类型                  | 说明             |
| ------------------------------------------------------ | --------------------- | ---------------- |
| --gpu-only / --highvram / --lowvram / --novram / --cpu | flag [互斥7]          | 显存使用档位     |
| --enable-dynamic-vram / --disable-dynamic-vram         | flag（bat 用 enable） | 动态显存         |
| --async-offload                                        | 可选 int（默认2）     | 异步权重卸载流数 |
| --disable-async-offload                                | flag                  | 关异步卸载       |
| --fast-disk / --disable-fast-disk                      | flag                  | 磁盘后备动态加载 |

**注意力**

| 参数                                                                                                                                                         | 类型         | 说明                                            |
| ------------------------------------------------------------------------------------------------------------------------------------------------------------ | ------------ | ----------------------------------------------- |
| --use-split-cross-attention / --use-quad-cross-attention / --use-pytorch-cross-attention / --use-sage-attention / --use-flash-attention / --use-ck-attention | flag [互斥8] | 注意力实现（bat：RDNA1/2 用 quad，RDNA4 用 ck） |
| --disable-xformers                                                                                                                                           | flag         | 禁用 xformers                                   |
| --force-upcast-attention / --dont-upcast-attention                                                                                                           | flag [互斥9] | 注意力 upcast                                   |

**Manager 与节点**

| 参数                                                       | 类型          | 说明                             |
| ---------------------------------------------------------- | ------------- | -------------------------------- |
| **--enable-manager**                                 | flag          | 启用 ComfyUI-Manager             |
| --disable-manager-ui /**--enable-manager-legacy-ui** | flag [互斥10] | Manager UI 形态                  |
| --disable-all-custom-nodes                                 | flag          | 禁用全部自定义节点               |
| --whitelist-custom-nodes                                   | str（多个）   | 白名单节点                       |
| --disable-partner-nodes                                    | flag          | 禁用付费 API 节点                |
| --offline                                                  | flag          | 前端断网（CSP）                  |
| **--disable-api-nodes**                              | flag          | --offline 的废弃别名（bat 在用） |

**性能与编译**

| 参数                                                                     | 类型                                                                      | 说明                               |
| ------------------------------------------------------------------------ | ------------------------------------------------------------------------- | ---------------------------------- |
| --disable-cuda-graphs / --disable-comfy-compiler / --assert-graph-breaks | flag                                                                      | CUDA graph / 编译器控制            |
| --force-non-blocking                                                     | flag                                                                      | 非阻塞张量操作                     |
| --fast                                                                   | 0~n 个枚举（fp16_accumulation / fp8_matrix_mult / cublas_ops / autotune） | 实验性优化                         |
| --deterministic                                                          | flag                                                                      | 确定性算法                         |
| --debug-hang                                                             | flag                                                                      | Ctrl-C 时 dump 栈                  |
| --mmap-torch-files / --disable-mmap                                      | flag                                                                      | ckpt/pt mmap / safetensors 禁 mmap |

**日志与其他**

| 参数                                     | 类型                       | 说明                                              |
| ---------------------------------------- | -------------------------- | ------------------------------------------------- |
| --verbose                                | 可重复；LEVEL [FILE]       | 日志级别 DEBUG/DETAIL/INFO/WARNING/ERROR/CRITICAL |
| --log-stdout                             | flag                       | 输出走 stdout                                     |
| --dont-print-server                      | flag                       | 不打印服务输出                                    |
| --quick-test-for-ci                      | flag                       | CI 快测                                           |
| --windows-standalone-build               | flag                       | 独立版便利行为                                    |
| --auto-launch / --disable-auto-launch    | flag                       | 自动开浏览器（启动器不代开，唯一入口）            |
| --disable-metadata                       | flag                       | 不存 prompt 元数据                                |
| --multi-user                             | flag                       | 多用户存储                                        |
| --front-end-version                      | str                        | 前端版本 repoOwner/repoName@version               |
| --front-end-root                         | str（须存在）              | 本地前端目录                                      |
| --enable-compress-response-body          | flag                       | 压缩响应体                                        |
| --comfy-api-base                         | str                        | API base URL                                      |
| --database-url                           | str                        | 数据库 URL                                        |
| --enable-assets / --enable-asset-hashing | flag                       | 资产系统                                          |
| --feature-flag                           | KEY[=VALUE]（可多次）      | 服务器特性开关                                    |
| --list-feature-flags                     | flag                       | 打印特性开关并退出                                |
| --default-hashing-function               | md5/sha1/sha256/sha512     | 文件哈希算法                                      |
| --preview-method                         | none/auto/latent2rgb/taesd | 采样预览方式                                      |
| --preview-size                           | int（默认512）             | 预览尺寸                                          |

> 实现方式：固化为 Core 内嵌资源 `Resources/cli-args.json`（EmbeddedResource，见 3.3）驱动 UI，发版时对照上游 `cli_args.py` 更新。

## 附录 B：GPU 模板定义

**B.1 三模板共有环境变量**

```
COMFYUI_ENABLE_MIOPEN=0
FLASH_ATTENTION_TRITON_AMD_ENABLE=TRUE
MIOPEN_FIND_ENFORCE=1
MIOPEN_FIND_MODE=2
MIOPEN_DEBUG_DISABLE_FIND_DB=0
MIOPEN_SEARCH_CUTOFF=1
MIOPEN_ENABLE_LOGGING=0
MIOPEN_LOG_LEVEL=0
MIOPEN_ENABLE_LOGGING_CMD=0
TRITON_PRINT_AUTOTUNING=0
TRITON_CACHE_AUTOTUNING=0
TRITON_CACHE_DIR=<ComfyUI目录>\triton-cache
PYTORCH_TUNABLEOP_CACHE_DIR=<ComfyUI目录>\tunableop-cache
MIOPEN_SYSTEM_DB_PATH=<venv>\Lib\site-packages\_rocm_sdk_devel\bin
ROCBLAS_TENSILE_DB_PATH=<venv>\Lib\site-packages\_rocm_sdk_devel\bin\rocblas
ROCBLAS_TENSILE_LIBPATH=<venv>\Lib\site-packages\_rocm_sdk_devel\bin\rocblas\library
```

（RDNA3/gfx1100 按原脚本不设 MIOPEN_SYSTEM_DB_PATH 与 ROCBLAS_* 三项——模板应用时按对应代际处理。）

**B.2 RDNA1/2（gfx101x / gfx103x）**

- 追加环境变量：`TORCH_BACKENDS_CUDA_FLASH_SDP_ENABLED=0`、`TORCH_BACKENDS_CUDA_MEM_EFF_SDP_ENABLED=0`、`TORCH_BACKENDS_CUDA_MATH_SDP_ENABLED=1`
- 参数：基础参数组 + `--use-quad-cross-attention`

**B.3 RDNA3（gfx110x）**

- 追加环境变量：`TORCH_ROCM_AOTRITON_ENABLE_EXPERIMENTAL=1`
- 参数：基础参数组

**B.4 RDNA4（gfx120x）**

- 追加环境变量：`TORCH_ROCM_AOTRITON_ENABLE_EXPERIMENTAL=1`
- 参数（按原脚本整组替换）：`--enable-manager --enable-manager-legacy-ui --use-ck-attention`

**B.5 基础参数组**（RDNA1/2、RDNA3 模板用）

```
--disable-api-nodes --cache-lru 20 --disable-smart-memory --disable-pinned-memory
--enable-manager --enable-manager-legacy-ui --disable-triton-backend --enable-dynamic-vram
```

## 附录 C：参考脚本 → 功能映射

| 脚本                              | SRS 条目                                                                                                                                      |
| --------------------------------- | --------------------------------------------------------------------------------------------------------------------------------------------- |
| comfyui-rocm.bat                  | FR-3（环境变量）、FR-4.1（启动序列）、FR-7（GPU 模板）、附录 B                                                                                |
| install.bat（1-7 步 Python 环境） | 部分迁移：FR-8.3（以 uv venv --python 3.12 替代 embeddable python_env；setuptools/wheel/mpmath 补装保留）。rocm_kpack.dll 修复步骤并入 FR-8.5 |
| install.bat（GPU 检测分支）       | FR-8.2（改为 WMI 内置映射）、FR-8.5（arch 分支安装）                                                                                          |
| install.bat（custom_nodes clone） | 不迁移（custom_nodes 管理不在范围）                                                                                                           |
| install.bat（专用轮子）           | FR-8.6（wheel-catalog.json 内置清单）                                                                                                         |
| comfyui-rocm-updater.bat          | FR-8.4（核心仓库检查/热更新；仓库地址改为用户指定）；custom_nodes 更新与 sageattention 更新分别不迁移 / 移至 FR-8.6                           |
| rocm-pytorch-package-updater.bat  | FR-8.5（卸载→重装流程 + 快照回滚增强）                                                                                                       |
| comfyui-rocm.bat（追踪包同步）    | 不迁移（v1.3 已移除；依赖同步统一归 FR-8.6 项目依赖）                                                                                         |

# 从源码构建 EasyShare

以下步骤在 Windows x64 的 PowerShell 中执行，工作目录为仓库根目录。发布包用户只需解压运行，无需安装这些开发工具。

## 1. 准备构建工具

| 工具 | 当前要求 | 用途 |
| --- | --- | --- |
| .NET SDK | 8.0.202，或同一 8.0.2xx 功能带内更新的补丁版本 | 编译 WPF 客户端和 C# 检查程序 |
| Eclipse Temurin JDK | Windows x64，25.0.3；其他 JDK 版本未作为发布基线验证 | 编译 Java 桥接，并用 `jlink` 生成随包运行环境 |
| Node.js | 18 或更新版本，需提供全局 `fetch` | 下载依赖、执行播放器边界检查 |
| PowerShell | Windows 自带 PowerShell 或 PowerShell 7 | 构建和打包 |
| Edge WebView2 Runtime | 运行客户端、执行播放器检查时需要 | HDC 内嵌播放器 |

下载入口：[.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)、[Eclipse Temurin](https://adoptium.net/temurin/releases/)、[Node.js](https://nodejs.org/)、[WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/)。

当前 [`global.json`](../global.json) 使用 `8.0.202` 与 `rollForward: latestPatch`，**仅安装 8.0.3xx / 8.0.4xx SDK 不满足这个选择规则**。可并行安装指定功能带的 SDK；如果自行调整 SDK 策略，需要重新验证构建。规则见 [Microsoft global.json 文档](https://learn.microsoft.com/dotnet/core/tools/global-json#rollforward)。

先确认工具能在终端中找到：

```powershell
dotnet --version
node --version
$jdkPath = 'C:\path\to\jdk-25.0.3'
& (Join-Path $jdkPath 'bin\javac.exe') -version
& (Join-Path $jdkPath 'bin\jlink.exe') --version
```

将 `$jdkPath` 替换成自己的 JDK 目录。构建脚本使用该参数寻找 `javac.exe` 和 `jlink.exe`，不依赖 `JAVA_HOME`。

## 2. 准备 HOScrcpy SDK

`tools/` 不提交到源码仓库。当前构建脚本要求先准备 `hosScrcpy-1.0.18-beta.jar`，不会自动下载该文件。

1. 从 [HOScrcpy 上游](https://gitcode.com/OpenHarmonyToolkitsPlaza/HOScrcpy)或依赖清单记录的 [dsh-hos-scrcpy 提供方](https://github.com/ns-zzj/dsh-hos-scrcpy/tree/main/resources)取得 `hosScrcpy-1.0.18-beta.jar`。
2. 在仓库根目录创建 `tools/hosscrcpy/`，将 JAR 放入其中。文件名及完整位置必须为 `tools/hosscrcpy/hosScrcpy-1.0.18-beta.jar`。
3. 执行下面的摘要检查。文件不匹配时应重新核对来源，不要跳过检查继续构建。

```powershell
$dependencyManifest = Get-Content -Raw .\DEPENDENCIES.json | ConvertFrom-Json
$jarRecord = $dependencyManifest.files |
    Where-Object { $_.path -eq 'tools/hosscrcpy/hosScrcpy-1.0.18-beta.jar' }
$jarHash = (Get-FileHash -LiteralPath $jarRecord.path -Algorithm SHA256).Hash
if ($jarHash -ne $jarRecord.sha256) {
    throw 'HOScrcpy SDK 的 SHA-256 与 DEPENDENCIES.json 不符。'
}
```

发布基线的 JAR 为 18,442,278 字节，SHA-256 为：

```text
2ce60b1893cc2be471a8cd7eab7e4127e41921f1f89a32a134c730ba15c8c0fd
```

提供方 Git blob 为 `ee3970d79d1bc36b5ad1b068d2def9c7befa7dc4`。完整版本、取得位置和第三方声明见 [`DEPENDENCIES.json`](../DEPENDENCIES.json) 与 [`THIRD_PARTY_NOTICES.md`](../THIRD_PARTY_NOTICES.md)。

## 3. 构建并打包

```powershell
.\scripts\build.ps1 -JdkPath $jdkPath
```

首次构建需要访问 GitHub、NuGet 和华为云的 OpenHarmony SDK 下载源。HDC 下载脚本流式读取较大的 SDK 归档，只保存 Windows 工具链 ZIP；首次下载可能耗时。构建脚本会依次：

1. 检查 HOScrcpy JAR 是否存在。
2. 补齐 HDC 工具、裁剪后的 Java 运行环境和官方 scrcpy 5.0.1 Windows x64 包。
3. 编译 `bridge/EasyShareBridge.java`。
4. 执行 JavaScript 播放器检查及 C# 设备枚举、设置边界检查。
5. 下载 .NET 8.0.32 运行包，恢复 WebView2 SDK 等 NuGet 依赖，执行 Windows x64 自包含发布。
6. 复制运行组件、第三方声明与 `docs/` 文档，并调用 `scripts/package.ps1` 生成 ZIP。

输出：

```text
dist/
├─ EasyShare/                  # 完整可运行目录
│  ├─ EasyShare.exe
│  ├─ Player/
│  ├─ docs/
│  └─ tools/
└─ EasyShare-win-x64.zip       # 分发包
```

运行：

```powershell
.\dist\EasyShare\EasyShare.exe
```

必须保留完整发布目录，不能只复制 EXE。发布包已包含 .NET 和 Java 运行环境；Edge WebView2 Runtime 由系统独立提供。

### 缓存和重新构建

构建缓存位于 `tools/downloads/`、`.nuget/`、`.dotnet/`。现有 `tools/hdc`、`tools/jre`、`tools/scrcpy` 会被复用；单纯更改 `-JdkPath` 不会重建已经存在的裁剪 JRE。更换依赖或 JDK 时，先将对应旧目录移到仓库外备份，再重新构建和核验版本。

`build.ps1` 会发布到现有 `dist/EasyShare`；`package.ps1` 会打包其中除 `selftest/` 外的全部文件。发布前应使用干净的输出目录，避免把自己放入其中的日志或其他文件一同打包。

scrcpy 下载脚本自动校验发行 ZIP 的 SHA-256。HOScrcpy 需要执行上面的手动检查；HDC 等文件的已知摘要在依赖清单中。当前脚本不为所有缓存组件执行统一的重新校验。

## 4. 分层验证

### 不连接平板的源码检查

```powershell
node .\tests\decoder.test.cjs
dotnet run --project .\tests\EasyShare.Checks -c Release
```

前者检查 H.264 参数集、关键帧和解码积压边界；后者检查 USB 设备枚举和投屏设置。构建脚本已包含这两项。

### 发布版界面与合成视频检查

```powershell
& .\dist\EasyShare\EasyShare.exe --discovery-test
& .\dist\EasyShare\EasyShare.exe --ui-test
& .\dist\EasyShare\EasyShare.exe --self-test
```

每次等待程序退出后再执行下一项。`--discovery-test` 枚举当前设备、检查控件状态并保存窗口渲染图，不启动投屏；`--self-test` 使用明确标注的合成视频检查 Java / WebView2 播放链路，不连接平板。

0.5.0 新增 `--ui-test`：在默认 1280 × 900 和最小 1060 × 700 的 WPF 窗口逻辑尺寸下，逐一检查画面、声音、键盘三个页签，确认启停按钮在窗口内、切页和调整窗口不改变设置；另检查声音开关与音频码率、平板输入法开关、画质与视频码率读回，共 9 项。它恢复原有设置，保存 `selftest/ui-result.json` 和各页签自渲染截图，然后退出。该检查程序化操作 WPF 控件，不启动投屏，也不替代真实鼠标交互或平板输入验证。

### 已授权 USB 设备检查

```powershell
& .\dist\EasyShare\EasyShare.exe --usb-test --quality=clear --video-bitrate=20 --audio --audio-bitrate=192 --keyboard=uhid
& .\dist\EasyShare\EasyShare.exe --refresh-test
```

同样逐项运行。`--usb-test` 会实际启动设备投屏，等待视频和已启用的音频输出就绪，再运行 8 秒并停止；这不替代声音听测、文字输入和鼠标动作检查。`--refresh-test` 需要稳定连接的已授权设备，检查三轮自动检测期间的控件状态及手动刷新，不启动投屏。

可选设置：

| 参数 | 含义 |
| --- | --- |
| `--quality=clear` | 使用原画清晰档 |
| `--video-bitrate=20` | 视频目标码率 Mbps；0 表示自动，其他值按界面支持范围选择 |
| `--audio` / `--no-audio` | 开启 / 关闭声音传输 |
| `--audio-bitrate=192` | 音频目标码率 kbps |
| `--keyboard=uhid` / `--keyboard=sdk` | 平板输入法 / 兼容键盘模式 |

诊断结果保存在运行 EXE 旁的 `selftest/`，可能含设备序列号。打包脚本排除该目录。编译、合成视频、设备启动和人工操作各自覆盖不同范围；历史实测及待测项目见 [`VALIDATION.md`](../VALIDATION.md)。

## 常见构建问题

| 现象 | 检查方式 |
| --- | --- |
| 找不到兼容的 .NET SDK | 在仓库根目录执行 `dotnet --list-sdks`，核对 `global.json` 的功能带要求 |
| 提示先准备 HOScrcpy SDK | 核对 JAR 路径、文件名和 SHA-256 |
| 提示找不到 `fetch` | 使用 Node.js 18 或更新版本，重新打开终端确认 `node --version` |
| NuGet 或依赖下载失败 | 检查对应官方源的连通性；若产生了不完整下载文件，只移走该缓存文件后重试 |
| 运行 EXE 时找不到工具 | 保留完整 `dist/EasyShare`；不要只移动 EXE |
| WebView2 无法初始化 | 安装 Windows x64 的 Edge WebView2 Runtime 后重试 |

当前源码文档按现有构建脚本核对；在全新 Windows 环境从零下载全部依赖的复现验证，仍应作为独立检查记录，不能由现有开发机发布成功推断。

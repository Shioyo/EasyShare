# 第三方组件

EasyShare 的桌面界面、进程管理、播放器和视频桥接源代码保存在本项目中。

## scrcpy 5.0.1 / ADB / FFmpeg / SDL / libusb

使用 Genymobile 官方 Windows x64 完整发行包：https://github.com/Genymobile/scrcpy/releases/tag/v5.0.1 。包的 SHA-256 同时与官方 GitHub asset digest 及 SHA256SUMS.txt 一致，具体文件摘要见 `DEPENDENCIES.json`。完整保留官方包的 `tools/scrcpy/LICENSE.txt`（Apache 2.0）及随包文件；上游第三方构建来源和许可证见 https://github.com/Genymobile/scrcpy/blob/v5.0.1/doc/build.md 。未执行 GPG 签名验证。

## HOScrcpy 1.0.18-beta

- 上游：https://gitcode.com/OpenHarmonyToolkitsPlaza/HOScrcpy
- 本次二进制取得位置：https://github.com/ns-zzj/dsh-hos-scrcpy/tree/main/resources
- SDK 的 SHA-256 和 Git blob 标识记录在 `DEPENDENCIES.json`。
- SDK 调用方式参考其 API 文档及 ns-zzj/dsh-hos-scrcpy 的 `Dev/src/Main.java`。没有沿用该桥接程序的控制端点、端口清理、GOP 缓存或 WebSocket 实现。
- 上游仓库标注 MIT，但 SDK 版权人字段是未填写的模板；保留提供方的完整第三方声明于 `licenses/HOScrcpy-provider-notices.md`，以及参考项目的 MIT 许可于 `licenses/dsh-hos-scrcpy-LICENSE.txt`。SDK 内附的其他许可证仍保留在 JAR 中。

## OpenHarmony HDC 3.2.0b / libusb

取得自 OpenHarmony 6.0 Release 的官方 SDK，包内元数据版本为 6.0.0.47，具体文件见 `DEPENDENCIES.json`。保留工具包原始 `tools/hdc/NOTICE.txt` 和 `oh-uni-package.json`。

## Eclipse Temurin OpenJDK 25.0.3

使用本机 Eclipse Adoptium JDK 的 jlink 生成裁剪运行环境。运行环境的许可证全文位于 `tools/jre/legal/`，构建来源见 `tools/jre/release`。原始 NOTICE 保存在 `licenses/Temurin-NOTICE.txt`。上游：https://adoptium.net/ ，源码：https://github.com/adoptium/temurin25-binaries/releases 。OpenJDK 适用 GPLv2 with Classpath Exception，详见所附原文。

## Microsoft .NET 8.0.32 / WebView2 SDK 1.0.3537.50

.NET 运行时的许可证和第三方通知保存在 `licenses/dotnet-LICENSE.txt`、`licenses/dotnet-THIRD-PARTY-NOTICES.txt`、`licenses/WindowsDesktop-LICENSE.txt`。WebView2 SDK 的 LICENSE 和 NOTICE 位于 `licenses/WebView2-LICENSE.txt`、`licenses/WebView2-NOTICE.txt`。Edge WebView2 运行时由系统独立提供，不随本包再分发。

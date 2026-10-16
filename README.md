# _!!!项目由ChatGPT独立完成_

<p align="center">
  <img src="docs/assets/icon.png" width="96" alt="EasyShare 图标">
</p>

<h1 align="center">EasyShare</h1>

<p align="center"> 将Android / HarmonyOS 设备的画面，通过 USB 带到 Windows。</p>

<p align="center">
  <a href="https://github.com/Shioyo/EasyShare/releases">下载 Releases</a> ·
  <a href="#快速开始">快速开始</a> ·
</p>

![EasyShare Windows 主界面](docs/assets/screenshot.png)



## 快速开始

准备 Windows 10/11 x64、一条支持数据传输的 USB 线，以及允许 USB 调试的设备。电脑需要 [Microsoft Edge WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/)；发布包自带 .NET、Java 和连接工具。

1. 在本仓库 [Releases](https://github.com/Shioyo/EasyShare/releases) 页面选择 `EasyShare-win-x64.zip`，完整解压后运行 `EasyShare/EasyShare.exe`。不要只移动 EXE；GitHub 的 Source code 压缩包需要自行构建。
2. 在平板的“关于平板”中按系统提示开启开发者选项，再打开 **USB 调试**。华为设备可参考[官方说明](https://consumer.huawei.com/cn/support/content/zh-cn00407281/)。
3. 用数据线连接电脑，解锁平板，在平板上允许这台电脑调试。
4. 等待 EasyShare 显示 **已连接**，在 **画面 / 声音 / 键盘** 页签按需调整设置，点击 **开始投屏**。也可以手动刷新设备。
5. ADB 画面会出现在独立的 **EasyShare · 平板画面** 窗口；关闭画面窗口或点击主界面的 **结束投屏** 即可停止。

更改画质、码率、声音或中文输入选项后，需要结束并重新开始投屏，设置才会用于新会话。

## 第三方组件

EasyShare 基于 [scrcpy](https://github.com/Genymobile/scrcpy)、[HOScrcpy](https://gitcode.com/OpenHarmonyToolkitsPlaza/HOScrcpy)、OpenHarmony HDC、Eclipse Temurin、.NET 和 WebView2 构建。ADB 使用 scrcpy 5.0.1；HDC 使用 HOScrcpy 1.0.18-beta。

组件来源、固定版本与已记录的摘要见 [DEPENDENCIES.json](DEPENDENCIES.json)，许可与再分发说明见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) 和 [licenses/](licenses/)。本项目并非华为或上述组件作者的官方产品。

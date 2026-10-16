param([Parameter(Mandatory=$true)][string]$JdkPath)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
Set-Location -LiteralPath $projectRoot
$env:DOTNET_CLI_HOME = Join-Path $projectRoot '.dotnet'
$env:NUGET_PACKAGES = Join-Path $projectRoot '.nuget'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
if (!(Test-Path 'tools/hosscrcpy/hosScrcpy-1.0.18-beta.jar')) {
    throw '请先按 DEPENDENCIES.json 准备并校验 HOScrcpy SDK。'
}
if (!(Test-Path 'tools/hdc/hdc.exe')) {
    node -e "eval(require('fs').readFileSync('scripts/fetch-hdc.cjs','utf8'))"
    if ($LASTEXITCODE -ne 0) {
        throw 'HDC 下载失败'
    }
    Expand-Archive -LiteralPath 'tools/downloads/toolchains.zip' -DestinationPath 'tools/downloads/toolchains' -Force
    New-Item -ItemType Directory -Force 'tools/hdc' | Out-Null
    Copy-Item 'tools/downloads/toolchains/toolchains/hdc.exe','tools/downloads/toolchains/toolchains/libusb_shared.dll','tools/downloads/toolchains/toolchains/NOTICE.txt','tools/downloads/toolchains/toolchains/oh-uni-package.json' 'tools/hdc'
}
if (!(Test-Path 'tools/jre/bin/java.exe')) {
    & (Join-Path $JdkPath 'bin/jlink.exe') --add-modules java.base,java.compiler,java.desktop,java.naming,java.sql,jdk.unsupported,jdk.crypto.ec --strip-debug --no-header-files --no-man-pages --compress zip-6 --output tools/jre
    if ($LASTEXITCODE -ne 0) {
        throw 'Java 运行环境构建失败'
    }
}
if (!(Test-Path 'tools/scrcpy/scrcpy.exe')) {
    node -e "eval(require('fs').readFileSync('scripts/fetch-scrcpy.cjs','utf8'))"
    if ($LASTEXITCODE -ne 0) {
        throw 'scrcpy 下载失败'
    }
    Expand-Archive -LiteralPath 'tools/downloads/scrcpy-win64-v5.0.1.zip' -DestinationPath 'tools/downloads/scrcpy' -Force
    New-Item -ItemType Directory -Force 'tools/scrcpy' | Out-Null
    Copy-Item 'tools/downloads/scrcpy/scrcpy-win64-v5.0.1/*' 'tools/scrcpy' -Recurse -Force
}
New-Item -ItemType Directory -Force 'tools/bridge' | Out-Null
& (Join-Path $JdkPath 'bin/javac.exe') --release 8 -Xlint:-options -encoding UTF-8 -cp tools/hosscrcpy/hosScrcpy-1.0.18-beta.jar -d tools/bridge bridge/EasyShareBridge.java
if ($LASTEXITCODE -ne 0) {
    throw '桥接程序编译失败'
}
node -e "eval(require('fs').readFileSync('tests/decoder.test.cjs','utf8'))"
if ($LASTEXITCODE -ne 0) {
    throw '播放器边界检查失败'
}
dotnet run --project tests/EasyShare.Checks -c Release
if ($LASTEXITCODE -ne 0) {
    throw '连接逻辑检查失败'
}
node -e "eval(require('fs').readFileSync('scripts/fetch-runtime.cjs','utf8'))"
if ($LASTEXITCODE -ne 0) {
    throw '.NET 运行包下载失败'
}
dotnet publish src/EasyShare/EasyShare.csproj -c Release -r win-x64 --self-contained true -o dist/EasyShare -p:DebugType=None -p:DebugSymbols=false --source tools/downloads/nuget --source https://api.nuget.org/v3/index.json
if ($LASTEXITCODE -ne 0) {
    throw 'Windows 客户端发布失败'
}
New-Item -ItemType Directory -Force 'dist/EasyShare/tools' | Out-Null
Copy-Item -LiteralPath 'tools/hdc','tools/jre','tools/bridge','tools/hosscrcpy','tools/scrcpy' -Destination 'dist/EasyShare/tools' -Recurse -Force
Copy-Item -LiteralPath 'README.md','THIRD_PARTY_NOTICES.md','DEPENDENCIES.json','VALIDATION.md','licenses' -Destination 'dist/EasyShare' -Recurse -Force
Copy-Item -LiteralPath 'docs' -Destination 'dist/EasyShare' -Recurse -Force
& (Join-Path $PSScriptRoot 'package.ps1')
Write-Output '已发布 dist/EasyShare/EasyShare.exe 和 dist/EasyShare-win-x64.zip'


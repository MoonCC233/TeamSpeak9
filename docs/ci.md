# 持续集成与安装包构建

三条相互独立的流水线，分别对应仓库里的三个可交付组件。每条都带 `paths` 过滤，
只在自己那部分代码变动时触发，也都支持在 Actions 页面手动 `workflow_dispatch`。

| 流水线 | 文件 | Runner | 产物 |
|---|---|---|---|
| Desktop CI | [`.github/workflows/desktop.yml`](../.github/workflows/desktop.yml) | `windows-latest` | zip 便携包 + Inno Setup 安装程序 |
| Android CI | [`.github/workflows/android.yml`](../.github/workflows/android.yml) | `ubuntu-latest` | debug APK（+ 配了 secrets 时的已签名 release APK） |
| Stream Service CI | [`.github/workflows/stream-service.yml`](../.github/workflows/stream-service.yml) | `ubuntu-latest` | linux/amd64、linux/arm64、windows/amd64 三个静态二进制 |

每条流水线都会为自己的产物生成 `SHA256SUMS.txt`。产物保留在 workflow run 的
Artifacts 区，不会自动发布到 Releases。

## 1. Desktop CI

### 1.1 流程

版本号校验 → restore → build → test → vcpkg 建 libopus → 自包含 publish →
zip 便携包 → 装简中语言文件 → ISCC 编译安装包 → 校验和 → 上传。

### 1.2 版本号只有一个来源

产品版本的权威来源是 [`client/desktop/Directory.Build.props`](../client/desktop/Directory.Build.props)
的 `<Version>`。

[`TeamSpeak9.iss`](../client/desktop/packaging/TeamSpeak9.iss) 刻意不使用 ISPP
预处理指令（`#define` / `{#Var}`），好让只装了 Inno Setup 基础组件的机器也能直接
编译它。代价是 `AppVersion` 和 `VersionInfoVersion` 只能写死，所以流水线里有一步
`Resolve and verify product version` 会比对三者，不一致就直接失败。

**改版本号时要同时改三处**：`Directory.Build.props` 的 `<Version>`、`.iss` 的
`AppVersion` 与 `VersionInfoVersion`。

### 1.3 libopus 现场构建

`libopus.dll` 属于原生二进制，按
[`third_party/native/README.md`](../client/desktop/third_party/native/README.md)
的约定不入库。流水线用 runner 预装的 vcpkg 现场构建 `opus:x64-windows`，再把
`opus.dll` 改名成 `libopus.dll`（TSLib 的 `[DllImport("libopus")]` 要求这个名字）
放进 `third_party/native/win-x64/`。

这一步**必须在 publish 之前**：csproj 里那条 `<None Include>` 带 `Exists()` 条件，
文件在不在是 publish 求值那一刻才决定的。

vcpkg 这步**故意容错**——挂了只 warn 不中断，写一个 `built=false` 的 output 后
继续，最终产出一个不含语音支持的包，其余功能仍可测试。反过来，如果
`built=true` 却没能 staging 到 `lib/x64/`，publish 步骤会主动 throw，用来守住
csproj 里那条容易被误删的 `<None Include>`。

### 1.4 简体中文安装向导

`ChineseSimplified.isl` 是 Inno Setup 的**非官方**翻译，不随安装程序附带。
流水线从 `jrsoftware/issrc` 拉取，tag 由 `ISCC.exe` 的 `ProductVersion` 推导
（`6.7.1` → `is-6_7_1`），这样 runner 镜像升级 Inno 时能自动跟上；推导失败则退回
`env.ISSRC_FALLBACK_TAG`。下载后会检查内容是否含 `[LangOptions]` 与
`LanguageName`，避免把 404 页面当成语言文件用。

### 1.5 安装程序行为

- `AppId` 是永久 GUID，升级安装与卸载项都依赖它，**切勿修改**。
- 默认按管理员装到 `Program Files`，但开了
  `PrivilegesRequiredOverridesAllowed=dialog`，用户可在向导里改成仅为自己安装。
- `MinVersion=10.0.19041`：App 的目标框架是 `net8.0-windows10.0.19041.0`，
  屏幕共享用的 Windows.Graphics.Capture 也要求 Win10 2004 以上。
- 卸载时**故意保留** `%APPDATA%\TeamSpeak9`（日志、图标缓存、设置）。
- 仓库里没有 `.ico` 素材，所以不设 `SetupIconFile`，安装程序用 Inno 的默认图标。

### 1.6 本地复现

```powershell
dotnet publish client/desktop/src/TeamSpeak9.App/TeamSpeak9.App.csproj `
  -c Release -r win-x64 --self-contained true `
  -o client/desktop/artifacts/publish
Copy-Item NOTICE client/desktop/artifacts/publish/NOTICE.txt
& "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" client\desktop\packaging\TeamSpeak9.iss
```

## 2. Android CI

### 2.1 三个绕不开的前提

- **`-x buildRustLibs`**。`buildRustLibs` 是个 `Exec` 任务，指向并不存在的
  `${rootDir}/../tslib_multi`（tslib 源码不可得，submodule 里只有预编译的
  `libtslib_jni.so`）。必须跳过，jniLibs 本身已随 submodule 入库。
- **不跑 `./gradlew test`**。`client/mobile` 只有 `main` 一个 source set，
  没有 `test` / `androidTest`，跑了也是空转。
- **不复用 submodule 的 `signingConfigs`**。
  [`app/build.gradle.kts`](../client/mobile/app/build.gradle.kts) 把
  storePassword / keyPassword 硬编码成了 `ts6droid`，且整段被
  `if (file("${rootDir}/release.keystore").exists())` 包住——只要往那个路径放
  keystore，Gradle 就会拿错口令去开它。所以流水线先出 unsigned APK，再用
  `zipalign` + `apksigner` 单独签名，这样任意口令的 keystore 都能用，也不必改
  submodule。

`settings.gradle.kts` 里的阿里云镜像被 `GITHUB_ACTIONS != "true"` 包住，
CI 上会自动走 `google()` / `mavenCentral()`，无需干预。

### 2.2 release 签名

配齐下列仓库 secrets 才会额外产出已签名的 release APK；缺任何一个就只出 debug APK。

| Secret | 说明 |
|---|---|
| `ANDROID_KEYSTORE_BASE64` | keystore 文件的 base64（`base64 -w0 my.jks`） |
| `ANDROID_KEY_ALIAS` | 密钥别名 |
| `ANDROID_KEYSTORE_PASSWORD` | keystore 口令 |
| `ANDROID_KEY_PASSWORD` | 密钥口令，与 keystore 口令相同时可省略 |

实现上有两个细节：`secrets` 上下文在 step 的 `if:` 里不可用，所以先由一个
`Detect signing secrets` 步骤把「有没有配」落成 step output；keystore 只解码到
`$RUNNER_TEMP`，绝不落进工作区，签完即 `rm -f`，口令则全程走
`--ks-pass env:` 而不出现在进程命令行里。

### 2.3 已知限制

jniLibs 只有 `arm64-v8a` 与 `x86_64`，32 位设备装上会崩。这是上游 submodule
的现状，不在 CI 的处理范围内。

## 3. Stream Service CI

### 3.1 cgo 开关要反着来两次

服务本身不使用 cgo，交叉编译要 `CGO_ENABLED=0` 才能出完全静态的二进制。
但 `-race` **依赖** cgo，所以测试步骤显式打开 `CGO_ENABLED=1`，打包步骤再关掉。

（顺带一提，Windows 上默认 `CGO_ENABLED=0`，本地直接 `go test -race` 会报
`-race requires cgo`，跑测试请去掉 `-race`。）

### 3.2 其余步骤

`go-version-file: server/ts9-stream/go.mod` 让工具链版本跟着仓库走，不必在
workflow 里写死。`gofmt -l .` 有输出即失败——这是最便宜的静态检查，仓库当前是
clean 的，所以可以安全地当硬性门槛。构建参数与
[部署说明 §4](deploy/stream-service.md) 保持一致：

```bash
go build -trimpath -ldflags "-s -w -X main.version=$version" ./cmd/ts9-stream
```

版本号从 `cmd/ts9-stream/main.go` 的 `var version` 默认值去掉 `-dev` 后缀得到。

产物目录里除了三个二进制，还会带上 `config.example.yaml` 与部署文档，
便于直接分发。

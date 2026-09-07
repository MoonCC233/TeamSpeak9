# 原生依赖：libopus

TSLib 的语音编解码通过 `[DllImport("libopus")]` 调用原生 libopus
（`third_party/TSLib/Audio/Opus/NativeMethods.cs`）。静态构造即触发
`Helper/NativeLibraryLoader.cs` 的 `PreloadLibrary()`，其搜索顺序为：

1. `{工作目录}/lib/x64/libopus.dll`
2. `{工作目录}/lib/libopus.dll`
3. `{程序集目录}/lib/x64/libopus.dll`
4. `{程序集目录}/lib/libopus.dll`

找不到时只在 NLog 里记一条 `Failed to load library`，**UI 仍能正常启动**，
但语音收发会在运行时失败。因此必须在打包阶段显式校验该文件存在。

## 放置方式

把 64 位 `libopus.dll` 放到本目录：

```
client/desktop/third_party/native/win-x64/libopus.dll
```

`TeamSpeak9.App.csproj` 会将其复制到输出目录的 `lib/x64/` 下（命中上面第 3 条路径）。
文件不存在时构建仅给出警告而不失败，以便在没有该二进制的环境下也能编译与跑单元测试。

## 为何不进仓库

libopus 是二进制产物，且各发行版的构建选项不同，因此不纳入版本控制
（见 `.gitignore` 中 `third_party/native/` 规则）。请从下列任一来源获取：

- 官方源码自行构建：<https://opus-codec.org/downloads/>（BSD-3-Clause）
- 已有的 TeamSpeak 3 客户端安装目录中同名文件
- vcpkg：`vcpkg install opus:x64-windows`，产物在 `installed/x64-windows/bin/opus.dll`
  （需重命名为 `libopus.dll`）

许可要求见仓库根目录 `NOTICE` 第 3 节。

## FFmpeg 7.x（屏幕共享 H.264 编码）

`ScreenVideoEncoder` 通过 `FFmpeg.AutoGen` 调用原生 FFmpeg 7.x 库做 H.264 编码。
`FFmpegInit` 的自动发现逻辑会从工作目录向上查找 `FFmpeg/bin/x64` 目录，因此这些
DLL 必须放在输出目录的 `FFmpeg/bin/x64/` 下（`TeamSpeak9.App.csproj` 已将其从
`third_party/native/win-x64` 复制过去）。

需要的 64 位 DLL（对应 FFmpeg 7.x / FFmpeg.AutoGen 8.1.0 的版本号）：

```
avcodec-63.dll
avdevice-63.dll
avfilter-12.dll
avformat-63.dll
avutil-61.dll
swresample-7.dll
swscale-10.dll
```

缺少时构建仅给出警告而不失败，`ScreenVideoEncoder` 会回退到托管 VP8 编码器
（`SIPSorceryMedia.Encoders`，自带 `vpxmd.dll`）。因此没有 FFmpeg 也能编译、
运行与观看，只是发布端无法使用 H.264 硬编。

## 为何不进仓库

libopus 与 FFmpeg 都是二进制产物，且各发行版的构建选项不同，因此不纳入版本控制
（见 `.gitignore` 中 `third_party/native/` 规则）。请从下列任一来源获取：

- 官方源码自行构建：<https://ffmpeg.org/download.html>（LGPL/GPL，注意链接方式）
- 预编译共享库：<https://www.gyan.dev/ffmpeg/builds/>（`ffmpeg-release-full-shared`，
  解压后取 `bin/` 下的 DLL 并按上表重命名）
- vcpkg：`vcpkg install ffmpeg:x64-windows`，产物在 `installed/x64-windows/bin/`

许可要求见仓库根目录 `NOTICE` 第 3 节。
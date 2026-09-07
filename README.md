# TeamSpeak9

[![Desktop CI](https://github.com/MoonCC233/TeamSpeak9/actions/workflows/desktop.yml/badge.svg)](https://github.com/MoonCC233/TeamSpeak9/actions/workflows/desktop.yml)
[![Android CI](https://github.com/MoonCC233/TeamSpeak9/actions/workflows/android.yml/badge.svg)](https://github.com/MoonCC233/TeamSpeak9/actions/workflows/android.yml)
[![Stream Service CI](https://github.com/MoonCC233/TeamSpeak9/actions/workflows/stream-service.yml/badge.svg)](https://github.com/MoonCC233/TeamSpeak9/actions/workflows/stream-service.yml)

面向 TeamSpeak 3 / 6 服务端的双端客户端项目，并附带一个用于**屏幕共享**的旁挂流媒体服务。

| 组件 | 目录 | 技术栈 | 说明 |
|---|---|---|---|
| Android 客户端 | [`client/mobile`](client/mobile) | Kotlin + Jetpack Compose | 基于 TS6_Droid 简中版二次开发，新增屏幕共享与观看 |
| PC 客户端 | [`client/desktop`](client/desktop) | C# + WPF + .NET 8 | 从零编写，高仿官方 TS6 客户端布局与功能 |
| 旁挂流媒体服务 | [`server/ts9-stream`](server/ts9-stream) | Go + pion/webrtc | 屏幕共享的信令与媒体转发（SFU / P2P 双模式） |
| 官方服务端 | `server/win`、`server/linux` | — | TeamSpeak 官方二进制，**不入库**（专有许可，需自行下载），且不作任何修改 |

## 为什么需要旁挂服务

TeamSpeak 6 服务端（`tsserver`）是闭源二进制，其许可证第 7.3 条禁止逆向、反编译与制作衍生作品，
因此**本项目不修改也不逆向 tsserver**。

官方的屏幕共享本身就是「tsserver 转发信令 + 外部 SFU 转发媒体」的架构
（可从二进制的公开字符串表看到服务器属性 `virtualserver_sfu_endpoint` 与流模式 `STREAM_MODE_P2P` / `STREAM_MODE_SFU`），
但其信令使用 TS6 私有 protobuf，运行在加密的私有传输层上，ServerQuery 中没有任何对应命令。

于是本项目在 tsserver **旁边**并行部署一个独立服务 `ts9-stream`：

```
                ┌──────────────────────────────┐
                │  tsserver (闭源, 不改动)      │
                │  语音 / 文字 / 频道 / 权限     │
                └───────▲──────────────▲───────┘
        TS 私有协议 (UDP)│              │ServerQuery（仅旁挂服务使用，只读校验）
                        │              │
   ┌────────────────────┴──┐   ┌───────┴──────────────────────────┐
   │ Android / PC 客户端    │◄─►│ ts9-stream                       │
   │  采集 / 编码 / 渲染    │WSS│  · TSSP v1 信令                  │
   └───────────────────────┘   │  · pion SFU 媒体转发              │
              ▲                │  · P2P 模式仅中转 SDP/ICE         │
              └── SRTP 媒体（SFU 转发 或 P2P 直连）──────────────────┘
```

客户端同时维持两条连接：TS 私有协议连 tsserver，[TSSP v1](docs/protocol/tssp-v1.md) 连 `ts9-stream`。
二者通过「服务器地址 + 频道 ID + 客户端 UID」关联，并由 `ts9-stream` 通过 ServerQuery **反向校验**客户端是否真的在线于该频道，
以此完成鉴权而无需用户额外设置密码。

> **互通性说明**：由于不使用官方私有信令，本项目的屏幕共享流只在本项目的客户端之间可见。
> 官方 TeamSpeak 客户端看不到本项目共享的画面，反之亦然。语音、文字、频道等功能则完全通过 tsserver，与官方客户端正常互通。

## 快速开始

```bash
# 克隆时一并拉取 Android 子模块
git clone --recurse-submodules https://github.com/MoonCC233/TeamSpeak9.git
```

`client/mobile` 是指向 [TS6_Droid_CN](https://github.com/MoonCC233/TS6_Droid_CN) 的
git submodule（GPL-3.0）。若已经克隆过，补一句 `git submodule update --init --recursive`。

官方 `tsserver` 不在本仓库内（专有许可禁止再分发），
需自行下载解压到 `server/win` 或 `server/linux`，详见
[旁挂服务部署说明 §3.1](docs/deploy/stream-service.md)。

## 文档

- [TSSP v1 信令协议规范](docs/protocol/tssp-v1.md)
- [旁挂服务部署说明](docs/deploy/stream-service.md)
- [TSLib ↔ TS6 服务端兼容性实测报告](docs/desktop/tslib-ts6-compat.md)
- [PC 端 UI 还原规格](docs/desktop/ui-spec.md)
- [持续集成与安装包构建](docs/ci.md)

## 第三方组件与许可

见 [NOTICE](NOTICE)。其中需特别注意：PC 端的 TeamSpeak 协议层复用 **TSLib（OSL-3.0）**，
这是带网络条款的 copyleft 许可，分发 PC 客户端时须一并提供对应源码。

## 许可

`server/win`、`server/linux` 下的 TeamSpeak 官方二进制与文档遵循其自带的 TeamSpeak 许可协议，不适用本仓库许可。
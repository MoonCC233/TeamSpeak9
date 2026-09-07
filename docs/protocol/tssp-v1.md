# TSSP v1 — TeamSpeak9 Stream Signaling Protocol

版本：`1.0`
状态：草案（实现基准）
适用组件：`server/ts9-stream`（服务端）、`client/mobile`（Android）、`client/desktop`（PC）

---

## 1. 背景与定位

TeamSpeak 6 服务端（`tsserver`）是闭源二进制，其许可证第 7.3 条禁止逆向、反编译与制作衍生作品。
官方的屏幕共享信令使用 TS6 私有 protobuf（`teamspeak.servermessages.streaming.*`），运行在加密的 TS 私有传输层上，
并通过服务器属性 `virtualserver_sfu_endpoint` 指向一个外部 SFU。ServerQuery 中没有任何对应命令。

TSSP 是本项目自定义的**旁挂**信令协议。它与 tsserver 完全解耦：

- tsserver 继续负责语音、文字、频道、权限，**不做任何改动**。
- `ts9-stream` 独立进程提供屏幕共享的信令与（可选的）媒体转发。
- 客户端**同时**维持两条连接：TS 私有协议连 tsserver，TSSP 连 ts9-stream。
- 两者通过「服务器地址 + 频道 ID + 客户端 UID」关联，并由 ts9-stream 通过 ServerQuery 反向校验。

> **互通性说明**：由于不使用官方私有信令，TSSP 的屏幕流只在本项目的客户端之间可见，
> 官方 TeamSpeak 客户端无法看到，反之亦然。

---

## 2. 传输层

| 项 | 规定 |
|---|---|
| 协议 | WebSocket over TLS（`wss://`）。明文 `ws://` 仅允许在 `dev_insecure: true` 时用于本地开发 |
| 默认端口 | `9987` 是 tsserver 语音端口，TSSP 默认使用 **`10099`** |
| 路径 | `/tssp/v1` |
| 编码 | UTF-8 JSON，一帧一条消息（WebSocket text frame） |
| 最大帧长 | 256 KiB（SDP 可能较大，但不应超过此限制） |
| 心跳 | 服务端每 20s 发 WebSocket Ping；客户端 60s 内无任何流量则应重连 |
| 子协议 | `tssp.v1`，客户端 **MUST** 在 `Sec-WebSocket-Protocol` 中声明；服务端在协商结果不是 `tssp.v1` 时以 `1002 protocol error` 关闭连接 |

### 2.1 服务地址发现

客户端获取 TSSP 端点地址有两种方式，**优先级从高到低**：

1. **从 tsserver 读取（推荐）**：TS6 的虚拟服务器属性 `virtualserver_sfu_endpoint` 可写且跨会话持久化，
   客户端连上 tsserver 后从 `serverinfo` 或 `notifyserverupdated` 读出即可，无需用户手工配置。
   管理员用 `serveredit virtualserver_sfu_endpoint=wss://<host>:10099/tssp/v1` 写入一次。
   （该属性可读可写已实测验证，见 [TSLib ↔ TS6 兼容性报告 §6.2](../desktop/tslib-ts6-compat.md)。）
2. **用户手工填写**：作为兜底，客户端设置里应保留一个可手填的 TSSP 地址输入框。

⚠️ **安全要求**：`virtualserver_sfu_endpoint` 由服务器管理员控制，客户端 **MUST** 把它当作不可信输入：

- scheme 必须是 `wss://`（除非用户显式开启开发模式）；
- 首次连接一个新地址时 **MUST** 提示用户确认，显示完整主机名与端口；
- 不得因为该地址来自 tsserver 就跳过 TLS 证书校验。

否则恶意服务器可以把客户端的屏幕画面引导到任意第三方地址。

---

## 3. 消息封装

所有消息共享同一外层结构：

```json
{
  "t": "setup",
  "id": "c7",
  "ts": 1764600000000,
  "d": { }
}
```

| 字段 | 类型 | 必需 | 说明 |
|---|---|---|---|
| `t` | string | 是 | 消息类型，见 §5 |
| `id` | string | 否 | 请求关联 ID。请求带 `id` 时，响应必须回同一个 `id`。事件推送无 `id` |
| `ts` | int64 | 否 | 发送方 Unix 毫秒时间戳，仅用于诊断 |
| `d` | object | 否 | 负载 |

### 3.1 响应

请求的响应只有两种类型：`ok` 与 `error`。

```json
{ "t": "ok", "id": "c7", "d": { } }
{ "t": "error", "id": "c7", "d": { "code": "NOT_SAME_CHANNEL", "message": "..." } }
```

### 3.2 命名约定

- 消息类型、字段名一律 `snake_case`。
- 时间戳统一 Unix 毫秒（int64）。
- ID 类型：`stream_id` 为服务端生成的 UUIDv4 字符串；`clid` 为 tsserver 的 int32 客户端 ID；`cid` 为 int64 频道 ID。

---

## 4. 鉴权：ServerQuery 反向校验

### 4.1 动机

ts9-stream 是网络暴露服务。若不鉴权，任何人都能冒充他人加入频道会话或偷看画面。
但 TSSP 不能要求用户额外设一套密码，也拿不到 tsserver 的会话密钥。
因此采用**反向校验**：客户端声明自己的身份，服务端通过 ServerQuery 向 tsserver 核对该身份此刻是否真的在线。

### 4.2 流程

```
Client                                ts9-stream                     tsserver
  │                                        │                              │
  │── hello{server_addr,uid,clid,cid,      │                              │
  │         nonce,client_info} ───────────►│                              │
  │                                        │── clientinfo clid=<clid> ───►│
  │                                        │◄── uid, cid, groups, ... ────│
  │                                        │  校验 uid/cid 一致且在线      │
  │◄── ok{session_token, expires_at, ...} ─│                              │
```

1. 客户端连上 tsserver 后，取得自己的 `clid`、`client_unique_identifier`（uid）、当前 `cid`。
2. 客户端发送 `hello`（见 §5.1）。
3. 服务端用配置中的 ServerQuery 凭据（SSH 优先，Raw 兜底）执行：
   - `use port=<虚拟服务器端口>`
   - `clientinfo clid=<clid>`
4. 校验全部通过才签发 token：
   - `client_unique_identifier` 必须等于 `hello.uid`；
   - `cid` 必须等于 `hello.cid`；
   - `client_type` 必须为 `0`（普通客户端，非 query）；
   - 若配置了组白名单/黑名单，检查 `client_servergroups`。
5. 校验失败返回对应错误码（§7），并计入速率限制。

### 4.3 Session token

- 结构：`v1.<base64url(payload)>.<base64url(hmac_sha256(payload, secret))>`
- `payload` 为紧凑 JSON：`{"sid":"<session uuid>","uid":"...","clid":123,"cid":45,"sa":"<server_addr hash>","exp":1764600600000}`
- `secret` 从配置文件或环境变量 `TS9STREAM_TOKEN_SECRET` 读取；**不得**硬编码，启动时若缺失则拒绝启动（`dev_insecure` 下自动生成随机值并打警告）。
- 默认有效期 **10 分钟**，可通过 `renew` 续签（§5.10）。服务端在 `exp` 前 2 分钟通过 `token_expiring` 事件提醒。
- token 与 WebSocket 连接绑定：换连接必须重新 `hello`。token 仅用于该连接内的消息校验与断线快速恢复。
- 频道变更：客户端换频道后必须发 `renew`（带新 `cid`），服务端重新走 ServerQuery 校验；旧频道的订阅会被服务端主动关闭。

### 4.4 服务端安全要求

- ServerQuery 凭据、TLS 私钥、token secret 只存在于服务端配置/环境变量，**永不下发客户端**。
- ServerQuery 账号只需读权限（`clientinfo` / `clientlist` / `channelinfo`）；文档中明示最小权限配置。
- 每个 IP 的 `hello` 失败次数受限（默认 10 次 / 5 分钟），超限临时封禁。
- ServerQuery 查询结果缓存 3 秒，避免被客户端放大攻击 tsserver。

---

## 5. 消息定义

### 5.1 `hello`（C→S，请求）

```json
{
  "t": "hello", "id": "c1",
  "d": {
    "protocol": 1,
    "server_addr": "ts.example.com:9987",
    "uid": "aBcDeF0123456789abcdef==",
    "clid": 17,
    "cid": 4,
    "nonce": "9f2a...",
    "client": { "name": "TeamSpeak9-Desktop", "version": "0.1.0", "platform": "windows" },
    "capabilities": {
      "modes": ["sfu", "p2p"],
      "video_codecs": ["H264", "VP8"],
      "audio_codecs": ["opus"],
      "max_recv_streams": 4
    }
  }
}
```

- `server_addr` 必须能被服务端映射到某个已配置的虚拟服务器（按 host:port 匹配，见部署文档）。
- `nonce` 为客户端随机值，服务端原样回显，用于客户端确认响应对应自己的请求。

**响应 `ok`：**

```json
{
  "t": "ok", "id": "c1",
  "d": {
    "session_id": "b1e5...",
    "session_token": "v1.eyJ...",
    "expires_at": 1764600600000,
    "nonce": "9f2a...",
    "server": {
      "modes": ["sfu", "p2p"],
      "default_mode": "sfu",
      "video_codecs": ["H264", "VP8"],
      "max_bitrate_kbps": 4000,
      "max_streams_per_channel": 4,
      "ice_servers": [
        { "urls": ["stun:stun.example.com:3478"] },
        { "urls": ["turn:turn.example.com:3478?transport=udp"], "username": "...", "credential": "...", "credential_ttl": 600 }
      ]
    }
  }
}
```

TURN 凭据是短时效的（REST API 风格 `timestamp:username` + HMAC），随 token 一起续签。

### 5.2 `setup`（C→S，请求）— 开始共享

```json
{
  "t": "setup", "id": "c2",
  "d": {
    "token": "v1.eyJ...",
    "mode": "sfu",
    "stream_type": "screen",
    "accessibility": "channel",
    "name": "屏幕 1",
    "properties": {
      "width": "1920", "height": "1080", "fps": "30",
      "codec": "H264", "bitrate_kbps": "2500",
      "audio": "false", "source": "display:0"
    }
  }
}
```

| 字段 | 取值 | 说明 |
|---|---|---|
| `mode` | `sfu` \| `p2p` | 服务端若不支持则回 `MODE_NOT_SUPPORTED` |
| `stream_type` | `screen` \| `window` \| `camera` | 对齐官方 StreamType 语义 |
| `accessibility` | `channel` \| `invite_only` | `channel`：同频道成员可直接订阅；`invite_only`：需 `join_request` 获批 |
| `properties` | string→string | 与官方 `SetupStreamRequest.properties` 同为字符串字典，便于扩展 |

**响应 `ok`：** `{ "stream_id": "...", "mode": "sfu", "publish": { ... } }`

- SFU 模式下 `publish` 含服务端的 `offer` 或指示客户端发 `offer`（本协议规定：**发布者始终作为 offerer**，服务端作为 answerer，简化实现）。
- P2P 模式下 `publish` 为空对象，等订阅者到来时逐个协商。

服务端随后向同频道所有会话广播 `stream_added`（§5.11）。

### 5.3 `update`（C→S，请求）— 更新共享参数

```json
{ "t": "update", "id": "c3", "d": { "token": "...", "stream_id": "...", "name": "屏幕 2", "properties": { "fps": "15", "bitrate_kbps": "1200" } } }
```

只有发布者可调用。服务端广播 `stream_updated`。

### 5.4 `stop`（C→S，请求）— 停止共享

```json
{ "t": "stop", "id": "c4", "d": { "token": "...", "stream_id": "..." } }
```

服务端关闭所有相关 PeerConnection，广播 `stream_removed`（`reason: "stopped"`）。

### 5.5 `list`（C→S，请求）— 列出可见流

```json
{ "t": "list", "id": "c5", "d": { "token": "...", "cid": 4 } }
```

`cid` 可省略（默认当前频道）。响应返回 `streams` 数组，元素结构见 §6.1。

### 5.6 `subscribe`（C→S，请求）— 观看

```json
{ "t": "subscribe", "id": "c6", "d": { "token": "...", "stream_id": "...", "prefer_mode": "sfu" } }
```

- 服务端校验订阅者与发布者同频道；不同频道回 `NOT_SAME_CHANNEL`。
- `accessibility == invite_only` 时回 `ok{ "state": "pending" }` 并向发布者推 `join_request`；获批后推 `subscribe_ready`。
- `accessibility == channel` 时：
  - SFU 模式：`ok{ "state": "ready", "mode": "sfu" }`，随后服务端发 `signaling{offer}`（**订阅方向服务端为 offerer**）。
  - P2P 模式：`ok{ "state": "ready", "mode": "p2p", "peer": { "clid": 17, "uid": "..." } }`，服务端通知发布者 `peer_joined`，由**发布者作为 offerer** 发起协商。

### 5.7 `unsubscribe`（C→S，请求）

```json
{ "t": "unsubscribe", "id": "c7", "d": { "token": "...", "stream_id": "..." } }
```

### 5.8 `respond_join`（C→S，请求）— 发布者审批

```json
{ "t": "respond_join", "id": "c8", "d": { "token": "...", "stream_id": "...", "clid": 23, "accept": true } }
```

### 5.9 `signaling`（双向）— SDP / ICE 中转

这是唯一的媒体协商通道，SFU 与 P2P 共用。

```json
{
  "t": "signaling",
  "d": {
    "token": "...",
    "stream_id": "...",
    "peer_clid": 23,
    "role": "publisher",
    "signaling_type": "offer",
    "signaling_data": "v=0\r\no=- ..."
  }
}
```

| 字段 | 说明 |
|---|---|
| `peer_clid` | P2P 必填，指明对端；SFU 模式省略（对端即服务端） |
| `role` | 发送者自身角色：`publisher` \| `subscriber` |
| `signaling_type` | `offer` \| `answer` \| `candidate` \| `end_of_candidates` \| `restart` |
| `signaling_data` | `offer`/`answer` 为 SDP 文本；`candidate` 为 JSON 字符串 `{"candidate":"...","sdpMid":"0","sdpMLineIndex":0,"usernameFragment":"..."}` |

`restart` 用于 ICE 重启（网络切换），携带新的 offer SDP。

**offerer 规则（避免 glare）：**

| 场景 | offerer | answerer |
|---|---|---|
| SFU 发布 | 发布客户端 | ts9-stream |
| SFU 订阅 | ts9-stream | 订阅客户端 |
| P2P | 发布客户端 | 订阅客户端 |

### 5.10 `renew`（C→S，请求）— 续签 / 频道变更

```json
{ "t": "renew", "id": "c9", "d": { "token": "...", "clid": 17, "cid": 9 } }
```

服务端重新走 ServerQuery 校验，返回新 token、新 `expires_at` 与刷新后的 TURN 凭据。
若 `cid` 变化，服务端关闭跨频道的订阅并推送对应 `stream_removed`（`reason: "channel_changed"`）。

### 5.11 服务端事件（S→C，无 `id`）

| 事件 | 负载要点 | 触发 |
|---|---|---|
| `stream_added` | `stream`（§6.1） | 同频道有人开始共享 |
| `stream_updated` | `stream` | 发布者调用 `update` |
| `stream_removed` | `stream_id`, `reason` | 发布者 `stop`、掉线、换频道、被管理员移除 |
| `subscribe_ready` | `stream_id`, `mode`, `peer?` | `invite_only` 获批 |
| `join_request` | `stream_id`, `clid`, `uid`, `nickname` | 有人请求观看 `invite_only` 流（发给发布者） |
| `join_rejected` | `stream_id`, `reason` | 发布者拒绝 |
| `peer_joined` | `stream_id`, `clid`, `uid` | P2P：订阅者就绪，通知发布者发 offer |
| `peer_left` | `stream_id`, `clid`, `reason` | P2P：订阅者离开 |
| `removed_from_stream` | `stream_id`, `reason` | 被发布者/管理员踢出 |
| `token_expiring` | `expires_at` | token 剩余 2 分钟 |
| `stats_request` | `stream_id` | 服务端请求客户端上报质量（可选实现） |
| `bye` | `code`, `message` | 服务端主动断开（关服、鉴权失效） |

### 5.12 `stats`（C→S，可选，无响应）

```json
{ "t": "stats", "d": { "token": "...", "stream_id": "...", "role": "subscriber",
  "bitrate_kbps": 2100, "fps": 29, "packet_loss": 0.004, "rtt_ms": 32, "jitter_ms": 6, "frames_dropped": 3 } }
```

服务端用于日志与自适应码率决策，不回复。

---

## 6. 数据结构

### 6.1 `stream`

```json
{
  "stream_id": "7f3c...",
  "cid": 4,
  "mode": "sfu",
  "stream_type": "screen",
  "accessibility": "channel",
  "name": "屏幕 1",
  "publisher": { "clid": 17, "uid": "aBc...==", "nickname": "MoonCC233" },
  "properties": { "width": "1920", "height": "1080", "fps": "30", "codec": "H264" },
  "viewer_count": 2,
  "created_at": 1764600000000
}
```

`nickname` 由服务端从 ServerQuery 结果填充，客户端不可自报（防伪造）。

---

## 7. 错误码

| code | 含义 | 客户端建议行为 |
|---|---|---|
| `BAD_REQUEST` | JSON 格式错误 / 缺字段 | 视为 bug，记录日志 |
| `UNSUPPORTED_PROTOCOL` | `hello.protocol` 不被支持 | 提示升级客户端 |
| `UNKNOWN_SERVER` | `server_addr` 未在服务端配置 | 提示管理员配置 |
| `QUERY_UNAVAILABLE` | ServerQuery 不可用 | 指数退避重试 |
| `CLIENT_NOT_FOUND` | tsserver 上找不到该 `clid` | 重新读取自身 clid 后重试 |
| `IDENTITY_MISMATCH` | uid 或 cid 与 tsserver 不一致 | 重新 `hello` |
| `NOT_ALLOWED` | 服务器组不在白名单 | 提示无权限 |
| `RATE_LIMITED` | 触发速率限制 | 按 `retry_after_ms` 退避 |
| `TOKEN_INVALID` | token 非法 | 重新 `hello` |
| `TOKEN_EXPIRED` | token 过期 | 发 `renew`；失败则重新 `hello` |
| `MODE_NOT_SUPPORTED` | 服务端未开启该模式 | 切换到另一模式 |
| `CODEC_NOT_SUPPORTED` | 无共同编解码 | 提示不兼容 |
| `STREAM_NOT_FOUND` | `stream_id` 不存在 | 刷新 `list` |
| `NOT_STREAM_OWNER` | 非发布者调用发布者操作 | 视为 bug |
| `NOT_SAME_CHANNEL` | 订阅者与发布者不同频道 | 提示需先进入该频道 |
| `ALREADY_PUBLISHING` | 该客户端已有活跃流 | 先 `stop` |
| `TOO_MANY_STREAMS` | 频道内流数超限 | 提示稍后再试 |
| `TOO_MANY_VIEWERS` | 观看者超限 | 提示稍后再试 |
| `JOIN_REJECTED` | 发布者拒绝 | 提示被拒绝 |
| `SIGNALING_FAILED` | SDP/ICE 处理失败 | 尝试 `restart`，仍失败则重建流 |
| `INTERNAL` | 服务端内部错误 | 退避重试 |

错误负载可选携带 `retry_after_ms`。

---

## 8. 状态机

### 8.1 会话（连接级）

```
    ┌──────────┐  ws connect   ┌──────────┐  hello ok   ┌───────────┐
    │DISCONNECTED├────────────►│  OPENED  ├────────────►│AUTHENTICATED│
    └──────────┘               └────┬─────┘             └──────┬──────┘
         ▲                          │ hello error              │ renew ok (loop)
         │                          ▼                          │
         │                     ┌─────────┐                     │ bye / ws close
         └─────────────────────┤ CLOSING │◄────────────────────┘
                               └─────────┘
```

- `OPENED` 状态下除 `hello` 外的消息一律回 `TOKEN_INVALID`。
- `hello` 超时（默认 10s 未收到）服务端主动关闭连接。

### 8.2 发布流

```
IDLE ──setup──► NEGOTIATING ──ICE connected──► LIVE ──stop/掉线──► CLOSED
                     │                          │
                     └── SIGNALING_FAILED ──────┘ (可 restart 回 NEGOTIATING)
```

### 8.3 订阅

```
NONE ──subscribe──► PENDING(invite_only) ──respond_join accept──► NEGOTIATING ──► WATCHING
  │                      │                                                            │
  │                      └── reject ──► NONE                    unsubscribe/流结束 ──┘
  └── subscribe(channel) ──► NEGOTIATING
```

### 8.4 生命周期联动（客户端职责）

- tsserver 连接断开 → 立即停止共享并关闭所有订阅，主动断开 TSSP。
- 客户端换频道 → 发 `renew`（新 `cid`）；服务端会清理跨频道订阅。
- TSSP 断线 → 指数退避重连（1s、2s、4s…上限 30s），重连后重新 `hello`，并按需重建发布/订阅。
- tsserver 仍在线但 TSSP 不可用 → 语音文字功能不受影响，仅屏幕共享降级不可用。

---

## 9. 媒体约定

| 项 | 规定 |
|---|---|
| 视频编解码 | H.264 Baseline（`profile-level-id=42e01f`，packetization-mode=1）优先；VP8 兜底 |
| SFU 转码 | **不转码**，按 payload type 原样转发 RTP |
| 音频 | 共享音频可选，Opus 48 kHz stereo；默认关闭（语音仍走 tsserver） |
| 关键帧 | 订阅者接入时 SFU 通过 PLI 向发布者请求关键帧 |
| 丢包恢复 | NACK + PLI；不启用 FEC（屏幕内容对延迟不敏感，重传成本更低） |
| 拥塞控制 | 启用 TWCC（`transport-cc`）与 REMB；服务端按 `max_bitrate_kbps` 硬上限截断 |
| 分辨率 | 发布端自行缩放；建议 1080p@30 / 720p@30 / 720p@15 三档 |
| Simulcast | v1 **不支持**（SFU 单层转发），预留在 `properties` 中协商，v2 再做 |
| DTLS-SRTP | 强制。`DTLS_SRTP_AES128_CM_HMAC_SHA1_80` 及以上 |
| ICE | 强制 `ice-lite: false`；服务端提供 STUN，TURN 可选但强烈建议（对称 NAT） |

### 9.1 Payload type 分配

因为 SFU 不转码，payload type 必须在实现之间固定下来。P2P 模式尤其重要：两端直接交换 SDP，中间没有任何一跳会替它们重映射编号。

| 编解码 | Payload type | `fmtp` |
|---|---|---|
| H.264 constrained baseline | **102** | `level-asymmetry-allowed=1;packetization-mode=1;profile-level-id=42e01f` |
| H.264 high | **108** | `level-asymmetry-allowed=1;packetization-mode=1;profile-level-id=640c1f` |
| VP8 | **96** | — |
| Opus | **111** | `minptime=10;useinbandfec=1`（48 kHz、2 声道） |

编号沿用 WebRTC 生态惯例（与 Chrome 常用值一致）。规范实现见服务端 `internal/sfu/engine.go` 的 `registerCodecs`；客户端侧对应 PC 端 `StreamCodecs`。发布端只需实现 102 与 96 两档，108 仅用于接收其它实现推来的 high profile。

> **注意**：SFU 转发订阅方向时会用服务端 MediaEngine 的编号重新生成 SDP，因此 SFU 模式下的编号不一致只会被静默吸收；一旦回落 P2P 就会直接失败。不要依赖这种吸收行为。

---

## 10. P2P 与 SFU 的回落策略

1. 客户端按配置选择首选模式（默认 `sfu`，局域网场景可配 `p2p`）。
2. P2P 模式下，若 ICE 在 15s 内未进入 `connected`，客户端应：
   - 发 `unsubscribe` / `stop`；
   - 以 `mode: "sfu"` 重新 `setup` / `subscribe`；
   - UI 提示「已切换到服务器转发模式」。
3. SFU 模式失败（服务端资源不足 / `INTERNAL`）不自动回落 P2P，直接报错，避免在多观众场景把上行压垮发布者。
4. 发布者在 P2P 模式下的观众数上限由客户端自行限制（建议 3），超出时提示切换 SFU。

---

## 11. 版本演进

- `hello.protocol` 为整数。服务端支持的版本集合在 `ok.d.server` 中不返回；不兼容直接回 `UNSUPPORTED_PROTOCOL`。
- 新增可选字段不算破坏性变更，客户端与服务端都必须**忽略未知字段**。
- 新增消息类型：接收方对未知 `t` 的请求回 `BAD_REQUEST`，对未知事件静默忽略。
- 破坏性变更递增 `protocol` 并新增路径 `/tssp/v2`。

---

## 12. 与官方实现的对应关系（仅供理解，不构成兼容）

| 官方（从二进制公开字符串观察到） | TSSP v1 |
|---|---|
| `SetupStreamRequest` | `setup` |
| `StopStreamRequest` | `stop` |
| `UpdateStreamRequest` | `update` |
| `RequestStreamInfoRequest` | `list` |
| `JoinStreamRequestRequest` | `subscribe` |
| `RespondJoinStreamRequestRequest` | `respond_join` |
| `StreamSignalingRequest` | `signaling` |
| `RemoveClientFromStreamRequest` | 服务端事件 `removed_from_stream` |
| `STREAM_MODE_P2P` / `STREAM_MODE_SFU` | `mode: "p2p"` / `"sfu"` |
| `virtualserver_sfu_endpoint` | ts9-stream 的 `wss://host:10099/tssp/v1` |

命名参照官方语义以便理解，但**报文格式、传输、鉴权完全不同**，两者不互通。

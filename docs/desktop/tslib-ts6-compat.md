# TSLib ↔ TeamSpeak 6 服务端兼容性实测报告

状态：**已验证**
被测服务端：`TeamSpeak 6 Server` / `6.0.0-beta12.1 [Build: 1785239375]` / Windows
被测协议库：[TSLib](https://github.com/Splamy/TS3AudioBot)（TS3AudioBot，OSL-3.0），`netstandard2.1`
探针：`TsProbe`（.NET 8 控制台，40 项检查）
结论：**TSLib 加一处 license 解析补丁即可完整驱动 TS6 beta12.1**，40/40 通过、0 失败、0 条解析异常。

> 本文是 PC 端（`client/desktop`）协议层的实现依据。所有条目都来自对本地 tsserver 的实测，
> **不涉及任何逆向、反编译或二进制修改**；未实测的内容一律标注为「未证实」。
> 与官方 ServerQuery 文档冲突之处，以本文实测为准（§4.9 列出已过时的官方文档条目）。

---

## 1. 结论摘要

| 项 | 结果 |
|---|---|
| UDP 语音协议 / 握手 | ✅ 可用，`pv=8`（TS3 为 `pv=3`，TSLib 已支持） |
| License chain 解析 | ⚠️ **必须打补丁**，TS6 新增 block type `8`，见 §3 |
| 客户端版本签名 | ✅ `VER_WIN_3_X_X`（`3.?.? [Build: 5680278000] DEBUG`）被接受 |
| 身份等级要求 | `virtualserver_needed_identity_security_level = 8` |
| 最低客户端版本 | `virtualserver_min_client_version = 1560850141`（未拦截上述签名） |
| 频道树 / 成员 / 权限 / 文字消息 | ✅ 全部可用 |
| 文件传输（图标） | ✅ 上传 / 列目录 / 下载 / 删除全通，往返字节一致 |
| 频道属性写入 | ✅ 19 项中 17 项可写，2 项被拒属预期，见 §5 |
| 图标写入 | ⚠️ **路径与 TS3 不同**，`*_icon_id` 属性已不可写，见 §4 |
| raw ServerQuery（10011） | ❌ **TS6 已移除**，只剩 SSH 与 HTTP，见 §7 |
| 官方屏幕共享信令 | ❌ 不可用（私有 protobuf），且本机 `capability_extensions` 无 `STREAM_SFU`，见 §6 |
| TSLib 解析异常 | ✅ 无 —— 探针收到的每一条报文都干净解析 |

---

## 2. 测试环境与复现方式

| 项 | 值 |
|---|---|
| 服务端 | `tsserver.exe` 6.0.0-beta12.1，`127.0.0.1:9987/UDP` |
| Query 端点 | SSH `127.0.0.1:10022`、HTTP `127.0.0.1:10080`、HTTPS `127.0.0.1:10443` |
| 虚拟服务器 | `virtualserver_id=1`，`virtualserver_unique_identifier=DjVYItdxSToj7Prj3Vi1tZNF7nmaiRVwnp1YQSfzCoo=` |
| 探针运行时 | .NET 8.0.30 / Windows NT 10.0.26200 |
| 探针身份 | 复用固定 identity（level 8），连上后 `client_type=0`，`cldbid=3` |

探针覆盖：identity → connect → whoami → initserver 字段全量转储 → channellist（typed + raw）→
book 状态 → clientlist/clientinfo/clientgetvariables → servergetvariables/serverinfo 全量转储 →
频道生命周期（create / edit / move / clientmove / 图标全链路 / 19 属性矩阵 / delete）→
文件传输 → 文字消息 → 8 秒保活 → 干净断开。

图标与组图标的**写入值域**部分通过 HTTP WebQuery 单独二分实测（`servergroupaddperm` / `channelgroupaddperm`
的返回码），因为该行为与客户端协议无关，纯服务端校验。

---

## 3. 必须的 license 补丁（否则连不上）

TS6 服务端在 license chain 中插入了一种 TSLib 未知的 block，导致握手期 `LicenseBlock.Parse`
直接返回 `Invalid license block type 8`，连接失败。这是**唯一**阻断项。

补丁位置：`TSLib/Full/License.cs`，`switch (data[33])` 新增分支。改动量 **+38 / -1，仅此一个文件**。

### 3.1 block type 8 的字节布局（实测推得）

```
偏移      长度   内容
0         1      key kind（必须为 0）
1        32      公钥（所有 block 通用）
33        1      block type —— TS6 新增值 8
34        4      not-valid-before（大端 u32，加 0x50E22700 秒得 Unix 时间）
38        4      not-valid-after
42       12      不透明数据（含义未知，不参与密钥派生）
54       N       issuer 字符串，null 结尾
54+N+1    7      不透明数据（含义未知）
```

因此 block 总长 = `42 (MinBlockLen) + 20 + N`。密钥派生只需要**精确的长度**，
不透明字节的语义无须理解，也不应去猜测。

### 3.2 补丁代码

```csharp
case 8:
    // TeamSpeak 6 servers (observed on beta 12.1) insert this block into their
    // license chain. Its semantics are opaque to us; key derivation only needs the
    // exact block length, which is: 12 opaque bytes, a null terminated issuer
    // string, then 7 opaque bytes.
    result = ReadNullString(data.Slice(54));
    if (!result.Ok) return result.Error;
    nullStr = result.Value;
    block = new Ts6ExtensionLicenseBlock(nullStr.str);
    read = 20 + nullStr.read;
    break;
```

配套改动：

- 新增 `ChainBlockType.Ts6Extension = 8` 枚举值。
- 新增 `Ts6ExtensionLicenseBlock : LicenseBlock`，只保留 `Issuer` 属性。
- `default:` 分支的报错信息附加 `HexDump(data)`（前 64 字节十六进制），
  这样下次再遇到未知 block type 时能一眼看出布局，不必再猜。

### 3.3 为什么这样改是安全的

- `block.Key`、`NotValidBefore/After`、`Hash` 的计算逻辑完全复用通用路径，没有为 type 8 特殊化。
- `allLen = MinBlockLen + read` 保证链上后续 block 的偏移正确；如果长度算错，
  下一个 block 的 `data[0] != 0` 会立刻报 `Wrong key kind`，不会静默错下去。
- 实测该服务端的 `license=NoLicense`（免费授权），链上 block 依次解析通过，
  握手后的所有加密报文收发正常 —— 长度推断被端到端验证。

---

## 4. 图标：TS6 的写入路径与 TS3 完全不同 🔑

这是 PC 端实现差异最大的一块。**TS3 时代的 `channeledit channel_icon_id=<crc32>` 在 TS6 上已被拒绝**，
必须改走权限表。

### 4.1 全路径矩阵

| 目标 | 命令 | 值域约束 | 读回位置与符号 |
|---|---|---|---|
| **虚拟服务器** | ✅ `serveredit virtualserver_icon_id=<crc32>` | **只接受无符号十进制**（传有符号 → `1540 convert error`） | `serverinfo.virtualserver_icon_id`（无符号） |
| **频道** | ❌ `channeledit channel_icon_id=…` | 所有值形式全拒（见 §4.2） | — |
| **频道** | ✅ `channeladdperm cid=N permsid=i_icon_id permvalue=<crc32>` | 无限制，**有符号 / 无符号都接受** | `channelinfo` / `channellist -icon`（**无符号**）；`channelpermlist`（**有符号**） |
| **频道（清除）** | ✅ `channeldelperm cid=N permsid=i_icon_id` | — | 读回 `0` |
| **服务器组** | ✅ `servergroupaddperm sgid=N permsid=i_icon_id permvalue=<crc32>` | ⚠️ **`type=0` 模板组与 `type=2` query 组只接受 `0..999`** | `servergrouplist.iconid`（**有符号**）；`servergrouppermlist`（有符号） |
| **频道组** | ✅ `channelgroupaddperm cgid=N permsid=i_icon_id permvalue=<crc32>` | 同上（`cgid` 1–4 是 `type=0`） | `channelgrouplist.iconid`（**有符号**）；`channelgrouppermlist`（有符号） |
| **客户端** | ❌ `clientedit client_icon_id=…` → `1538 invalid parameter` | — | — |
| **客户端** | ⚠️ `clientaddperm cldbid=N permsid=i_icon_id` 命令成功、`clientpermlist` 能读回，但 `client_icon_id` 始终为 `0` | — | **未证实有效** |

### 4.2 `channeledit channel_icon_id` 被拒的完整证据

探针对同一个频道依次尝试了 5 种值形式，全部失败：

| 传入值 | 说明 | 服务端返回 |
|---|---|---|
| （空） | 清除图标 | `1540 parameter_convert`（convert error） |
| `2725694802` | 无符号 crc32 | `1538 parameter_invalid` |
| `-1569272494` | 有符号 crc32 | `1540 parameter_convert` |
| `578211154` | `crc32 & int.MaxValue` | `1538 parameter_invalid` |
| `12345` | 任意小整数 | `1538 parameter_invalid` |

值得注意的是，被拒的 `channeledit` 之后服务端**仍然发出了 `notifychanneledited`**
（其中一次甚至带上了 `channel_icon_id=18446744072140279122`），但 `channelinfo` 读回的值未变。
所以 **不能用 `notifychanneledited` 判断写入是否成功**，必须看 `error id`。

### 4.3 频道图标的正确写法

```csharp
// 写入：permvalue 用无符号十进制字符串，这是对所有路径都成立的唯一形式
await client.SendVoid(new TsCommand("channeladdperm") {
    { "cid", channelId },
    { "permsid", "i_icon_id" },
    { "permvalue", crc.ToString() },   // crc 为 uint
});

// 清除
await client.SendVoid(new TsCommand("channeldelperm") {
    { "cid", channelId },
    { "permsid", "i_icon_id" },
});
```

写入后 `channelinfo` / `channellist -icon` 会把该权限值镜像回 `channel_icon_id`，
所以频道树的图标显示逻辑不需要额外查权限表。

### 4.4 组图标：`2560 invalid group ID` 是**误导性错误码** 🔑

对模板组写自定义 crc32 图标会得到 `2560 invalid group ID`，但**这与 group ID 无关**。
二分实测（sgid=4，`type=0`）：

| 值 | 结果 |
|---|---|
| `0` / `300` / `400` / `500` / `512` / `600` / `700` / `999` | ✅ ok |
| **`1000`** / `1001` / `1023` / `65535` / `1000000` / `2147483647` / `2147483648` / `-1` | ❌ `2560 invalid group ID` |

边界干净落在 **999 / 1000** 之间。交叉验证：

- 同一个 sgid=4 写其他权限（如 `b_client_info_view=1`）→ ok；覆盖已有值（300 → 301）→ ok
  ⇒ 不是「模板组只读」。
- `type=2` query 组（sgid 1/2）：`500`/`999` → ok，`1000` → 2560 ⇒ 与 `type=0` 同规则。
- `type=1` 实例组（sgid 6/7/8，cgid 5–8）：`1000`、`2725694802`、`-1569272494` 全 → ok。
- 频道组同构：cgid=2（`type=0`）`999` → ok / `1000` → 2560；cgid=6（`type=1`）`1000` → ok。

**推断的语义**：`type=0` 模板组与 `type=2` query 组的 `i_icon_id` 只允许引用**内置图标编号**
（出厂值就是 100 / 200 / 300 / 500 / 600 这类三位数），自定义 crc32 图标必须挂在 `type=1` 的实际使用组上。
该上限是否为硬编码常量、模板组语义是否有官方说明，**无从确认**（不逆向）。

出厂图标编号（本机 `virtualserver_id=1`，可作为「内置编号」的样本）：

```
servergrouplist  iconid (sgid 1..8) : 0 / 500 / 300 / 0 / 0 / 300 / 0 / 0
channelgrouplist iconid (cgid 1..8) : 100 / 200 / 600 / 0 / 100 / 200 / 600 / 0
```

**PC 端要求**：编辑组图标的 UI 必须先读 `servergrouplist` / `channelgrouplist` 的 `type` 字段。
`type != 1` 时禁用「上传自定义图标」，只允许从内置编号（`< 1000`）里选，
否则用户会撞上一个说「invalid group ID」而实际是值域问题的错误提示。

### 4.5 客户端图标：结论是「不做」

- `clientedit client_icon_id=<crc32>` → `1538 invalid parameter`。
- `clientaddperm cldbid=3 permsid=i_icon_id permvalue=2725694802` → ok，
  `clientpermlist` 能读回 `-1569272494`，但 `clientlist -icon` / `clientinfo` 的
  `client_icon_id` 始终是 `0`，`clientdbinfo` 里也没有该字段。
- 对在线的 query 客户端（`cldbid=1`）执行 `clientaddperm` → `512 invalid clientID`。

**判定**：`client_icon_id` 在 TS6 上很可能已是只读遗留字段，用户图标改由**服务器组 / 频道组图标**体现。
PC 端 v1 只实现频道 / 服务器 / 组三类图标 —— 这与官方 TS6 客户端的实际表现一致，**不算功能缩减**。
若后续发现真正的写入路径，再补即可。

### 4.6 有符号 / 无符号读回矩阵（必须实现双向转换）

同一个图标 ID，在不同接口上会以**三种**线上形式出现：

| 接口 | 形式 | 示例 |
|---|---|---|
| `serverinfo.virtualserver_icon_id` | 无符号十进制 | `2725694802` |
| `channelinfo.channel_icon_id` | 无符号十进制 | `2725694802` |
| `channellist -icon` 的 `channel_icon_id` | 无符号十进制 | `2725694802` |
| `channelpermlist` / `servergrouppermlist` / `channelgrouppermlist` / `clientpermlist` 的 `permvalue` | 有符号十进制 | `-1569272494` |
| `servergrouplist.iconid` / `channelgrouplist.iconid` | 有符号十进制 | `-1569272494` |
| 客户端协议 `notifychanneledited` 的 `channel_icon_id` | **u64 补码** | `18446744072140279122` |

TSLib 的 `using IconHash = System.Int32`（`TSLib/Generated/Messages.cs`），
所有 `IconId` 属性都是 `int`；其生成代码里 `channel_icon_id` 共 6 处解析点，逻辑统一：

```csharp
if (!value.IsEmpty && value[0] == (u8)'-') {
    if (Utf8Parser.TryParse(value, out i32 oval, out _)) IconId = oval;
} else {
    if (Utf8Parser.TryParse(value, out u64 oval, out _)) IconId = unchecked((i32)oval);
}
```

`-` 前缀走 `i32`，否则走 `u64` 再 `unchecked` 截断成 `i32` —— 上表三种形式**全部**能吃下，
PC 端直接复用 TSLib 的类型层即可，不需要自己解析。

自己拼命令或做比较时统一用：

```csharp
static uint ToWire(int iconId)   => unchecked((uint)iconId);
static int  FromWire(uint crc)   => unchecked((int)crc);

// 拼 permvalue / serveredit 一律用无符号十进制字符串
string wire = ToWire(iconId).ToString();
```

### 4.7 图标文件传输

图标本体走标准 TS 文件传输，与 TS3 一致：

| 项 | 值 |
|---|---|
| 文件名约定 | `/icon_<crc32-unsigned>`，如 `/icon_2725694802` |
| 所在频道 | `cid=0`（TSLib 的 `ChannelId.Null`），即服务器全局区 |
| CRC32 算法 | 标准 CRC-32，多项式 `0xEDB88320`，初值 / 终值 `0xFFFFFFFF` |
| `ftgetfilelist cid=0 path=/` | ⚠️ **只返回一个 `dir icons`**，看不到图标本身 |
| `ftgetfilelist cid=0 path=/icons` | ✅ 列出 `icon_2725694802` |
| 下载握手 | `ftinitdownload` → `notifystartdownload clientftfid=2 serverftfid=1 ftkey=<32 hex> port=30033 size=69 proto=0` |
| 传输端口 | `30033/TCP`（与语音端口分离） |
| 磁盘落地 | `<tsserver>\files\internal\icons\` 与 `<tsserver>\files\virtualserver_1\internal\icons\` |
| 往返一致性 | ✅ 69 字节 PNG 上传后下载，字节完全一致 |
| 删除 | `ftdeletefile cid=0 name=/icon_<crc32>` |

**PC 端注意**：图标浏览器不能只查 `/`，必须查 `/icons`。

TS6 另外提供 HTTP 直取通道（官方文档 `ftgetchannelfilehttptoken`）：

```
ftgetchannelfilehttptoken cid={channelID}|scid={avatars|icons|chat|listuserfiles}
→ cid= expires_in=60 jwt=<token>
```

`expires_in=60` 意味着 token 只有 60 秒有效期，适合「点开就下」，不适合缓存。
本项目 v1 走原生文件传输即可，此通道记录备用。

### 4.8 图标大小与权限

| 权限 | permid | 本机实测值 | 用途 |
|---|---|---|---|
| `i_icon_id` | **149** | — | 图标 ID 本体（就是 §4.1 里写的那个） |
| `i_max_icon_filesize` | **150** | **8192** | 上传上限（字节）。PC 端**必须在上传前本地校验** |
| `b_icon_manage` | **151** | 1 | 是否允许管理图标 |
| `b_virtualserver_modify_icon_id` | 76 | — | 是否允许改服务器图标 |
| `i_needed_modify_power_icon_id` | 32917 | 75 | 改图标所需权力值 |

用 permid 而非 permsid 可以省掉一次名字解析；不过 TSLib 在握手期已通过 `permissionlist`
建好了 `TablePermissionTransform`，两种都行（实测 `149 → i_icon_id` 解析正确）。

8192 字节的上限相当紧 —— 32×32 PNG 通常够用，PC 端的图标选择器应在选图后立刻检查大小并给出明确提示，
而不是等服务端拒绝。

### 4.9 已过时的官方文档条目

以下官方 ServerQuery 文档条目在 TS6 beta12.1 上**已不成立**，实现时不要照抄：

| 文件 | 行 | 原文 | 实测 |
|---|---|---|---|
| `server/win/serverquerydocs/channeledit.txt` | 56 | `channel_icon_id : CRC32 checksum of the channel icon` | ❌ 不可写，见 §4.2 |
| `server/win/serverquerydocs/clientedit.txt` | 16 | `client_icon_id : CRC32 checksum of the client icon` | ❌ 不可写，见 §4.5 |

另外 `server/win/serverquerydocs/` 下**没有** `permissiondoc.txt`；权限清单请用 `permissionlist` 命令实时获取。

---

## 5. `channeledit` 属性写入矩阵（19 项逐条实测）

探针对同一个频道逐条发 `channeledit`（一条命令只改一个属性，避免一个坏键掩盖其他键），
随后又用合并命令复验。结果：**19 项全部可写**，但其中 4 项有条件，见 §5.1–§5.4。

| 属性 | 结果 | 备注 |
|---|---|---|
| `channel_name` | ⚠️ | 可写，但**重发频道当前的名字会被拒**，见 §5.1 |
| `channel_topic` | ✅ | |
| `channel_description` | ✅ | |
| `channel_password` | ✅ | 传空串即清除密码 |
| `channel_codec` + `channel_codec_quality` | ✅ | 同一条命令一起改 |
| `channel_codec_latency_factor` | ✅ | |
| `channel_codec_is_unencrypted` | ✅ | |
| `channel_maxclients` | ✅ | |
| `channel_maxfamilyclients` | ✅ | |
| `channel_flag_maxclients_unlimited` | ✅ | |
| `channel_flag_maxfamilyclients_unlimited` | ✅ | |
| `channel_flag_maxfamilyclients_inherited` | ✅ | |
| `channel_needed_talk_power` | ✅ | |
| `channel_name_phonetic` | ✅ | |
| `channel_flag_permanent` | ⚠️ | 只有**与 `channel_flag_semi_permanent` 成对**发送才可靠，见 §5.2 |
| `channel_banner_gfx_url` | ✅ | **TS6 新增**，读回 `https://example.invalid/banner.png` |
| `channel_banner_mode` | ✅ | **TS6 新增**，读回 `2` |
| `channel_flag_semi_permanent` | ⚠️ | 同上，单独发送会 `channel_invalid_flags` |
| `channel_delete_delay` | ⚠️ | **只有临时频道接受**，见 §5.3 |
| `channel_icon_id` | ❌ | 见 §4.2，不在 17/19 计数内 |

### 5.1 `channel_name` 是「重发即报错」字段

`channeledit channel_name=<频道自己当前的名字>` 返回 **771 `channel_name_inuse`**。服务端把这个键
一律当作改名请求，改成自己的名字等于和自己撞名。其余 15 个普通字段重发原值都没问题，
`serveredit virtualserver_name=<原名>` 也没问题，这是 `channeledit` 独有的坑。

只有**字节完全相同**才会被拒，`ceshi` → `CESHI` 是合法改名，所以客户端判等必须用序数比较
（C# 的 `StringComparison.Ordinal`）。撞别人的名字返回同一个错误码，两种情况无法从响应区分。

⇒ **PC 端**：编辑时若名字未改动，`channel_name` 必须整个省略。

### 5.2 两个类型 flag 必须成对发送

| 命令 | 结果 |
|---|---|
| `channel_flag_semi_permanent=1`（目标为永久频道） | ❌ 775 `channel_invalid_flags` |
| `channel_flag_permanent=0`（目标为永久频道） | ✅ ok，但频道变成**临时**，空频道立即消失 |
| `channel_flag_permanent=0&channel_flag_semi_permanent=1` | ✅ ok，切到半永久 |
| `channel_flag_permanent=1`（目标为半永久频道） | ❌ 775 |
| `channel_flag_permanent=1&channel_flag_semi_permanent=0` | ✅ ok，切到永久 |
| 成对重发频道**已有**的类型 | ✅ ok，幂等 |
| `channel_flag_permanent=1&channel_flag_semi_permanent=1` | ❌ 775（唯一非法组合） |
| 完全不带 flag 的完整编辑 | ✅ ok，类型不变 |

单发一个 flag 只在它恰好与当前状态一致时才不报错，因此**成对发送是唯一可靠的类型切换方式**。
早先记录的「TSLib 成对发 flag 被 `channel_invalid_flags` 拒绝」是误判。

默认频道另有约束：把 `channel_flag_default=1` 的频道改成临时或半永久返回
**774 `default channel requires permanent`**。

### 5.3 `channel_delete_delay` 只对临时频道合法

| 频道类型 | `channeledit channel_delete_delay=N` | `channelcreate` 带 delay |
|---|---|---|
| 临时 | ✅ ok（含 `N=0`） | ✅ ok |
| 半永久 | ❌ 1538 `parameter_invalid`（含 `N=0`） | ❌ 1538 |
| 永久 | ❌ 1538（含 `N=0`） | ❌ 1538 |

不是值的问题，纯粹是类型的问题。所以判定条件是 `Kind == Temporary`，**不是** `Kind != Permanent`。

### 5.4 一条 `channeledit` 可以带齐全部字段

实测把 `channel_name` + 15 个普通字段 + 两个类型 flag + `channel_delete_delay` +
`channel_flag_default=1` 塞进**同一条** `channeledit` ⇒ **0 ok**，`channelinfo` 读回全部正确，
`channellist -flags` 确认默认频道也切过去了。

⇒ **PC 端**：一次编辑就是一条命令，不需要分步提交。`channelcreate` 仍不吃 banner 两个字段，
创建路径需要一次后续 `channeledit` 补横幅。

---

## 6. 屏幕共享相关字段（旁挂方案的实证依据）

### 6.1 `virtualserver_capability_extensions`

```
virtualserver_capability_extensions = FILETIME,STREAM_P2P
```

`initserver` 与 `serverinfo` 两处都是这个值。**没有 `STREAM_SFU`** ——
本机 tsserver 只声明支持 P2P 流，SFU 能力需要一个外部 SFU 服务。
这正面印证了 `server/ts9-stream` 旁挂方案的必要性：官方架构本身就把媒体转发放在 tsserver 之外。

### 6.2 `virtualserver_sfu_endpoint` 可写且持久化 🔑

```
virtualserver_sfu_endpoint = wss://127.0.0.1:10099/tssp/v1
```

这个值是探针早前通过 `serveredit` 写进去的，重连后 `notifyserverupdated` 与 `serverinfo` 都仍能读到
⇒ **该属性可写、可读、跨会话持久化**。

于是它可以直接当作**旁挂服务地址的自动发现通道**：管理员在 tsserver 上写一次，
双端客户端连上 tsserver 后从 `serverinfo` / `notifyserverupdated` 读出地址即可，无需在客户端手工配置。
（`docs/protocol/tssp-v1.md` 应把这条记为地址发现的推荐方式。）

⚠️ 安全提醒：该字段由服务器管理员控制，客户端应把它当作**不可信输入**：
必须校验 scheme 为 `wss://`（除非用户显式开启开发模式）、限制端口范围，并在首次连接新地址时提示用户确认，
以免被恶意服务器把客户端媒体流引到第三方地址。

### 6.3 `client_is_streaming`

`initserver` 与 `clientinfo` 中都有 `client_is_streaming = 0`。这是官方用来标记「该客户端正在共享」的字段，
但它由 tsserver 通过私有 protobuf 信令置位，ServerQuery 无对应写命令，**本项目无法设置它**。

因此本项目的「谁在共享」状态由 TSSP 自己的 `list` / 事件推送维护，不依赖 `client_is_streaming`。
副作用：官方客户端看不到本项目的共享状态，反之亦然 —— 这与 README 中的互通性说明一致。

---

## 7. TS6 已移除 raw ServerQuery（影响 `ts9-stream` 配置）🔑

三重证据：

1. tsserver 启动日志只有
   `listening for ssh query on 127.0.0.1:10022` 与 `listening for http query on 127.0.0.1:10080`，
   **没有** raw query 的监听行。
2. `Test-NetConnection 127.0.0.1 -Port 10011` → `False`（端口未监听）。
3. `10022` 的 banner 是 `SSH-2.0-libssh_0.11.4`，确为 SSH 服务。

即 TS6 只提供 **SSH（10022）**、**HTTP（10080）**、**HTTPS（10443）** 三种 query 端点，
TS3 时代的明文 raw query（10011）已不存在。

对本项目的影响：

| 位置 | 现状 | 需要的调整 |
|---|---|---|
| `server/ts9-stream/internal/config/config.go` | 默认 `query.protocol = raw`、默认端口 `10011` | 默认改为 `ssh` / `10022`，否则在 TS6 上开箱即用连不上 |
| `server/ts9-stream/internal/config/config_test.go` | 断言默认值为 raw / 10011 | 随默认值一起改 |
| `server/ts9-stream/internal/serverquery/client.go` | 只实现 raw 与 ssh 两种行协议 | TS6 上只能走 ssh；HTTP WebQuery 后端为可选增强 |
| `docs/deploy/stream-service.md` §5 | 基于「raw + ssh 双协议」描述 | 按「TS6 只有 ssh + http」重写 |

另外实测：**WebQuery 的 API key scope 不包含文件传输命令**
（`ftgetfilelist` → `5120 out of scope`），所以图标相关操作只能走客户端协议，不能用 WebQuery 走捷径。

---

## 8. 调用 TSLib 的注意事项（踩过的坑）

### 8.1 `channelpermlist` / `clientpermlist` 必须用 `SendHybrid`

TS6 在客户端协议下把权限列表**用通知回复**（`notifychannelpermlist` / `notifyclientpermlist`），
普通 `Send` 拿不到：

```csharp
var permList = await client.SendHybrid<ChannelPermList>(
    new TsCommand("channelpermlist") { { "cid", cid } },
    NotificationType.ChannelPermList);
```

三个配套约束：

- **不能用 `ResponseDictionary` 做 `SendHybrid` 的类型参数** ——
  `TSLib/Helper/CommandErrorExtensions.cs` 里是硬 cast，会抛 `InvalidCastException`。必须用具体的生成类型。
- **不要加 `-permsid` 选项** —— `ChannelPermList` 类型没有对应字段。
  让服务端回数字 permid，由握手期 `TsFullClient.ProcessPermList` 通过 `permissionlist` 建立的
  `TablePermissionTransform` 解析（实测 `149 → i_icon_id` 正确）。
- 返回的 `permvalue` 是**有符号**的，见 §4.6。

实测一个频道的 `notifychannelpermlist` 内容：

```
permid=89  permvalue=75           i_channel_needed_permission_modify_power
permid=137 permvalue=75           i_channel_needed_delete_power
permid=149 permvalue=-1569272494  i_icon_id          ← 图标
permid=227 permvalue=5            i_client_needed_talk_power
```

### 8.2 `TsBaseFunctions.ChannelEdit` 覆盖不全

`ChannelEdit` 的 18 个可选参数里**没有 icon、也没有 banner**。
`channel_banner_gfx_url` / `channel_banner_mode` / `i_icon_id` 都必须自己拼命令：

```csharp
await client.SendVoid(new TsCommand("channeledit") {
    { "cid", channelId },
    { "channel_banner_mode", 2 },
});
```

### 8.3 Book 里读不到 banner

`BannerGfxUrl` / `BannerMode` 只存在于 `ChannelInfoResponse` 与 `ChannelList` 两个响应类型上，
`TSLib/Full/Book/Book.cs` 的频道模型里没有这两个字段。
⇒ PC 端要显示频道横幅，必须单独发 `channelinfo`，不能只依赖 book 的缓存状态。

Book **能**读到图标（`icon` 字段，有符号形式），实测频道树里显示为 `icon=-1569272494`。

### 8.4 `DedicatedTaskScheduler` 的单线程约束

TSLib 的 `TsFullClient` 要求在其 `DedicatedTaskScheduler` 上驱动。用完**必须 `Dispose()`**，
否则调度循环永不结束、进程挂住不退：

```csharp
var scheduler = (DedicatedTaskScheduler)TaskScheduler.Current;
try     { await RunAsync(scheduler, /* … */); }
finally { scheduler.Dispose(); }   // 少了这句进程不会退出
```

对 WPF 而言这意味着：**TSLib 的调度器线程与 UI 线程是两条线程**，
所有从 TSLib 事件回调更新界面的地方都要 `Dispatcher.InvokeAsync`。
建议在 `TeamSpeak9.Core` 里封一层，把 TSLib 的回调统一转成在 UI 线程上触发的事件，
不要让 ViewModel 直接订阅 TSLib。

### 8.5 被拒的命令也可能触发通知

见 §4.2：`channeledit` 被拒后服务端**仍发** `notifychanneledited`。
⇒ 判断写入成功只看 `error id`，不要看通知。

### 8.6 版本签名

`VER_WIN_3_X_X`（`TSLib/Generated/TsVersion.gen.cs`，值为 `3.?.? [Build: 5680278000] DEBUG`）
被 TS6 beta12.1 接受，未被 `virtualserver_min_client_version=1560850141` 拦截。
探针的 `ConnectionDataFull` 用 8 参数构造，identity 需 level ≥ 8（本机 `needed_identity_security_level=8`）。

---

## 9. TS6 新增 / 值得注意的字段

以下字段在 TS3 时代不存在或语义有变，PC 端解析时应容错处理（TSLib 对无对应属性的键会静默忽略，不会报错）。

### 9.1 `initserver`（62 个线上键，38 个无 TSLib `InitServer` 属性对应）

| 键 | 值 | 说明 |
|---|---|---|
| `pv` | `8` | 协议版本，TS3 为 3 |
| `virtualserver_version` | `6.0.0-beta12.1 [Build: 1785239375]` | |
| `virtualserver_capability_extensions` | `FILETIME,STREAM_P2P` | 见 §6.1 |
| `virtualserver_administrative_domain` | （空） | TS6 新增 |
| `virtualserver_channel_temp_delete_delay_default` | `0` | |
| `client_is_streaming` | `0` | 见 §6.3 |
| `client_myteamspeak_id` / `client_integrations` / `client_active_integrations_info` / `client_myteamspeak_avatar` / `client_signed_badges` / `client_user_tag` | （空） | myTeamSpeak 账号体系，本项目按需求**不实现** |

### 9.2 `notifyserverupdated`（55 个键，23 个无对应）

| 键 | 值 | 说明 |
|---|---|---|
| `virtualserver_sfu_endpoint` | `wss://127.0.0.1:10099/tssp/v1` | 🔑 见 §6.2 |
| `virtualserver_mytsid_connect_only` | `0` | 若为 1 则只允许 myTeamSpeak 账号连接 —— PC 端应识别并给出明确提示 |
| `virtualserver_max_homebases` | `64` | TS6 「homebase」概念 |
| `virtualserver_homebase_storage_quota` | `4294967295` | |
| `virtualserver_canonical_name` | （空） | |
| `virtualserver_min_client_version` | `1560850141` | |
| `virtualserver_min_android_version` | `1559834030` | Android 端需注意 |
| `virtualserver_min_ios_version` | `1559144369` | |
| `virtualserver_min_clients_in_channel_before_forced_silence` | `100` | |

`serverinfo` 的键全集是 96 个（含 24 个 `connection_*` 统计键）。

### 9.3 `channelinfo`（28 键） / `channellist`（19 键）

| 键 | 说明 |
|---|---|
| `channel_banner_gfx_url` / `channel_banner_mode` | TS6 频道横幅，可写（§5），但 Book 读不到（§8.3） |
| `channel_storage_quota` | `4294967295`（无限） |
| `channel_filepath` | 如 `files\virtualserver_1\channel_10` |
| `channel_unique_identifier` | ⚠️ **TS6 是 UUID**（如 `3f775e5b-d280-403a-bd30-c934175ac697`），不再是 TS3 的 hash 串 —— 用它做本地缓存键时不要假设格式 |
| `channel_icon_id` | 只读镜像，见 §4 |

### 9.4 `clientinfo`（50 键）

新增 `client_is_streaming`；`client_icon_id` 恒为 `0`（§4.5）；
`client_base64HashClientUID`、`client_public_key_raw`、`client_unread_messages` 等照旧。

---

## 10. 其他实测通过的能力

| 能力 | 命令 | 结果 |
|---|---|---|
| 频道创建 / 编辑 / 删除 | `channelcreate` / `channeledit` / `channeldelete force=1` | ✅ |
| 频道排序 | `channelmove` | ✅（移到已在的位置会回 `channel_already_in`，属预期） |
| 自己换频道 | `clientmove` | ✅（目标传 `ChannelId.Null` 会回 `channel_invalid_id`，属预期） |
| 频道消息 | `sendtextmessage targetmode=channel` | ✅ |
| 私聊消息 | `sendtextmessage targetmode=private` | ✅ |
| 频道指挥官 | `clientupdate client_is_channel_commander` | ✅ |
| 客户端描述 | `clientedit client_description` | ✅ |
| 插件命令 | `plugincmd targetmode=CurrentChannel` | ✅ |
| 订阅全部频道 | `channelsubscribeall`（Book 自动执行） | ✅ |
| 保活 | 连续 8 秒，`ping`/丢包统计正常，无掉线 | ✅ |
| 干净断开 | `disconnect` → `notifyclientleftview reasonid=8` → `Disconnected` | ✅ |

---

## 11. 对 PC 端 11 个任务的可行性判定

| 任务 | 判定 | 说明 |
|---|---|---|
| `desktop-solution-setup` | ✅ 可做 | vendor 打过补丁的 TSLib（OSL-3.0 允许，须开源对应源码并在 NOTICE 声明）。**不要从零写握手** |
| `desktop-theme-system` | ✅ 可做 | 纯 UI，与协议无关 |
| `desktop-core-connection` | ✅ 可做 | 连接 / 重连 / 频道树 / 成员 / 权限 / 文字消息 / 文件传输全部实测通过；注意 §8.4 的线程模型 |
| `desktop-shell-layout` | ✅ 可做 | 纯 UI |
| `desktop-channel-management` | ✅ 可做（**实现方式变更**） | 频道增删改 + 服务器信息编辑全通；**图标改走 `channeladdperm i_icon_id`**（§4.3），组图标需按 `type` 分流（§4.4）。功能范围不缩减 |
| `desktop-chat-panel` | ✅ 可做 | `sendtextmessage` 两种 targetmode 实测通过；Markdown 为纯客户端渲染 |
| `desktop-audio` | ✅ 可做 | UDP 语音链路握手与保活已验证；具体编解码由 TSLib 的 Opus 通道负责 |
| `desktop-stream-client` | ✅ 可做 | 走 TSSP，与 tsserver 解耦；地址可从 `virtualserver_sfu_endpoint` 自动发现（§6.2） |
| `desktop-screen-share` | ✅ 可做 | 不依赖 tsserver 任何能力 |
| `desktop-screen-view` | ✅ 可做 | 同上；「谁在共享」用 TSSP 自己维护（§6.3） |
| `desktop-tests-packaging` | ✅ 可做 | 打包需一并分发 TSLib 源码以符合 OSL-3.0 |

**不实现的功能**（用户需求已明确排除，且服务端侧也无对应能力）：
联系人、群组、myTeamSpeak 账号登录 —— 对应 `client_myteamspeak_*` / `client_integrations` 系列字段一律忽略。

---

## 12. 未解决 / 未证实

| 项 | 状态 |
|---|---|
| `client_icon_id` 的有效写入路径 | 未找到。已排除 `clientedit`（1538）与 `clientaddperm`（写得进权限表但不镜像）。倾向于「TS6 用户图标只通过组图标体现」（§4.5） |
| `i_icon_id` 的 1000 上限是否为硬编码常量 | 无从确认（不逆向）。行为已由二分实测钉死（§4.4） |
| license block type 8 里 12 / 7 字节不透明数据的含义 | 未知，且不影响密钥派生（§3.1） |
| `virtualserver_webrtc_certificate` / `virtualserver_webrtc_private_key` | 仅见于二进制字符串表，本机 `serverinfo` / `notifyserverupdated` 未返回，未实测 |
| 官方 `STREAM_SFU` 能力的开启条件 | 未知；本机只声明 `STREAM_P2P`（§6.1） |

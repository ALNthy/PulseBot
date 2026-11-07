# 邮件适配器使用指南

邮件适配器（`AnBot.Adapter.Email`）把**一个自有邮箱**当作聊天平台接入 AnBot：
用 IMAP 收信，把邮件翻译成 Core 的 `MessageEvent`；插件的回复通过 SMTP 发回发件人。

它适合「没有即时通讯平台、但需要一个可编程消息入口」的场景，例如个人服务器告警、
内部工具的命令通道，或作为新适配器的参考实现。

> 默认关闭。`Adapters:Email:Enabled = false` 时适配器不建立任何邮箱连接，
> 宿主可以在没有邮箱配置的情况下正常启动。

---

## 1. 能力边界（首版）

| 支持 | 不支持 |
| --- | --- |
| IMAP 收件（IDLE，不支持时自动降级为轮询） | 附件（会被忽略） |
| SMTP 发件（纯文本） | HTML 富文本发送（只发纯文本） |
| 私聊会话（会话 id = 发件人地址） | 邮件列表 / 抄送 / 群组语义 |
| 发件人地址与域名白名单 | 基于 SPF/DKIM/DMARC 的可信身份（由邮件服务商负责） |
| 自动回复 / 邮件列表流量过滤 | OAuth2 认证（只支持用户名 + 密码/授权码） |
| 只处理**未读**邮件，处理完标记已读 | 已读历史邮件的回溯处理 |

邮件本身有延迟：一次命令的端到端响应时间取决于 `IdleTimeout`（默认 30 秒），
因为它决定适配器多久重新检查一次未读邮件。

---

## 2. 快速开始

### 2.1 准备邮箱

1. 确认邮箱服务商支持 IMAP/SMTP（多数免费邮箱需要在网页端手动开启，并生成**授权码**）；
2. 记下收件服务器、发件服务器及其端口/加密方式；
3. 准备一个**专门给机器人用的邮箱**，不要复用个人主邮箱。

### 2.2 配置

在 `appsettings.json` 的 `Adapters` 节下添加 `Email`（完整配置项见第 3 节）：

```jsonc
{
  "Adapters": {
    "Email": {
      "Enabled": true,

      // 收件
      "ImapHost": "imap.qq.com",
      "ImapPort": 993,
      "ImapSecurity": "SslOnConnect",
      "Mailbox": "INBOX",
      "IdleTimeout": "00:00:30",

      // 发件
      "SmtpHost": "smtp.qq.com",
      "SmtpPort": 465,
      "SmtpSecurity": "SslOnConnect",

      // 凭据：密码/授权码建议用环境变量注入，不要写进配置文件
      "UserName": "12345678@qq.com",
      "Password": "",

      // 机器人身份
      "FromAddress": "12345678@qq.com",
      "FromDisplayName": "AnBot",
      "Subject": "AnBot",

      // 准入：两者至少填一个，否则适配器拒绝启动
      "AllowedSenders": ["your-address@example.com"],
      "AllowedDomains": []
    }
  }
}
```

密码用环境变量注入：

```bash
# Linux / macOS
export Adapters__Email__Password='你的授权码'

# Windows PowerShell
$env:Adapters__Email__Password = '你的授权码'
```

或写进 `.env` 后由部署脚本注入；**不要把授权码提交进版本库**。

### 2.3 启动与验证

```bash
dotnet run --project src/AnBot.Cli
```

启动日志中应出现：

```
邮件适配器已启动，监听邮箱 INBOX@imap.qq.com。
已连接 IMAP 服务器 imap.qq.com:993，开始监听 INBOX。
```

然后从 `AllowedSenders` 里的地址给机器人邮箱发一封纯文本邮件，正文写：

```
/echo 你好
```

等待一个 `IdleTimeout` 周期（默认约 30 秒），机器人会回复一封主题为 `Re: AnBot`
的纯文本邮件。如果没收到，见第 6 节排错。

---

## 3. 配置项详解

配置节：`Adapters:Email`。类型定义见
[EmailAdapterOptions.cs](../src/AnBot.Adapter.Email/EmailAdapterOptions.cs)。

| 键 | 默认值 | 必填 | 说明 |
| --- | --- | --- | --- |
| `Enabled` | `false` | — | 是否连接邮箱。`false` 时只登记适配器，发送返回 `NotConnected` |
| `ImapHost` | 空 | ✅ | IMAP 服务器主机名 |
| `ImapPort` | `993` | — | IMAP 端口，取值 1–65535 |
| `ImapSecurity` | `"SslOnConnect"` | — | `Auto` / `None` / `SslOnConnect` / `StartTls` |
| `Mailbox` | `"INBOX"` | — | 监听的文件夹；子文件夹使用服务器的分隔符（多数是 `/`，部分服务商是 `.`） |
| `IdleTimeout` | `"00:00:30"` | — | IDLE 到期后重新检查未读邮件的间隔；必须大于 0。服务器不支持 IDLE 时作为轮询间隔 |
| `ReconnectDelay` | `"00:00:15"` | — | 连接断开后的重连等待时间；必须大于 0 |
| `SmtpHost` | 空 | ✅ | SMTP 服务器主机名 |
| `SmtpPort` | `587` | — | SMTP 端口，取值 1–65535 |
| `SmtpSecurity` | `"StartTls"` | — | 同 `ImapSecurity` |
| `UserName` | 空 | ✅ | IMAP 与 SMTP 共用的登录用户名（通常是完整邮箱地址） |
| `Password` | 空 | ✅ | 登录密码或授权码；建议用 `Adapters__Email__Password` 环境变量注入 |
| `FromAddress` | 空 | ✅ | 机器人发件地址；可写成 `AnBot <bot@example.com>`，也用于忽略自发邮件 |
| `FromDisplayName` | `"AnBot"` | — | 发件人展示名 |
| `Subject` | `"AnBot"` | ✅ | 主动发送时的主题；回复自动加 `Re: ` 前缀（已有则不重复） |
| `AllowedSenders` | `[]` | 见说明 | 允许交互的完整发件地址，不区分大小写 |
| `AllowedDomains` | `[]` | 见说明 | 允许交互的发件域名（可带或不带 `@`），不区分大小写；与地址白名单取并集 |

**启用时的启动校验**：以下任一不满足，宿主会在启动阶段直接失败，而不是「看起来启动了但收不到邮件」：

- `ImapHost` / `SmtpHost` / `UserName` / `Password` / `FromAddress` / `Subject` 非空；
- `ImapPort` / `SmtpPort` 在 1–65535 之间；
- `FromAddress` 能被解析为合法邮箱地址；
- `IdleTimeout` / `ReconnectDelay` 大于 0；
- `AllowedSenders` 与 `AllowedDomains` **至少有一个非空**。

---

## 4. 工作方式

### 4.1 收件流程

```
连接 IMAP → 认证 → 打开 Mailbox
  → 搜索未读（UNSEEN）
  → 逐封处理 → 标记已读
  → IDLE（IdleTimeout 到期后回到「搜索未读」）
```

- 只处理**未读**邮件；处理完（包括被过滤掉的）标记为已读，因此重启不会重复发布；
- 读取失败（通常是网络问题）**不**标记已读，下一轮会重试；
- 翻译或发布失败（毒消息）会记录日志并标记已读，避免阻塞后续邮件；
- 服务器不支持 IDLE 时自动降级为按 `IdleTimeout` 轮询。

### 4.2 过滤顺序

一封邮件要进入事件管道，必须依次通过：

1. **有发件人**：`From` 为空或无法解析 → 丢弃；
2. **不是自己**：发件地址等于 `FromAddress` → 丢弃（防止 SMTP 副本投回同一邮箱形成自回环）；
3. **不是自动流量**：满足任一条即丢弃：
   - `Auto-Submitted` 头存在且值不是 `no`（RFC 3834）；
   - `Precedence` 为 `bulk` / `list` / `junk`；
   - 存在 `List-Id` 头（邮件列表）。
4. **在白名单内**：发件地址精确匹配 `AllowedSenders`，或域名匹配 `AllowedDomains`。

被拒绝的发件人会记录一条警告日志；其余被过滤的邮件只记 Debug 日志。

### 4.3 会话模型

- 类型：**私聊**（`ConversationType.Private`）；
- 会话 id：发件地址的小写形式（例如 `alice@example.com`）；
- 插件回复到该会话即发回原发件人，回复会带上 `In-Reply-To` 与 `References`，
  在邮件客户端里表现为同一个线程。

### 4.4 正文提取

- 优先使用 `text/plain` 正文；
- 只有 HTML 正文时降级为纯文本：去掉标签、块级标签转换行、解码 HTML 实体、
  整段丢弃 `<script>` / `<style>`；
- 两者都为空时正文为空字符串。

`Message-ID` 会规范化为带尖括号的形式；缺失时生成一个本地占位 id
（`<anbot-…@localhost>`），此时回复无法正确串起线程。

### 4.5 发件

- 仅支持私聊会话；会话 id 必须是可解析的邮箱地址，否则发送失败；
- 正文为纯文本：图片、文件等非文本消息段会被降级为占位文本；
- 每次发送新建一条 SMTP 连接（邮件量下开销可忽略，也避免了连接池的线程问题）；
- 出站邮件带 `Auto-Submitted: auto-replied`（回复）或 `auto-generated`（主动发送），
  与入站过滤形成双向防循环；
- SMTP 服务器明确拒绝（认证失败、收件人不存在、反垃圾拦截）返回 `Rejected`，
  其余异常返回 `Unknown`，不会把平台异常抛给插件。

### 4.6 断线与重连

- 收件循环断开后按 `ReconnectDelay` 重连，直到宿主关闭；
- 停止适配器时最多等待 5 秒让收件循环收尾，之后由进程退出回收。

---

## 5. 安全说明

**`From` 头可以伪造。** 与即时通讯平台不同，邮件发件地址不是可信身份。
适配器的安全边界只有 `AllowedSenders` / `AllowedDomains` 白名单：

- 白名单为空时适配器**拒绝启动**，避免误配置后一个公开邮箱直接变成命令入口；
- 真正的身份验证（SPF / DKIM / DMARC）由收信服务商完成，本适配器不重复实现；
- **不要把邮箱地址加入 `Permissions:SuperAdmins`**，除非你明确接受
  「发件人身份可能被伪造」这一风险；
- 密码字段属于敏感配置，用环境变量或密钥管理注入，不要提交进版本库；
- 自动回复、退信、邮件列表流量默认丢弃，避免双方自动互发形成无限循环。

如果机器人需要面向不可信的发件人开放，建议在邮件服务商侧再加一层反垃圾/SPF 策略，
并使用专用邮箱，不要与个人邮箱混用。

---

## 6. 常见服务商配置

> 以下仅为常见取值，服务商可能调整端口、加密方式与认证策略，**以服务商最新文档为准**。
> 多数免费邮箱需要先在网页端开启 IMAP/SMTP 并生成**授权码**，不能用登录密码。

| 服务商 | IMAP | SMTP | 备注 |
| --- | --- | --- | --- |
| QQ 邮箱 | `imap.qq.com:993`，`SslOnConnect` | `smtp.qq.com:465`，`SslOnConnect`（或用 `587` + `StartTls`） | 需在设置中开启 IMAP/SMTP 并生成授权码 |
| 163 邮箱 | `imap.163.com:993`，`SslOnConnect` | `smtp.163.com:465`，`SslOnConnect` | 同样使用授权码 |
| Gmail | `imap.gmail.com:993`，`SslOnConnect` | `smtp.gmail.com:587`，`StartTls` | 需开启两步验证并使用「应用专用密码」；企业域策略可能禁用 IMAP |
| Outlook / Microsoft 365 | `outlook.office365.com:993`，`SslOnConnect` | `smtp.office365.com:587`，`StartTls` | 租户可能已禁用基本认证；本适配器只支持用户名 + 密码/应用密码，不支持 OAuth2 |

---

## 7. 排错

| 现象 | 排查方向 |
| --- | --- |
| 宿主启动失败，提示「邮件适配器配置无效」 | 对照第 3 节的启动校验清单；异常消息会指出具体缺哪一项 |
| 启动成功但收不到邮件 | ① 邮件是否已读？（只处理未读）② 发件人是否在白名单？③ 是否被对手方标记为自动流量/列表邮件？④ 是否发到了 `Mailbox` 之外的文件夹？ |
| 日志出现「已拒绝发件人 …」 | 发件地址不在 `AllowedSenders` / `AllowedDomains` 中，注意域名匹配不含 `@` |
| 回复发不出去 | 检查 `SmtpHost` / `SmtpPort` / `SmtpSecurity` 与授权码；日志会记录 SMTP 服务器的拒绝原因 |
| 命令响应很慢 | 邮件本身就是异步通道；可调小 `IdleTimeout`（代价是更频繁的 IMAP 往返） |
| 同一封邮件被处理两次 | 「标记已读」失败时会重复处理一次；查看日志中的 `MarkSeen` 警告 |
| 连接反复断开 | 检查网络与邮箱服务商的连接数限制；调整 `ReconnectDelay` |
| 邮件进了对方垃圾箱 | 在发件服务器侧配置 SPF / DKIM，适配器不参与发信信誉建设 |

---

## 8. 与后台/权限的配合

- 邮件用户同样受 `Permissions` 与黑白名单约束——它们发生在事件管道里，与适配器无关；
- 想让某个邮箱地址拥有管理员权限，需要把**规范化后的小写地址**写进对应权限名单；
- 再次强调：邮件地址可伪造，生产环境应优先使用白名单 + 普通权限，
  而不是把邮箱地址提升为超级管理员。

---

## 9. 后续可能的方向

当前版本刻意保持最小闭环。尚未实现、按需求优先级排列：

- 附件（入站提取 / 出站附加）；
- HTML 邮件发送（目前只发纯文本）；
- 多文件夹监听与已读历史回溯；
- OAuth2 认证（Gmail / Microsoft 365 的部分租户已禁用基本认证）；
- 邮件列表 / 抄送语义的会话建模。

实现这些之前，先评估它们是否值得引入对应的复杂度——邮件适配器的定位是
「一个可编程的文本命令通道」，而不是完整的邮件客户端。

# 2026-11-18 · WebConsole 插件验收测试（测试侧）

> **开发侧处置**：[P5 + WebConsole 联合处置记录](2026-11-18-p5-webconsole-response.md)。

> **状态**：Phase 1 Go；Phase 2/3 Conditional（真实保存/执行等待 Host G5/G6）
> **基线**：`HEAD=2a13b8b`
> **最终复测**：[P5 + WebConsole 修复复测](2026-11-18-p5-webconsole-retest.md)
> **证据**：`artifacts/webconsole-test/2026-11-18-1200/`（`host.log`、`curl/`、`audit/`）

## 1. 前置

| 项 | 结果 |
| --- | --- |
| 插件构建 | **通过** |
| WebConsole 单测 | **123/123** |
| UI smoke | **`ui smoke ok`**，checks=6，gaps=7 |
| 宿主 | 真实 `natter serve` + 8 插件，webconsole listening，双 upstream connected |

## 2. 通过项

| 范围 | 用例 |
| --- | --- |
| Gate A | W-001/002/003（W-004 主框架 254/254） |
| Gate B | W-010 staging、W-011 加载/`web status`、W-012 缺 token（`missing-secret`）、W-013 短 token（`webconsole.token-too-short`）、W-014 非 loopback（schema 拒绝）、W-017 静态缺失（`webconsole.static-assets-missing`） |
| Gate C | W-020 未登录 401、W-021 错 token 401、W-022 限速 429+Retry-After、W-023 `HttpOnly; SameSite=Strict`、W-024 登出 200→401、W-025 无 CSRF 403、W-026 非法 Origin 403 |
| Gate D | W-030 `/`+app.js 200 且 CSP `default-src 'none'`、W-031 HEAD 200、W-032 路径穿越 404、W-033 404、W-035 安全头 |
| Gate E | W-040 snapshot 11 字段、W-042 plugins 计数/失败、W-043 upstreams 含时间戳/`sendFailures`、W-044 profiles、W-045 `host.logs.unavailable`、W-046 config secret 显示 `${secret:...}`、W-047 audit、W-048 dashboard 元素齐全 |
| Gate F | W-050 valid、W-051 invalid JSON、W-052 明文 secret（`config.secret_literal`）、W-053 `${secret:...}` 放行、W-055 413 超限、W-058 `config.write_disabled`、W-059 ETag 稳定/变化 |
| Gate G | W-061 `command.not_allowed`、W-065 `host.admin_command.unavailable`（501 语义）、W-064 `command.disabled`（console 关闭） |
| Gate H | W-070 trusted proxy → `Secure` cookie、W-072 审计 0700/0600、W-074 audit 无 secret、W-075 external 不重复绑定 |

## 3. 缺陷

### BUG-WC-TRUSTED-PROXY（Medium）

- **现象**：`X-Forwarded-For: 10.9.9.9`（非 trustedProxies 127.0.0.1）时，`X-Forwarded-Proto: https` 仍被采纳，Set-Cookie 仍带 `Secure`。
- **预期**：未受信代理头应忽略，不因伪造头改变 HTTPS 判定。
- **证据**：`curl/gate-h2.txt`。
- **建议**：按连接源 IP 校验 `trustedProxies` 后才接受 `X-Forwarded-*`。

### BUG-WC-PATH-ID（Medium）

- **现象**：`POST /api/config/profiles/validate` 的 `path:"../etc/passwd"` 返回 `valid:true`（id 归一化为 `profiles`），未拒绝路径穿越。
- **证据**：`curl/gate-f2.txt`。
- **建议**：config id 白名单/禁止 `..` 与分隔符；即使当前 write disabled，也应拒绝非法 id。

### BUG-WC-CMD-ARGS（Medium）

- **现象**：`allowCommandConsole=true` + `readOnly=false` 时，9 个参数（>8）和含换行参数的请求未被策略层拒绝，直接走到 `host.admin_command.unavailable`。
- **证据**：`curl/gate-g5.txt`。
- **预期**：W-062/W-063 应在插件策略层返回拒绝（`command.invalid_arguments` 类）。
- **建议**：`WebConsoleCommandPolicy.Evaluate` 增加参数数量/长度/控制字符校验。

## 4. 观察（Low）

| 编号 | 现象 |
| --- | --- |
| OBS-WC-PORT-EXTERNAL | 端口 5090 被占时自动 external 模式（warning），不返回 `webconsole.port-in-use`；W-015 与 W-016 实际合并 |
| OBS-WC-METHOD | 带 cookie 的 PUT → 403，文档 W-034 预期 405 |
| OBS-WC-MISSING-TOKEN-CODE | 缺 token 返回 `phase=config code=missing-secret`，文档预期 `webconsole.token-missing`（语义等价） |
| OBS-WC-READONLY-CMD | `readOnly=true` 时即使 `allowCommandConsole=true` 命令仍被禁用（实现要求 readOnly=false，文档未写明） |

## 5. 未测/待补

- W-027 会话过期（60min，未等待）；W-028 重启后旧会话（已由登出 401 间接覆盖，未单独测）；
- W-067 输出脱敏/截断（无 host runner，不可达）；
- W-073 只读挂载 audit=error（需只读挂载）；
- W-080~086 浏览器真实截图（curl 验证了登录/HTML/登出，未做截图）；
- W-051 的 issues 只做了 JSON 解析失败，未逐一核对 pointer。

## 6. 结论

- **Phase 1 只读控制台：Go**（认证/CSRF/静态/CSP/snapshot/审计/安全头通过）。
- **Phase 2/3：插件侧策略可验证**（validate/secret/ETag/write_disabled/command.not_allowed/501 语义）；真实保存/执行仍为 501，符合文档口径。
- **3 个 Medium 已关闭**（trusted proxy、path id、命令参数校验）；Low 已按文档/语义收口，未阻塞 Phase 1。

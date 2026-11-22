# WebConsole 前端方向（借鉴 chat-demo 的设计取舍）

> **状态**：方向已定 · P1 待实现
> **日期**：2026-11-22
> **定位**：在**不引入构建链**的前提下，把 WebConsole 从「能用的表格页」提升为「事故现场能迅速定位问题的控制台」。
> **参考**：[deepseek-ai/chat-demo](https://github.com/deepseek-ai/chat-demo)（MIT）仅作设计参考，不搬代码、不搬工具链。
> **关联**：[WebConsole 插件文档](WEBCONSOLE_PLUGIN.md)、[Conversation Console](CONVERSATION_CONSOLE.md)、[运维手册 §5.4](OPERATIONS_RUNBOOK.md)

---

## 0. 一句话

> 管理面的价值是**别的都挂了它还能开**。所以：借它的信息组织方式，不借它的重量。

---

## 1. 为什么不能直接搬

| 维度 | chat-demo | WebConsole |
| --- | --- | --- |
| 定位 | 产品面（用户聊天） | 管理面（运维诊断） |
| 栈 | React 18 + zustand + rspack + TypeScript | 零依赖静态 `wwwroot` |
| 状态源 | IndexedDB / localStorage | 服务端为唯一真值 |
| 凭据 | API Key 存浏览器 | 服务端持 secret，浏览器只见 `${secret:NAME}` |
| 构建 | `pnpm build` → `dist/` | 无构建，`HttpListener` 直出 |

**四条不能借**：

1. **API Key 入 localStorage** — 与我们的凭据模型相反，且管理面有审计/日志/命令输出三条泄漏路径；
2. **浏览器作为状态源** — 审计必须是服务端追加写入，不能是可被清掉的浏览器数据；
3. **浏览器直连第三方 API** — 会绕过认证、审计、脱敏边界；
4. **对话式布局** — 气泡布局信息密度太低。管理面一屏要看到 50 条日志、10 个上游、一次 diff。

---

## 2. 当前的短板

```text
app.js 905 行 / app.css 181 行 / index.html 143 行
```

具体问题（不是审美问题，是**定位效率**问题）：

| 现象 | 后果 |
| --- | --- |
| `/status` 把 trace 压成 5 行文本 | 看不出哪步慢、哪步失败、工具调用几次 |
| 结果「截断 500 字」 | 关键错误信息常在尾部，被截掉 |
| 「最近 5 条 trace」 | 长任务看不到完整链路 |
| 只有表格与按钮 | 空态/错态/载态不一致，出问题时不知道在等什么 |
| 导出只有单个日志文件 | 事故交接时拿不到完整现场 |

---

## 3. 借什么（P1 范围）

### 3.1 Trace 时间轴（最高价值）

把压平的文本改成竖向时间轴：

```text
┌ task_3f2a  Completed  v4   elapsed 12.4s  tools 1/3
│
├─ started                      10:48:33.201
├─ step #1    native            12ms
├─ tool       clock      ok     45ms   ████
├─ step #2    native             3ms
└─ final      completed        10:48:45.610
```

要素：

- 每行左侧时间轴竖线，形成「链」的视觉；
- tool 行显示 `name / status / duration`，耗时用横向条表示**相对占比**；
- 错误行整行标红并显示错误码（可复制）；
- step 行显示协议（native / text）；
- 顶部汇总：task id、状态、版本、总耗时、工具预算使用量。

数据来源：`/status` 已返回 `step`、`tools=used/budget`、`remaining`、`error`、`trace` 数组。**不需要新增后端字段**。

### 3.2 折叠详情替代截断

| 位置 | 现在 | 改为 |
| --- | --- | --- |
| `/status` 结果 | 截断 500 字 | 摘要 200 字 + 「展开全文」 |
| trace 条目 | 最多 5 条 | 全量列表，默认折叠到 10 条 + 「加载更多」 |
| config diff | 200 行上限直接截断 | 保留上限，但明确提示「已截断 N 行」并提供完整长度 |
| 命令输出 | 64 KiB 截断 | 同上：显示 `outputBytes` 原值 vs 展示值 |

原则：**截断可以，但必须告诉用户原文有多长、被截了多少。**

### 3.3 三态规范

每个异步区域统一三态：

```text
空    → 一句人话说明「为什么这里是空的」（不是空白）
错    → 错误码 + 可操作建议 + 重试按钮
载    → 骨架占位（不是全屏转圈）
```

尤其「空」要区分：

| 情况 | 文案 |
| --- | --- |
| 没有数据 | 「当前没有活跃任务」 |
| 没有权限 | 「需要 admin 角色」 |
| 功能未启用 | 「config 写入已关闭」 |
| 宿主不可用 | 「宿主管理面不可用（501）」 |

现在这四种经常都显示成同一句模糊提示，运维要猜。

### 3.4 诊断包导出（ZIP）

把单文件导出升级成「一次带走整个现场」：

```text
natter-diag-<UTC时间戳>.zip
├── manifest.json        版本、主机、采集时间、采集人
├── health.json          /health 快照
├── upstreams.json       upstream 详情
├── providers.json       provider 状态
├── plugins.json         插件列表与状态
├── config.redacted/     脱敏后的配置文档
├── logs.jsonl           最近 N 条日志（已脱敏）
└── audit.jsonl          审计尾部（已脱敏）
```

要求：

- 生成前对每个文件跑同一套脱敏（`WebConsoleRedaction`）；
- `manifest.json` 记录每个文件的条目数与截断情况；
- 文件名带时间戳，避免覆盖；
- 大小有上限（如 8 MiB），超限时按 manifest 声明截断。

参考 chat-demo 的「导出带清单」模式，但内容完全不同。

---

## 4. 技术形态：保持零构建，改用原生 ES modules

### 4.1 为什么不引入打包器

```text
管理面的存在意义 = 别的东西都挂了它还能开
```

引入 React + rspack 意味着：

- CI 多一条前端构建链；
- `dist/` 产物要与 DLL 同步（今天已经因为**旧 staging** 和 **旧 Release bin** 各踩了一次假绿）；
- 供应链面扩大（一个能执行命令、能写配置的面，挂上数百个传递依赖）；
- CSP 要放开更多（inline eval / 外部源）。

**每多一条产物同步面，就多一次「测试全绿但实机是旧代码」的机会。**

### 4.2 但 vanilla 不等于单个大文件

905 行还能忍，加完时间轴和折叠会到 1500+。解法不是上框架，是用浏览器原生能力：

```html
<script type="module" src="app.js"></script>
```

```javascript
// app.js
import { renderTrace } from './views/trace.js';
import { renderLogs } from './views/logs.js';
import { renderConfig } from './views/config.js';
import { renderCommands } from './views/commands.js';
import { api } from './api.js';
import { redact, escapeHtml } from './util.js';
```

目标结构：

```text
wwwroot/
├── index.html
├── app.js              入口：路由 + 轮询 + 全局状态
├── api.js              fetch 封装、错误码归一
├── util.js             escapeHtml / 时间格式化 / 截断标记
├── state.js            会话、角色、readOnly（集中，别散在 DOM 里）
└── views/
    ├── overview.js     health / upstreams / providers
    ├── trace.js        task trace 时间轴（新增）
    ├── logs.js         日志 + 过滤 + 导出
    ├── config.js       配置编辑器 + diff
    ├── commands.js     命令台
    └── diag.js         诊断包导出（新增）
```

约束：

- `HttpListener` 需为 `.js` 返回 `text/javascript`（模块必须正确 MIME，否则浏览器拒绝加载）；
- 全部资源同源，无 CDN；
- 禁止 inline `<script>`（CSP 已有约束）；
- 每个模块导出纯函数渲染，不共享可变全局（状态走 `state.js`）。

### 4.3 什么时候才该重新考虑框架

同时满足以下三条时再谈：

1. 出现流式 / 高频局部更新（例如内嵌 Playground 的流式对话）；
2. 需要富文本渲染（Markdown、代码高亮）；
3. 上述需求带来的 vanilla 复杂度已经影响可维护性（例如单个 view 超过 400 行且难以测试）。

否则不上。

---

## 5. 不借什么

| 项 | 原因 |
| --- | --- |
| 消息气泡 / 对话布局 | 信息密度太低，管理面要密度 |
| 模型选择器 / 输入框 | 那是 Playground 的事，见 [Conversation Console](CONVERSATION_CONSOLE.md) |
| KaTeX / Mermaid | 运维不需要渲公式和流程图 |
| 过度动效 | 事故现场要的是快，不是流畅 |
| 「美化」隐藏原始值 | 运维要看到精确的 hash、错误码、字节数，不能友好化掉 |
| localStorage 存凭据 | 见 §1 |

---

## 6. 分期

### P1（本文档范围）

```text
1. 原生 ES modules 拆分（app.js → entries + views）
2. Trace 时间轴
3. 折叠详情替代截断
4. 三态规范统一
5. 诊断包导出
```

验收：功能等价 + 上述 5 项可用；`wwwroot` 仍无构建步骤；CSP 不放宽；现有 333 个测试不回归。

### P2（视需要）

```text
日志虚拟滚动（>2000 行）
配置 diff 的语法高亮（纯 CSS + 简单 tokenizer，不引库）
键盘快捷键（/ 搜索、g 跳转、Esc 关闭）
多语言（zh/en）——若要，用 data-i18n + 字典对象，不引 i18n 库
```

### 不做

```text
暗色以外的主题定制
可视化图表库（趋势图用内联 SVG 手写即可）
```

---

## 7. 验收标准（P1）

- [ ] `wwwroot` 无构建步骤，`HttpListener` 直接可服务；
- [ ] 所有 `.js` 以 `text/javascript` 返回，模块加载无控制台错误；
- [ ] CSP 头未放宽（无 `unsafe-eval`、无外源）；
- [ ] Trace 时间轴显示 step / tool / 耗时 / 错误码，长任务可展开全部；
- [ ] 所有截断位置显示「原文长度 vs 展示长度」；
- [ ] 四类空态文案可区分（无数据 / 无权限 / 未启用 / 宿主不可用）；
- [ ] 诊断包 ZIP 内每个文件都经过脱敏，`manifest.json` 有截断声明；
- [ ] viewer 角色下所有写入口仍被禁用且提示明确；
- [ ] `readOnly=true` 时行为与现在一致；
- [ ] WebConsole 既有测试全绿，且新增时间轴/导出用例；
- [ ] 前端仍无第三方运行时依赖（`grep` 断言 `wwwroot/**/*.js` 无 `import ... from 'https://'`）。

---

## 8. 风险

| 风险 | 缓解 |
| --- | --- |
| ES modules 拆分引入回归 | 每个 view 单独可测（纯函数渲染 + fake DOM，沿用现有 ui-smoke 思路） |
| 诊断包泄漏 secret | 复用 `WebConsoleRedaction`，并加「打包后扫描明文哨兵」的自检 |
| 模块 MIME 配错导致全站白屏 | 启动时自检一次 `GET /app.js` 的 Content-Type；CI 加断言 |
| 时间轴在小屏挤压 | 响应式：窄屏下耗时条改数值显示 |

---

## 9. 一句话总结

> 借它的**信息组织方式**（时间轴、折叠、清单化导出），守住我们的**部署形态**（零构建、同源、无第三方运行时）。

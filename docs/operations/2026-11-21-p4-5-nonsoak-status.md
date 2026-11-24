# P4.5 非 Soak 测试状态标记

> **日期**：2026-11-21
> **分支**：`dev`
> **用途**：把 P4.5 中除 24h soak 以外的项目先标记清楚，避免和 soak 结果混在一起。
> **交接单**：[P4.5 实机验收交接](P4.5_REAL_MACHINE_HANDOFF.md)

---

## 1. 总状态

| 项 | 状态 | 说明 |
| --- | --- | --- |
| 构建 | ✅ PASS | `dotnet build Natter.slnx` 0 error |
| 框架测试 | ✅ PASS | **410/410** |
| WebConsole 测试 | ✅ PASS | **236/236** |
| WebConsole Phase 1 | ✅ GO | 只读控制台 |
| WebConsole Phase 2/3 | ✅ GO | 配置写入/回滚/受限命令 |
| P5 `/task` 主链路 | ✅ GO | 真实 Discord `/task` 通过 |
| P5 M-1 ~ M-5、M-7 ~ M-12 | ✅ PASS（人工确认） | 用户已执行；证据待归档 |
| P5 M-6 他人 `/cancel` | ⚠️ BLOCKED | 无第二个 Discord 账户；自动化用例已覆盖 |
| Docker Tier1 | ✅ PASS | 镜像/健康/插件已验证 |
| Docker Tier2 | ✅ PASS | 4 插件、hostParity 通过 |
| staging 同源校验 | ✅ PASS | `mismatches=0` |
| systemd 安装/运行 | ✅ PASS（运行中） | 服务已启动并承载 M-*；`verify.sh` 最终证据待归档 |
| secret 轮换 | ⏳ 未执行 | 需要 Discord/DeepSeek 控制台权限 |
| 24h soak | 🔄 进行中 | 当前唯一长跑项 |

---

## 2. 不能混淆的口径

### M-6

标记：

```text
M-6 BLOCKED — no second Discord account
```

不标 FAIL。自动化已覆盖 actor 归属：

- `P5FollowupTests` cancel/ownership；
- `TaskRuntimeTests` cancel before terminal。

### systemd

M-* 能跑，说明 systemd 服务和 Discord upstream 已经可用。
但 `deploy/systemd/verify.sh` 的正式证据仍需归档：

```bash
sudo deploy/systemd/verify.sh \
  --evidence artifacts/ops/p4.5/handoff/systemd-$(date +%F)
```

如果 `status healthy` 失败，先查 `selfId`、插件目录读权限、Discord upstream。

### secret 轮换

没有平台控制台操作记录，不能标 PASS。
需要单独执行：

```bash
scripts/rotate-secret.sh --file /etc/natter/live.env --check
```

并按 [secret 轮换](secret-rotation.md) 完成一次真实轮换。

---

## 3. 现在剩下的真正项

```text
1. 24h soak 完成并出 report
2. secret 轮换一次并记录指纹
3. systemd verify.sh 证据归档
4. M-6 如有第二账号再补
```

其中 24h soak 是当前唯一需要等待时间的项；其余是收尾证据/平台操作。

---

## 4. 完成后的 Go 条件

- [ ] 24h coverage ≥ 95%，无未解释 gap；
- [ ] 新载荷静默断连为 0，或有 `lastDisconnectError/Code`；
- [ ] `sendFailures` 不增长；
- [ ] 无重复回复/重复结果；
- [ ] secret 轮换通过；
- [ ] systemd verify 证据归档；
- [ ] M-6 明确 BLOCKED 或补测。

---

## 5. 一句话

**除 24h soak 外，代码、WebConsole、P5、Docker、systemd 运行和 M-* 已标记；secret 轮换与系统证据仍需收尾，不能把 soak 和这些混成一个“通过”。**

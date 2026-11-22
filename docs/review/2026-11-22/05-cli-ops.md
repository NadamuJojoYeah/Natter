# 05 · CLI / 发布 / 运维 代码审查报告

- **仓库**：`/home/ndm/Projects/csharp/Natter`（.NET 10，commit `671097b`）
- **审查范围**：`src/Natter.Cli`、`src/Natter.Cli.Core`、`src/Natter.Cli.Abstractions`、`src/Natter.Hosting.Android`、`scripts/`（16 个脚本）、`.github/workflows/{ci,deploy-verify}.yml`、`deploy/`（docker + systemd）、`config/profiles*.json`、`docs/{PUBLISHING,OPERATIONS_RUNBOOK,DEVELOPMENT}.md`
- **方式**：只读静态审查 + 交叉核对（代码 ↔ CI ↔ 脚本 ↔ 文档四向对齐）；**未修改任何源码/配置/脚本/工作流**，仅写入本报告。
- **说明**：`obj/` 下生成的 `AndroidManifest.xml` 作为 Release 构建事实证据被引用（`src/Natter.Hosting.Android/obj/Release/net10.0-android/android/manifest/AndroidManifest.xml`）。不确定项标「待确认」。

---

## 0. 严重度统计

| 严重度 | 数量 | 判据 |
|---|---|---|
| **P0** | **0** | 本范围内未发现可直接远程利用、或必然造成数据丢失/凭据外泄到不可信方的确定缺陷（本报告不给“凑数 P0”）。 |
| **P1** | **7** | 秘密进入进程列表、验收口径可假阳性通过、供应链门禁被全局关闭、`/health`/`--json` 未脱敏泄漏、Release 包功能性失效、运维手册明令禁止的“抢 bot 会话”被默认路径触发。 |
| **P2** | **31** | 脚本非幂等/无 trap/误判、CI 覆盖缺口、CLI 退出码与帮助信息不一致、容器与 systemd 加固缺口、Android 生命周期。 |
| **P3** | **30** | 可移植性、可读性、脆弱断言、文档与实现漂移、边角参数处理。 |
| **合计** | **68** | 全部为仓库真实问题，逐条索引见文末。 |

Top 级结论先说：**这个仓库的“代码质量”明显高于“验证链质量”**。CLI 内核（parser/registry/runner）设计干净，但 ①验收工具（soak）在最坏情况下会给出假 PASS；②CI 的漏洞审计被 `Directory.Build.props` 全局关掉；③`/health` 的脱敏只做了一半；④Android Release 包没有 `INTERNET` 权限。这四条任何一条都能让“看起来绿”的发布其实没被验证。

---

## 1. scripts/ —— 脚本健壮性

### R-01 [P1] Discord bot token 出现在 `curl` 命令行（进程列表可见）
`scripts/p2-collect.sh:37-40`

```bash
curl -s -m 10 "https://discord.com/api/v10/channels/$DISCORD_TEST_CHANNEL_ID/messages?limit=10" \
  -H "Authorization: Bot $DISCORD_BOT_TOKEN" -o "$OUT/discord-tail.json" || true
```

- **影响**：`-H` 头作为 argv 传给 `curl`，同机任意用户 `ps auxww` / 读 `/proc/<pid>/cmdline` 即可拿到 bot token → 直接接管 bot。同一个仓库里 `scripts/rotate-secret.sh:1-10` 专门写了“value never appears on the command line (no `ps`, no shell history)”的设计约束，此处正好违反。
- **修复方向**：改用 `curl -H @-`（从 stdin 读头）或 `curl -K "$tmp_cfg"`（配置文件 0600、含 `header = "Authorization: Bot ..."`），trap 清理；退一步可用 `--oauth2-bearer`（同样在 argv，不可接受）→ 必须走配置文件/stdin。

### R-02 [P2] `live-serve.sh` 声明的运行时长被 CLI 静默截断一半
`scripts/live-serve.sh:5-6,151` ↔ `src/Natter.Cli.Core/Commands/EventsCommands.cs:57`

```bash
# live-serve.sh:5  scripts/live-serve.sh            # stage + start in background (default 7200s)
# live-serve.sh:152  events watch --seconds "$SECONDS_TO_RUN"
```
```csharp
// EventsCommands.cs:57
var seconds = Math.Clamp(invocation.GetIntOption("seconds", 3), 1, 3600);
```

- **影响**：默认 7200s 被 `Clamp(...,1,3600)` 压成 3600s。host 一小时后自行退出，而脚本已写好 PID 文件并打印 `running pid=...`；`--status` 仍报“running”，实际会话已死。长链路（Discord 3s ack、代理抖动）验证到的其实是前 1 小时。
- **修复方向**：把上限提到与 `serve --duration` 一致（或去掉 clamp，用 `long` + 合理性校验）；`live-serve.sh` 启动后校验 CLI 的 `Watched N event(s)` / 用时是否与请求值一致。

### R-03 [P2] 后台 host 的 PID 文件记录的是 `setsid` 而非 host
`scripts/live-serve.sh:160-165`

```bash
setsid nohup dotnet "$CLI_DLL" ... >>"$LOG_FILE" 2>&1 &
PID=$!
echo "$PID" >"$PID_FILE"
```

- **影响**：`setsid` 在调用者已是进程组长时会 fork；`$!` 可能是立即退出的 `setsid` 包装进程，而真正的 `dotnet` 是它的子进程。`--stop` 时 `kill "$pid"` 打空 → 旧 host 继续持有网关会话与 `.lock`，下次 `start` 抢不到锁。`sleep 6` + `kill -0 "$PID"` 的存活性检查同样可能误判。
- **修复方向**：用 `setsid dotnet ... & echo $!`（先 `setsid --wait` 或用 `exec`）不可靠；推荐 `setsid --fork` 后由 host 自己写 pidfile，或直接记录进程组 `PGID` 并用 `kill -- -$PGID`。

### R-04 [P2] SQLite 备份：checkpoint 失败静默、路径未转义、无完整性校验
`scripts/natter-backup.sh:6-11`

```bash
test -f "$src"
mkdir -p "$(dirname "$out")"
sqlite3 "$src" "PRAGMA wal_checkpoint(FULL);"
sqlite3 "$src" ".backup '$out'"
chmod 600 "$out"
```

- **影响**：
  1. `test -f "$src"` 失败在 `set -e` 下静默退出，无任何提示（运维会以为“备份过了”）。
  2. `PRAGMA wal_checkpoint(FULL)` 在活跃写入时会返回 busy，脚本忽略返回值 → “先 checkpoint 再 backup”的承诺没有落地（`.backup` 本身会处理 WAL，所以危害有限，但文档口径 `docs/OPERATIONS_RUNBOOK.md:126` 描述的行为与实现不符）。
  3. `$out` 被拼进 SQL 字符串：路径含 `'` 时 `sqlite3` 报 SQL 错误；含空格可用但很脆。
  4. 备份完成后**没有 `PRAGMA integrity_check`**，磁盘满/被打断产生的半份库会被当成“备份成功”。
  5. `mkdir -p` 用默认 umask 建目录（通常 0755），备份库所在目录对同机其他用户可遍历。
- **修复方向**：参数校验加 `|| { echo ...; exit 3; }`；用 `.backup` 的参数形式 `sqlite3 -cmd ".timeout 5000" "$src" ".backup $out"`（或先 `cp -a` 三件套 `-wal/-shm` 再打开副本）；备份后 `sqlite3 "$out" 'pragma integrity_check;' | grep -qx ok`；`umask 077` 后再 `mkdir`。

### R-05 [P2] `rotate-secret.sh` 无 trap 清理临时文件与备份
`scripts/rotate-secret.sh:122-146`

```bash
tmp="$(mktemp "${FILE}.tmp.XXXXXX")"
backup="$backup_dir/$(basename "$FILE").bak.$(date +%Y%m%d-%H%M%S)"
cp -p "$FILE" "$backup"
```

- **影响**：`mktemp` 之后到 `mv` 之间任何失败（awk 读失败、`diff` 分支之外的 `chmod` 失败、磁盘满、SIGINT）都会留下 `live.env.tmp.XXXXXX`（0600，含明文新凭据）和 `.bak.*` 副本，且不清理；`awk ... >"$tmp"` 在磁盘满时会写半份，若随后 `mv` 成功则得到半截 env 文件（第 138 行的 diff 保护只比较“非目标行”，行数变化会命中，但磁盘满导致的截断未必）。另外 `$tmp` 由 `chmod 600` 前是 `mktemp` 的 0600，尚可。
- **修复方向**：`trap 'rm -f "${tmp:-}"' EXIT INT TERM`；写完后先 `awk ... >"$tmp" && test -s "$tmp"` 再 `mv`；`mv` 后再跑一次 `bash -n` 或逐行解析校验。
- **附**：第 145 行 `mv -f` 会以临时文件的属主/属组替换原文件（原文件可能 `root:natter 0640`），仅靠 `chmod 600` 兜底，**属主变化没有校验**，`docs/operations/secret-rotation.md` 的 systemd 路径下可能让 `natter` 用户读不到——建议 `chown --reference="$FILE"`。

### R-06 [P2] `soak-sample.sh` 在文件创建前 `chmod`，首次落盘是 0644
`scripts/soak-sample.sh:68-71`

```bash
FIRST_WRITE=0
[[ -f "$OUT" ]] || FIRST_WRITE=1
mkdir -p "$(dirname "$OUT")" 2>/dev/null || { ...; exit 3; }
chmod 600 "$OUT" 2>/dev/null || true
```

- **影响**：文件不存在时 `chmod` 直接失败（被 `|| true` 吞掉），随后 `>>"$OUT"` 用当前 umask 创建（交互式 shell 常为 0644）。样本里含 `lastError`/`lastDisconnectError`（见 R-30，未经脱敏），同机其他用户可读。systemd 单元有 `UMask=0077` 才不触发；手工运行必然触发。
- **修复方向**：`(umask 077; : >>"$OUT")` 先建文件再 `chmod`；或全程 `install -m 600 /dev/null "$OUT"`。

### R-07 [P2] `soak-sample.sh` 的“无 jq 回退”会写出破坏审计的样本
`scripts/soak-sample.sh:169-172`

```bash
else
  # No jq: keep the raw body but wrap the sample metadata as a separate prefix-free line.
  printf '%s\n' "$body" >>"$OUT"
fi
```

- **影响**：回退路径丢掉 `sampledAt`/`sampledWallClock`/`source`。`soak-report.py:109-114` 在没有 `sampledAt` 时会退回 `sampledWallClock`，两者都没有则计入 `unparsableLines` 并从时间线剔除 → **覆盖率与 gap 统计全部失真**，且报告不会因此 FAIL（见 R-10/R-11）。而 `deploy/systemd/natter-soak.service` 并不保证 `jq` 存在（`install.sh:200` 只把它写成“needs jq on the host”）。
- **修复方向**：缺 `jq` 时宁可失败（`exit 4` 并在 journal 里叫出来），或用 python3 兜底；至少把 `sampledAt` 用纯 printf 拼到 raw body 前后。

### R-08 [P2] `p2-serve.sh` 默认运行目录写死日期，与自身文档冲突且不可重复
`scripts/p2-serve.sh:8,13`

```bash
# Run dir defaults to artifacts/p2/<today>. Override with NATTER_P2_RUN.
RUN_DIR="${NATTER_P2_RUN:-$ROOT/artifacts/p2/2026-11-15-1647}"
```

- **影响**：文档说 `<today>`，代码是固定 `2026-11-15-1647`。每次运行都复用同一目录：`stage()` 只 `mkdir -p` + `cp`（第 26 行，不清空 `lib/`），旧的、已被删除的 DLL 会残留在插件目录里被加载 → “本地能跑、干净环境不能跑”，且两次运行的 `evidence/serve-all.log` 混在一起。
- **修复方向**：`RUN_DIR="${NATTER_P2_RUN:-$ROOT/artifacts/p2/$(date +%F-%H%M)}"`；`stage()` 内部 `rm -rf "$RUN_DIR/plugins/all/$id"` 后再建（对齐 `stage-tier1-plugins.sh` 的做法）。

### R-09 [P2] `p2-serve.sh` 的 `exec ... | tee` 不是原子替换，信号与退出码都走形
`scripts/p2-serve.sh:61-63`

```bash
exec dotnet "$CLI_DLL" --plugin-dir ... serve --duration "$SECONDS_TO_RUN" --health-port "$PORT" \
  2>&1 | tee -a "$RUN_DIR/evidence/serve-all.log"
```

- **影响**：管道中的 `exec` 只替换左侧子 shell，脚本进程仍是 `tee` 的父进程；`kill <script-pid>` / `systemctl stop` 的 SIGTERM 不会转发给 `dotnet`，`serve` 拿不到优雅停机（`docs/OPERATIONS_RUNBOOK.md` 承诺的 WAL 干净关闭失效），`--duration` 之外只能靠 SIGKILL。
- **修复方向**：`exec > >(tee -a "$log") 2>&1` 后 `exec dotnet ...`；或 `set -m` + `trap 'kill -TERM $child' TERM` 转发。

### R-10 [P1] `soak-report.py`：`error` 样本（host 不可达）**永远不影响结论**
`scripts/soak-report.py:115-118,329,362`

```python
if row.get("error"):
    audit.errors.append(row)
    continue
audit.samples.append(Sample(at=at, raw=row, source=...))
```
```python
"errorSamples": len(audit.errors),
```
（`errorSamples` 只在 `print_human` 里打印，`reasons`/`result` 从不引用）

- **影响**：`soak-sample.sh` 在 host 不可达时写的正是 `{"error":"unreachable"}`（`soak-sample.sh:184-189`）。这些行被移出时间线，所以：
  - 停机发生在**采样窗口两端**（启动晚于 timer、或 host 中途死掉再没起来）时，剩下的好样本之间没有 gap、coverage 100% → **`result: PASS`**；
  - 脚本头部注释（`soak-report.py:6`“did the sampler keep up (coverage, gaps, **unreachable windows**)?”）承诺的检查没有实现。
  - 结果：一个“24h soak”可以在一台只健康了 20 分钟的机器上通过验收。
- **修复方向**：把 `error` 样本纳入时间线（占位 timestamp + `unreachable=true`），或至少加规则：`errorSamples > 0` 且 `errorSamples/(samples+errorSamples) > 阈值` → FAIL；并检查“首样本 ≤ 头部 `soakStart` + 1.5×interval”“末样本 ≥ `soakStart+durationSeconds` − 1.5×interval”。

### R-11 [P1] 覆盖率的分母是“观测到的跨度”，不是声明的采样契约
`scripts/soak-report.py:142-145`

```python
first, last = samples[0].at, samples[-1].at
window = (last - first).total_seconds()
expected = int(window // interval) + 1 if interval > 0 else len(samples)
coverage = round(100.0 * len(samples) / expected, 1) if expected else 100.0
```

- **影响**：`window` 完全由样本自身决定 → 采样器跑多久，“期望样本数”就是多久。任何提前结束的运行都拿满分覆盖率。头部行里的 `intervalSeconds` / `durationSeconds`（`soak-sample.sh:122-124` 明确写“records the sampling contract so a later audit can recompute expected coverage”）在 `soak-report.py` 里**只被原样回显**（第 326 行 `"soakHeaders"`），从未参与判定。契约等于不存在。
- **修复方向**：`expected = int(durationSeconds // intervalSeconds) + 1`（优先取头部，缺失才回退观测跨度并在报告里标注 `contractSource: "observed"`）；coverage 之外再输出 `observedHours / contractedHours`。

### R-12 [P2] `soak-report.py` 解析了 `status` 却从不据此判定
`scripts/soak-report.py:177-180,341`

```python
status_counts[key] = status_counts.get(key, 0) + 1
...
"statusCounts": status_counts,
```

- **影响**：`status` 只被统计不被断言。`RuntimeHealth.cs:72-78` 在“没有任何 upstream 注册”时给出 `status="unknown"`，`HealthCommand` 加 `--offline` 时给出 `"offline"`；这两种样本与 `degraded` 一样完全可能 PASS（`unknown` 时 `upstreams` 为空 → 没有任何 disconnect 规则会命中）。`docs/OPERATIONS_RUNBOOK.md:248` 把 `unknown` 列为合法状态值，但 soak 验收没有对它的语义。
- **修复方向**：加入 `status == "healthy"` 的占比规则（例如 `degradedSamples/…`、`unknownSamples` 必须为 0），并把 `offline` 视作“无效样本”而非健康样本。

### R-13 [P2] `ops-health-alert.sh`：告警状态文件无锁、写失败与“有告警”共用退出码 1
`scripts/ops-health-alert.sh:310-322,335-337`

```bash
prev="{}"
if [[ -r "$STATE_FILE" ]]; then prev="$(cat "$STATE_FILE" 2>/dev/null || echo '{}')"; fi
mkdir -p "$(dirname "$STATE_FILE")" 2>/dev/null || true
report="$(evaluate "$snapshot" "$prev")"
printf '%s' "$snapshot" >"$STATE_FILE"
chmod 600 "$STATE_FILE" 2>/dev/null || true
...
if [[ "$count" != "0" ]]; then exit 1; fi
```

- **影响**：
  1. **无锁读改写**：两个 cron 重叠（默认 5 分钟一次 + 慢查询）会读到同一份 `prev`，各自告警一次；若 `RECONNECT_ALERT` 突增，会出现重复告警或漏告警。`reconnectCount` 增量语义依赖“上次快照”，竞态直接破坏“一次重连一条告警”的承诺。
  2. **退出码歧义**：`STATE_FILE` 默认 `/var/lib/natter/log/alert-state.json`，目录不可写/权限不足时 `printf > "$STATE_FILE"` 在 `set -e` 下退出 **1**，与“alert raised”完全同码，监控会把它当成真告警，反之也可能把真告警当成状态文件写失败。
  3. `mkdir -p ... || true` 掩盖了目录创建失败。
- **修复方向**：用 `flock "$STATE_FILE.lock"` 包住读-判-写；状态文件写失败用独立退出码（如 5）；`set +e` 显式判断写结果后 `exit 5`；输出里增加 `stateWritten: true/false`。

### R-14 [P2] `ops-health-alert.sh`：`status != healthy` 把 `unknown`/`offline` 一律报成告警
`scripts/ops-health-alert.sh:79-81`

```jq
if ($now.status // "") != "healthy" then
  {id:"status-not-healthy", severity:"warning",
   message:("health.status=" + ($now.status // "unknown") + " (Tier 2 absence is expected for Tier 1-only deployments)")}
```

- **影响**：`--file` 模式喂 `natter health --offline --json`（runbook §5.3 的 fallback 正是它）时 `status` 恒为 `offline`/`unknown` → 每次巡检必出 1 条 warning，`exit 1`。告警噪声会让人习惯性忽略 `disconnect` 这类真告警（告警疲劳）。
- **修复方向**：把 `unknown`/`offline` 与 `degraded` 区分开（`severity: info` 或不产生 alert，只写 `statusNote`）；只在 `upstreams` 非空且确实 `connected=false` 时告警。

### R-15 [P3] `p2-collect.sh` 文档承诺输出 `env.txt`，实际从不生成
`scripts/p2-collect.sh:6-8`

```bash
# Writes into <run>/evidence/collect-<HHmmss>/:
#   health.json, serve.log.tail, sqlite.txt, discord-tail.json, env.txt
```

- **影响**：验收记录里会出现“文件清单对不上”的歧义；同时脚本实际把 Discord 消息原文落在 `discord-tail.json`（0644 默认权限），与“Never prints secrets”的定位容易被误读为“无敏感内容”。
- **修复方向**：删掉 `env.txt` 或真的写一份“已脱敏的环境变量名清单”；`chmod 600` 输出目录。

### R-16 [P3] `p2-serve.sh` `stage()` 的未引用通配符 + 不清空目标
`scripts/p2-serve.sh:26-27`

```bash
cp "$src/bin/Debug/net10.0/lib/"* "$RUN_DIR/plugins/all/$id/lib/"
```

- **影响**：`lib/` 不存在时 `set -e` 直接中断，前面已 stage 的插件目录留在半成品状态；未加 `--skip-build`，用户无法先构建再收集。
- **修复方向**：`rm -rf` 目标后重建（同 R-08）；`cp -a "$src/bin/Debug/net10.0/lib/." "$dest/lib/"` 避免 `*` 不匹配。

### R-17 [P3] `stage-tier1-plugins.sh`：无 trap 的 staging 目录 + 硬编码 TFM + GNU-only `install`
`scripts/stage-tier1-plugins.sh:172-195,138,176`

```bash
local tmp="$dest.staging.$$"
rm -rf "$tmp"; mkdir -p "$tmp/lib"
cp -a "$bin_dir/lib/." "$tmp/lib/"
install -m 0644 "$source_dir/plugin.json" "$source_dir/config.schema.json" "$tmp/"
...
rm -rf "$dest"
mv "$tmp" "$dest"
```

- **影响**：①`$tmp` 只有在第 187 行（parity 不符）和第 140/194 行被清理，中途失败（`cp -a` 磁盘满、`install` 失败）会留下 `*.staging.<pid>` 垃圾；②`bin_dir=.../bin/$CONFIGURATION/net10.0`（第 138 行）写死 TFM，升级到 `net11.0` 时脚本报“missing build output”而不是明显的 TFM 错配；③`install -m` 是 GNU coreutils，macOS（`publish.sh` 明确支持 osx-*）上没有；④`rm -rf "$dest"` + `mv` 之间有一个插件目录不存在的窗口，运行中的 host 若此时 reload 会看到坏插件。
- **修复方向**：加 `trap 'rm -rf "${tmp:-}"' EXIT`；TFM 从 csproj 读取（`dotnet msbuild -getProperty:TargetFramework`）或作为 `-f` 参数；用 `cp` + `chmod` 替代 `install -m`；改用 `mv "$tmp" "$dest.new" && mv ...` 的就地替换（`renameat2(RENAME_EXCHANGE)` 不可移植，至少先 `mv "$dest" "$dest.old"`）。

### R-18 [P3] `live-serve.sh` 用 GNU-only `xargs -r`
`scripts/live-serve.sh:56`

```bash
ls -t "$SERVE_DIR"/*.log 2>/dev/null | head -1 | xargs -r tail -n 20
```

- **影响**：macOS/BSD `xargs` 没有 `-r`，`--status` 直接报 `xargs: illegal option`（仓库 `publish.sh` 声明支持 `osx-*`，开发者在 mac 上用这条路径会失败）。
- **修复方向**：`log="$(ls -t ... | head -1)"; [[ -n "$log" ]] && tail -n 20 "$log"`。同类问题：`p2-serve.sh:48`。

### R-19 [P3] `scripts/*` 的权限位不齐（0711 / 0600）
`scripts/build.sh`、`test.sh`、`publish.sh`、`publish-android.sh`、`live-*.sh`、`p2-*.sh` = `-rwx--x--x`(0711)；`scripts/publish.ps1` = `-rw-------`(0600)

- **影响**：0711 的脚本对 group/other **不可读**，非属主用户 `./scripts/build.sh` 会在执行时被内核拒绝读取（`Permission denied`，报错点很迷惑）；`publish.ps1` 600 在共享构建机上不可用。git 会保存执行位但**不保存** 0600，所以 CI 不受影响，本地/共享机器会踩到。
- **修复方向**：`git update-index --chmod=+x` 统一到 0755，脚本类统一 0755、纯文档类 0644；加一条 `.editorconfig`/CI 检查（`find scripts -type f -perm -u+x ! -perm -o+r`）。

### R-20 [P3] `ops-health-alert.sh` 的 `RECONNECT_ALERT` 直接进 `--argjson`
`scripts/ops-health-alert.sh:33,170`

```bash
RECONNECT_ALERT=${RECONNECT_ALERT:-1}
...  --argjson reconnect_alert "$RECONNECT_ALERT" ...
```

- **影响**：运维按直觉写 `RECONNECT_ALERT=false` → `jq: invalid JSON text`，脚本以非 0/1 的码退出，告警链断在半路。
- **修复方向**：接受 `0|1|true|false`，归一化成 0/1 再 `--argjson`。

### R-21 [P3] `ops-health-alert.sh` 用 `startswith` 找上一份样本，多个同协议 upstream 会互相顶替
`scripts/ops-health-alert.sh:76`

```jq
def prevof($name): (($prevups // []) | map(select((.source // "") | startswith($name))) | first);
```

- **影响**：`source` 形如 `discord:discord:1`、`discord:discord:2` 时，`prevof("discord:discord:2")` 不会误匹配，但 `prevof("discord:discord:")` 或前缀重叠（`dingtalk:dingtalk:1` vs `dingtalk:dingtalk:10`）会取错 baseline → `reconnectCount` 增量算错。当前部署只有单个 upstream 所以没暴露。
- **修复方向**：改成精确 `source` 匹配（`select(.source == $name)`），或按 `source` 建 map 后 `$map[$name]`。

### R-22 [P3] `discord-egress-probe.sh` 把两个端点的时延混进同一个 p95
`scripts/discord-egress-probe.sh:91-105`

- **影响**：`discord.com/api/v10/gateway`（REST）与 `gateway.discord.gg`（WS 握手）混排后取 p95，注释里“p95 预算=2000ms”实际是两种分布混合后的分位数；另外 `-i` 无数字校验（`sleep "abc"` 报错但不退出，因为脚本是 `set -uo pipefail` 无 `-e`）。
- **修复方向**：按 label 分别聚合（`results_rest[]` / `results_ws[]`），判定用 ack 相关的那一个；`-i` 加 `[[ $INTERVAL =~ ^[0-9]+$ ]]`。

### R-23 [P3] `webconsole-e2e.sh` 的 60s 预算与端口占用
`scripts/webconsole-e2e.sh:95,99-106`

```bash
dotnet "$CLI" --plugin-dir "$WORK/plugins" serve --duration 60 --health-port "$HEALTH_PORT"
```

- **影响**：整个矩阵（构建 + 20+ 次 HTTP 断言 + rollback）必须挤进 60s，慢机器上 host 会在断言中途退出，表现为随机失败；`rm -rf "$WORK"`（第 72 行）发生在启动前，但端口若被上次残留进程占用，只会得到“console did not start”+ 日志尾部，不指向真正原因。
- **修复方向**：`--duration 300` 或干脆不设（用 trap 结束）；启动前 `lsof -i:$PORT`/`ss -ltnp` 预检并给出明确错误。

### R-24 [P3] `rotate-secret.sh --check` 输出每个 secret 的长度 + SHA-256 前缀
`scripts/rotate-secret.sh:49-52,66-70`

```bash
fingerprint() { printf '%s' "$1" | sha256sum | cut -c1-8; }
printf '  %-24s len=%s sha256=%s\n' "$name" "${#value}" "$(fingerprint "$value")"
```

- **影响**：`--check` 的输出常被贴进工单/截图。`len + sha256 前缀` 对低熵或格式固定的 token（例如 72 字符 base64 bot token 的固定结构）构成离线校验器，配合常见泄漏库可显著缩小搜索空间。
- **修复方向**：`--check` 默认只输出 `<set>/<empty>`，需要 fingerprint 时显式 `--fingerprint`；或改用 HMAC（key 来自 `--key-file`）而非裸 SHA-256。

---

## 2. CI：`.github/workflows/*` + MSBuild

### R-25 [P1] `NuGetAudit=false` 全局关闭审计，CI 的“依赖漏洞扫描”只是局部补丁
`Directory.Build.props:18-19` ↔ `.github/workflows/ci.yml:86-97`

```xml
<NoWarn>$(NoWarn);CS1591;NU1701;NU1702;NU1900</NoWarn>
<NuGetAudit>false</NuGetAudit>
```
```yaml
- name: Dependency vulnerability scan
  run: |
    dotnet restore tests/Natter.Framework.Tests/Natter.Framework.Tests.csproj -p:NuGetAudit=true
    dotnet list tests/Natter.Framework.Tests/Natter.Framework.Tests.csproj package --vulnerable --include-transitive | tee vulnerability.txt
    if grep -q "has the following vulnerable packages" vulnerability.txt; then exit 1; fi
```

- **影响**：
  1. 全仓 `dotnet restore/build`（含开发者本地、`scripts/build.sh`、Docker 构建）都不产出 NU1901-NU1904 告警，`NU1900` 还被 `NoWarn` 显式压掉 → 审计**默认关闭**。
  2. CI 只对 `tests/Natter.Framework.Tests` 一个工程重开审计。`plugins/Natter.Plugin.WebConsole`、`plugins/Natter.Plugin.PromptLibrary`、`samples/*` 不在该工程的引用图里（`tests/Natter.Framework.Tests.csproj:23-42`），而 WebConsole/PromptLibrary 会进镜像/发布物 → 它们的依赖从未被审计。
  3. 断言语义是 grep 一段英文输出（`"has the following vulnerable packages"`），SDK 文案一变就静默变成 green。
- **修复方向**：删掉 `NuGetAudit=false`（或只在 `dotnet build` 的快速路径用 `-p:NuGetAudit=false`，CI 不加）；CI 改为对 `Natter.slnx` 全量 `dotnet restore` + 各发布宿主工程 `dotnet list package --vulnerable --include-transitive`，用 `--format json` 输出后 `jq '.projects[].frameworks[].transitivePackages[]? // empty'` 判定而非 grep。

### R-26 [P2] NuGet 缓存 key 漏掉 `Directory.Build.props`
`.github/workflows/ci.yml:36-42`

```yaml
key: nuget-${{ runner.os }}-${{ hashFiles('global.json', 'Directory.Packages.props', '**/*.csproj', 'Natter.slnx') }}
restore-keys: |
  nuget-${{ runner.os }}-
```

- **影响**：`Directory.Build.props` 影响 restore 图（`NuGetAudit`、`NoWarn`、`Deterministic`、TFM 相关属性），改它不会让缓存失效 → “改了 props 但 CI 行为没变”的鬼故事；`packages.lock.json`（若启用锁定）也不在 key 里。`restore-keys` 又很宽（只按 OS），缓存 key 精度实际被回退键破坏。
- **修复方向**：key 里加 `Directory.Build.props`、`**/packages.lock.json`；`restore-keys` 收窄到 `nuget-${{ runner.os }}-${{ hashFiles('Directory.Packages.props') }}`。

### R-27 [P2] CI 从未构建/测试 `PromptLibrary`，也从未构建解决方案
`.github/workflows/ci.yml:44-84` ↔ `Natter.slnx:1-29`

- **影响**：`tests/Natter.Plugin.PromptLibrary.Tests`（`Natter.slnx:28`）与 `plugins/Natter.Plugin.PromptLibrary`（`Natter.slnx:23`）在两条工作流里都不出现（`deploy-verify.yml:60-69` 的 shellcheck 名单也没有它）。这两个工程的测试在本仓 CI 中**永远不跑**，`dotnet test`/PR 门禁对它免疫；同理 `Natter.Chat.Satori` 以外的若干工程只靠传递引用“顺带编译”。
- **修复方向**：把 `restore/build/test` 换成 `dotnet build Natter.slnx` + `dotnet test Natter.slnx`（Android 工程用 `-p:EnableAndroidTargeting=false` 或单独 job 排除），或加一个 `for proj in $(sed -n 's/.*Path="\([^"]*\)".*/\1/p' Natter.slnx)` 的对账步骤，确保 slnx 里每个 `*Tests.csproj` 都被 CI 引用到。

### R-28 [P2] `deploy-verify.yml` 上传的 `soak-report.json` 在 `image` job 里根本不存在
`.github/workflows/deploy-verify.yml:182-193`

```yaml
- name: Upload staging manifest
  if: always()
  with:
    name: natter-image-verification
    path: |
      plugins.txt
      inspect-discord.txt
      inspect-deepseek.txt
      healthcheck.json
      soak-report.json
    if-no-files-found: ignore
```

- **影响**：`soak-report.json` 由 `scripts` job（第 112 行）生成，`image` job 是另一个 runner、另一个工作目录。`if-no-files-found: ignore` 把这个错误彻底藏起来 → 证据包永远缺一份 soak 自检产物，而 CI 是绿的。
- **修复方向**：把 `soak-report.json` 挪到 `scripts` job 的上传列表；或用 `actions/upload-artifact` + `download-artifact` 显式传递；把 `if-no-files-found` 改成 `error`。

### R-29 [P3] `deploy-verify.yml` 的固定 `sleep 12` 与“对齐空格”断言
`.github/workflows/deploy-verify.yml:175,156`

```yaml
sleep 12
...
grep -q 'valid          : True' "inspect-${plugin}.txt"
```

- **影响**：①`sleep 12` 是纯时序假设（容器冷启动 + JIT + 插件加载），慢 runner 上偶发 `status=exited`；②`'valid          : True'` 依赖 `PluginsInspectCommand.cs:29` 的 11 个空格，任何对齐调整（`PadRight` 重构）都会让断言失效，而失效方式只表现为“grep 找不到”。
- **修复方向**：用 `docker wait`/轮询 `docker inspect .State.Status` 到 `running` 或 `exited`（带上限）；断言改为 `plugins inspect --json | jq -e '.isValid == true'`（`PluginsInspectCommand.cs:20-23` 已经支持 `--json`）。同样问题：`deploy/docker/verify-image.sh:130-131`。

### R-30 [P3] `deploy-verify.yml` 的 PR 触发路径不含 `src/**`，镜像验证只在合并后跑
`.github/workflows/deploy-verify.yml:13-27`

```yaml
on:
  push:
    paths: [..., "src/**", ...]
  pull_request:
    paths: ["deploy/**", "scripts/**"]
```

- **影响**：改 `src/**`（影响镜像内容、`natter` 参数、健康端点）不会在 PR 上触发镜像构建与 `/health` 验证，只能在 main 上发现。`Directory.Build.props`/`global.json` 同理。
- **修复方向**：PR 路径与 push 路径取齐（或至少包含 `src/**`、`Directory.Build.props`、`Directory.Packages.props`）。

### R-31 [P3] 工作流未固定 action SHA；分析器全局关闭
`.github/workflows/ci.yml:29,32,37,101`、`.github/workflows/deploy-verify.yml:49,184`、`Directory.Build.props:8`

- **影响**：`actions/checkout@v5` 等用 major tag，tag 可被上游移动（供应链风险）；`EnableNETAnalyzers=false` 且 `GenerateDocumentationFile=false`，意味着**整个仓库没有任何 Roslyn 分析器门禁**（CA2xxx/CA1xxx、IDE 规则全部沉默），nullability 之外的静态缺陷只能靠人看。
- **修复方向**：action 固定到 commit SHA（Dependabot 负责升级）；至少给 `Natter.Cli*` 打开 `EnableNETAnalyzers=true` + `<AnalysisLevel>latest-recommended</AnalysisLevel>` 并按目录 `NoWarn` 白名单。

---

## 3. CLI 设计：`Natter.Cli*`

### R-32 [P1] `/health` 与 `health --json` 只脱敏了一半字段
`src/Natter.Cli.Core/Commands/RuntimeHealth.cs:61-64`

```csharp
value.ReconnectCount,
value.LastError,                    // 未脱敏
value.SendFailures,
value.LastSendError,               // 未脱敏
value.LastDisconnectError is null ? null : Sanitize(value.LastDisconnectError),
value.LastDisconnectCode);
```

- **影响**：`Sanitize`（第 11-16 行）只覆盖 `LastDisconnectError` 和插件失败消息。`LastError`/`LastSendError` 直接进入 `/health` 200 响应体与 `natter health --json` 输出——这两条正是最容易带上上游凭据的字段（Discord REST 4xx 常回显 `Authorization` 片段/url，provider 5xx 常回显 `apiKey`）。`HealthHttpServer.cs:59-77` 无认证，同机任意进程 `curl 127.0.0.1:5099/health` 即可；`scripts/soak-sample.sh`、`verify.sh:143`、`verify-image.sh:211` 还会把这些字段**落盘到证据文件**。
- **修复方向**：在 `RuntimeHealth.CreateAsync` 里对所有自由文本字段统一过 `Sanitize`（最好在 record 构造处做，避免漏字段）；`SecretPattern` 补 `Bot\s+[A-Za-z0-9._-]{20,}`、`mfa\.`、`[A-Za-z0-9_-]{23,28}\.[A-Za-z0-9_-]{6,7}\.[A-Za-z0-9_-]{27,}`（Discord token 形如 `xxx.yyy.zzz`）；证据采集脚本再加一层“已知 secret 值比对”（`verify-image.sh:250-265` 已有这个思路，可复用）。

### R-33 [P1] 任何非 `health --offline` 命令都会拉起完整 host —— 包括文档让你在 live host 上执行的巡检命令
`src/Natter.Cli.Core/Cli/NatterCli.cs:96-124`；对照 `docs/OPERATIONS_RUNBOOK.md:119-121,134,294`

```csharp
var offlineHealth = args.Length > 0 && string.Equals(args[0], "health", ...) && args.Contains("--offline", ...);
if (!offlineHealth) { await host.StartAsync(runToken); ... }
```

- **影响**：
  - `natter version`、`natter help`、`natter plugins list --json`、`natter upstreams --json` **全部**会 `StartAsync`：加载 NPS 插件、连接 Discord 网关。单实例锁只在 `args[0] == "serve"` 时获取（`NatterCli.cs:41-44`），因此这些命令可以与正在运行的 `serve` 并存 → 同一 bot token 开第二个 gateway 会话，Discord 侧表现为旧会话被踢/4004，`reconnectCount` 突增。
  - 讽刺的是 `docs/OPERATIONS_RUNBOOK.md:119-121`（“启动后检查”）直接让运维在主实例运行时跑 `plugins list --detail` 和 `upstreams --json`，而第 134 行自己写明了这个危害。文档与实现同时在违反。
  - 对 `serve` 之外的写库命令（`memory import`/`remember`/`forget`）同样无锁，与 `serve` 并发写同一 state 目录（SQLite 有锁但 `-wal` 竞争会放大 `SQLITE_BUSY`）。
- **修复方向**：①把 host 启动改成**按命令声明**（`CliCommandDescriptor.RequiresHost: true/false`），`version/help/events/plugins inspect/plugins config validate/doctor(可选)` 走 build-only 路径；②把 `StateDirectoryLock` 从“仅 serve”改成“所有会打开 state/网关的命令”，并对只读命令加共享锁（`flock -s` 语义）；③文档侧先修 `§2.3` 的命令清单。

### R-34 [P2] `health --offline` 跳过 `StartAsync`，于是插件从未激活、状态永远是 `unknown`
`src/Natter.Cli.Core/Cli/NatterCli.cs:99-124` ↔ `src/Natter.Infrastructure/Composition/NatterApplicationBuilder.cs:150-215`、`NatterHost.cs:47-77`

- **影响**：插件扫描发生在 `Build()`，但 **激活/适配器绑定/upstream 连接发生在 `host.StartAsync()`**（`OnHostStartedAsync` 的注释也写着“after the host and NPS runtime started”）。因此 `health --offline` 得到的快照是“插件 activated=0、upstreams=[]、status=unknown”。它被当作：runbook §0/§2.3/§5.3 的推荐 fallback、`deploy/systemd/verify.sh:153-159`“proves config/plugin wiring”的证据、`soak-sample.sh:174` 的离线采样源——三处都建立在一个几乎恒为 `unknown` 的输入上。`verify.sh` 还会把这个 `unknown` 存成 `health-offline.json` 证据。
- **修复方向**：`--offline` 语义改成“加载配置 + 激活插件 + **不连接 upstream**”（NPS runtime 分层：`InitializeAsync` vs `ConnectAsync`），或在 `health --offline` 输出里明确 `plugins.declared` / `plugins.activated=0 (offline)` 并让 `verify.sh` 用 `plugins.declared` 断言。

### R-35 [P2] `serve` / `health` 在有失败插件或 `degraded` 时仍返回 0
`src/Natter.Cli.Core/Commands/RuntimeCommands.cs:100,173`

```csharp
return CliExitCodes.Success;   // ServeCommand 结束
...
return CliExitCodes.Success;   // HealthCommand，无论 status 是什么
```

- **影响**：`health` 作为 CI/探针使用时无法区分“健康”和“插件全挂了”。`CliExitCodes`（`Cli.Abstractions/CliCommand.cs:4-10`）只有 `0/1/2/130`，没有“不健康”这一档；`PluginsInspectCommand` 却用 `IsValid ? Success : Failure` 表达了同类语义——同一 CLI 里“校验失败”有两种编码。`ops-health-alert.sh` 因此不得不自己解析 JSON 并自带一套退出码，形成第二套事实标准。
- **修复方向**：定义 `CliExitCodes.Unhealthy = 3`（或 `Unavailable=69`、`Degraded=75` sysv 风格），`health`/`serve --probe` 按 `status` 返回；`CliExitCodes` 文档化并把 `ops-health-alert.sh` 的映射对齐。

### R-36 [P2] 全局选项缺值时静默忽略，且会把下一个选项吃掉
`src/Natter.Cli.Core/Cli/NatterCliOptions.cs:40-73`

```csharp
case "--plugin-dir":
    if (index + 1 < args.Count)
        options.PluginDirectories.Add(args[++index]);
    break;                                  // 没有 else/错误
...
case "--state-dir":
    if (index + 1 < args.Count) options.StateDirectory = args[++index];
```

- **影响**：
  1. `natter --plugin-dir`（忘写值）→ 静默无插件运行，退出 0，用户以为插件已加载。
  2. `natter --config-dir --state-dir /tmp/x serve` → `ConfigDirectory == "--state-dir"`，`/tmp/x` 变成位置参数 → `Unknown command: /tmp/x` 或更糟：配置目录变成一个字面量目录名，`serve` 起来后读不到 profiles 却“成功”。因为解析器不校验“值不能以 `-` 开头”。
  3. `--plugin-dir a --plugin-dir b` 累加，而 `--config-dir a --config-dir b` 后者覆盖前者，语义不一致且无告警（重复选项）。
  4. 没有 `--` 终止符（`CliParser` 有，全局解析器没有），无法传以 `--` 开头的插件路径。
- **修复方向**：缺值 → `Usage(2)` 并提示；拒绝把 `-`/`--` 开头的 token 当值（除非用 `=` 形式）；重复标量选项 → 报错或至少 `Warning` 到 stderr；支持 `--`。

### R-37 [P2] 进程级解析器对未知选项“接受并吞掉下一位”
`src/Natter.Cli.Core/Cli/CliParser.cs:53-59`

```csharp
if (option is null)
{
    if (value is null && index + 1 < tokens.Count && !tokens[index + 1].StartsWith('-'))
        value = tokens[++index];
    options[name] = value ?? "true";        // 未知选项被静默接受
    continue;
}
```

- **影响**：拼错选项（`--jsno`）不会报错，而是被当成未知选项存起来，同时把紧跟的位置参数当值吃掉：`natter plugins inspect --jsno /path/to/plugin` → `/path` 丢失，`GetPositional(0, ".")` 退化为 `"."`，于是 **inspect 了错误的目录并返回 0/1 而不是 Usage**。脚本化使用（`stage-webconsole.sh` 提示的 `plugins inspect "$PLUGIN_ROOT/discord"`）一旦拼错就静默降级。
- **修复方向**：未知选项 → `Failure($"unknown option '--{name}'")`（`natter` 自己的 `--` 语义照旧）；`CliParser` 加一个 `AllowUnknownOptions` 开关给 `exec` 场景。

### R-38 [P2] `--no-plugins` 顺带禁掉 `NATTER_CONFIG_DIR` / `NATTER_STATE_DIR` / `NATTER_PROFILE`
`src/Natter.Cli.Core/Cli/NatterCliOptions.cs:100-108`

```csharp
public NatterCliOptions AddDefaultPluginLocations()
{
    if (!LoadPlugins) return this;                                // ← 提前返回
    ConfigDirectory ??= Environment.GetEnvironmentVariable("NATTER_CONFIG_DIR");
    StateDirectory ??= Environment.GetEnvironmentVariable("NATTER_STATE_DIR");
    ProfileOverride ??= Environment.GetEnvironmentVariable("NATTER_PROFILE");
```

- **影响**：`--no-plugins` 的本意是“不扫描插件目录”，实际把**配置目录、状态目录、profile** 也一起丢弃。Docker 镜像（`deploy/docker/Dockerfile:76-77` 用 `NATTER_CONFIG_DIR=/config`、`NATTER_STATE_DIR=/state`）与 `natter --no-plugins serve` 组合时，状态会写到 `AppContext.BaseDirectory/state`（`/app/state`，`read_only` 根文件系统上直接写失败），并**绕过单实例锁**（`StateDirectoryLock.TryAcquire` 在 `StateDirectory` 为空时返回 null）→ 双实例风险。
- **修复方向**：把环境变量解析移出 `if (!LoadPlugins)`；只让 `NATTER_PLUGIN_DIR` 与本地 `plugins/` 的探测受 `LoadPlugins` 控制。

### R-39 [P2] 单实例锁只捕 `IOException`，目录不可写时进程带栈崩溃
`src/Natter.Cli.Core/Cli/StateDirectoryLock.cs:13-33`

```csharp
var directory = Path.GetFullPath(options.StateDirectory);
Directory.CreateDirectory(directory);
...
catch (IOException) { console.Error.WriteLine("Another Natter serve instance ..."); return null; }
```

- **影响**：`Directory.CreateDirectory` 与 `FileStream` 在“目录属 root、0700”时抛的是 `UnauthorizedAccessException`（`SystemException`，不是 `IOException`）；`Path.GetFullPath` 遇到非法字符抛 `ArgumentException`。这些异常从 `NatterCli.RunAsync`（第 41 行的 `using var` 初始化器，不在 try 内）逸出 → `Program.cs:23` 无 try → **未处理异常 + 栈 + 以 134/139 退出**。`deploy/README.md:111-112` 描述的“bind mount /state 会导致 host 崩溃”正是这条路径，而 `verify-image.sh` 恰好用 `--tmpfs /state:uid=10001` 绕开了它，所以 CI 看不到。
- **修复方向**：`catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)` → 统一打印“state 目录不可用：<原因>”，返回 `CliExitCodes.Failure`；`Directory.CreateDirectory` 后显式 `File.SetUnixFileMode(dir, 0700)`（当前目录用默认 umask 创建，`Dockerfile` 里 `/state` 的属主依赖靠卷继承，靠不住）。

### R-40 [P2] 帮助文本缺了三个真实存在的全局选项
`src/Natter.Cli.Core/Cli/HelpText.cs:32-37` ↔ `NatterCliOptions.cs:59-73,76-85`

```
Global options:
  --plugin-dir <path>   ...
  --no-plugins          ...
  --verbose, -v         ...
  --trace-events        ...
  -h, --help            ...
```

- **影响**：`--config-dir` / `--plugin-config-dir` / `--state-dir` / `--profile`（以及 `=` 形式）在解析器里存在、在 runbook 里被大量使用，但 `natter --help` 里没有 → 用户只能靠翻文档；`--trace-events` 却没有 `RenderCommand` 侧的任何说明（它与 `events watch` 的关系不明）。帮助与实现漂移是 CLI 可维护性的头号腐蚀点。
- **修复方向**：把全局选项做成数据（`IReadOnlyList<CliOptionDescriptor>`）由 `HelpText` 渲染，与 `NatterCliOptions.Parse` 共用同一份定义（parse 用 switch 时至少加一个“每个 case 必须在描述表里存在”的单测）。

### R-41 [P2] `--json` 的序列化选项三套并存，输出不稳定
`src/Natter.Cli.Core/Rendering/JsonOutput.cs:10-15,27-31` ↔ `RuntimeCommands.cs:19-23,130-134`、`MemoryCommands.cs:11-15,94-98,155-160,193-198`、`PluginsInspectCommand.cs:22`

```csharp
// JsonOutput：WriteIndented = true + JsonStringEnumConverter(camelCase)
// RuntimeCommands/MemoryCommands：各自的 static JsonOptions，WriteIndented = false，无 enum 转换器
```

- **影响**：`version --json`（缩进、枚举 camelCase）与 `health --json`（不缩进、枚举 ToString）与 `plugins inspect --json`（缩进）**格式不一致**；`memory export` 又用 `WriteIndented: true` + 默认 `JsonStringEnumConverter()`（PascalCase）。脚本 `jq` 能容忍缩进差异，但“字段大小写/枚举拼写”会变：`PluginState.ToString()` 给 `"Activated"`，`JsonStringEnumConverter(camelCase)` 给 `"activated"`。同一份 JSON 契约在 CLI 内部不成立，下游（WebConsole、`ops-health-alert.sh`、`soak-report.py`）各自做兼容 → `soak-report.py:249` 已经在用 `has_field` 猜测 payload 版本。`--json` 输出**没有任何 golden-file 测试**。
- **修复方向**：只保留 `JsonOutput.Options` 一个入口（需要紧凑输出时用 `JsonOutput.SerializeCompact`）；枚举一律 `toLowerInvariant` 或统一 enum converter；为每个 `--json` 命令加一份 `tests/.../golden/*.json` 快照测试。

### R-42 [P2] `HealthHttpServer`：无认证、无并发上限、`/health` 是唯一路由
`src/Natter.Cli.Core/Commands/HealthHttpServer.cs:24,55,63-68`

```csharp
_listener.Prefixes.Add($"http://127.0.0.1:{port}/");
...
_ = HandleAsync(context);        // fire-and-forget，无并发限制、异常被吞
...
if (context.Request.Url?.AbsolutePath != "/health") { 404 }
```

- **影响**：①绑定 127.0.0.1 是对的，但**同机任意本地账号**（含容器内其他进程、`--network=host` 场景）都能读到 `lastError` 等字段（与 R-32 叠加成实际泄密面）；②`_ = HandleAsync(context)` 无并发上限，`_snapshot()` 走 SQLite 查询，`ab`/若干 curl 并发即可放大磁盘压力并让 `serve` 的线程池饥饿；③只认 `/health`，没有 `/healthz`/HEAD/`/metrics` 且无 `Cache-Control`；④`Start()` 抛 `HttpListenerException` 时（端口被占）发生在 `ServeCommand` 打印完 startup summary 之后，用户看到“serve 起来了”再看到 `error:`。
- **修复方向**：加 `--health-token`（`Authorization: Bearer`）或 Unix socket 选项；`SemaphoreSlim` 限流（如 8）+ `CancellationTokenSource` 超时；`/health?format=json|text`；端口占用时先探测再打印 summary，或把 `Start()` 移到 summary 之前。

### R-43 [P3] `CliLoggerProvider` 无条件打印完整栈，`CliRunner` 的 `--verbose` 开关形同虚设
`src/Natter.Cli.Core/Logging/CliLoggerProvider.cs:53-54` ↔ `src/Natter.Cli.Core/Cli/CliRunner.cs:95-99`

```csharp
if (exception is not null) _console.Error.WriteLine(exception.ToString());
```
```csharp
_logger.LogError(ex, "Command '{Command}' failed.", command.Name);
console.Error.WriteLine($"error: {ex.Message}");
if (_options.Verbose) console.Error.WriteLine(ex.ToString());
```

- **影响**：`LogError` 走的 provider 已经把 `ex.ToString()` 打到 stderr，所以“只有 `--verbose` 才输出栈”不成立；默认 `LogLevel.Warning` 下每次命令异常都吐完整栈（含内部路径），脚本/CI 日志噪声大，且与 `--json` 场景混流（stderr，尚可）。
- **修复方向**：`CliLogger.Log` 里只在 `logLevel >= LogLevel.Debug || verbose` 时打印 `exception.ToString()`，把 verbose 传给 provider。

### R-44 [P3] 取消与“时长到期”共用 130
`src/Natter.Cli.Core/Cli/CliRunner.cs:88-92` ↔ `src/Natter.Cli.Core/Cli/NatterCli.cs:105-115`

- **影响**：`natter serve --duration 60` 正常走完返回 0（`ServeCommand.cs:95-97` 吞掉 `OperationCanceledException`），但 `events watch --seconds 5` 在 `commandBudget` 到期时抛 `OperationCanceledException` → `CliRunner` 打 `cancelled.` 并返回 130。`scripts/p2-signal-smoke.sh` 用 130/0 区分“信号”和“失败”，`--duration` 语义让两处依赖 130 的脚本容易误判。
- **修复方向**：给“预算到期”单独语义（`Cancelled`=130 只留给 SIGINT/SIGTERM），例如 `NatterCli` 在 `commandBudget` 取消时把 token 换成 `CancellationToken.None` 或给 handler 传 `CancellationReason`。

### R-45 [P3] `memory export --output` 先写文件再 `chmod 600`
`src/Natter.Cli.Core/Commands/MemoryCommands.cs:177-179`

```csharp
await File.WriteAllTextAsync(path, json, cancellationToken);
if (!OperatingSystem.IsWindows())
    File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
```

- **影响**：写入到 `chmod` 之间存在窗口，文件按 umask 创建（通常 0644），内容是用户可见的记忆（`sensitive` 级别也在内）。`BuiltInCommands.cs:222` 的说明文字明确承诺“0600 JSON file”，实现是“先 0644 再改”。
- **修复方向**：`using var fs = new FileStream(path, new FileStreamOptions { Mode = CreateNew, Access = Write, UnixCreateMode = UserRead|UserWrite })`（.NET 7+ 支持 `UnixCreateMode`），或先 `File.Create` + `SetUnixFileMode` 再写。

### R-46 [P3] 非法数值一律静默回落
`src/Natter.Cli.Abstractions/CliCommand.cs:104-110`、`EventsCommands.cs:57`

```csharp
public int GetIntOption(string name, int fallback) => int.TryParse(GetOption(name), out var value) ? value : fallback;
```
```csharp
var seconds = Math.Clamp(invocation.GetIntOption("seconds", 3), 1, 3600);
```

- **影响**：`--seconds abc`→3、`--seconds 0`→1、`--limit -5`→原值（无校验）、`--health-port abc`→0（等于不启 health 服务器，静默）。CLI 无法表达“你给的值非法”，全部退化成默认行为——与 R-35 的“没有失败码”同源。
- **修复方向**：加 `TryGetIntOption` 并在 descriptor 里声明范围（`CliOptionDescriptor(..., Min:1, Max:86400)`），越界 → `Usage(2)`。

### R-47 [P3] `events watch` 没有 `--json`，而 `events list` 有
`src/Natter.Cli.Core/Commands/BuiltInCommands.cs:108-118` ↔ `EventsCommands.cs:55-74`

- **影响**：`--json` 覆盖不对称（`events watch`/`events clear`/`help`/`exec` 无），`exec` 又无法把子命令 JSON 结果结构化输出，导致 `scripts`/插件只能解析文本。
- **修复方向**：`events watch --json` 输出 NDJSON（一行一个 envelope），`exec --json` 输出 `[{command, exitCode, stdout}]`。

### R-48 [P3] 其余 CLI 细节（合并记录）
- `HealthHttpServer.Dispose`（`HealthHttpServer.cs:37`）用 `_loop?.Wait(TimeSpan)` 同步阻塞等异步循环，Android/UI 上下文里存在死锁风险；应 `DisposeAsync`。
- `StateDirectoryLock.cs:24` `stream.Position = 0` 后重写但不 `SetLength(0)`，旧内容比新内容长时残留尾部字节。
- `HelpText.RenderGlobal`（第 20-29 行）`width = Math.Min(28, max+2)`，命令名超过 26 字符时列不对齐（当前 `plugins config validate` 已 23 字符，接近临界）。
- `VersionCommand.cs:18` 的 `processPath` 在 `--json` 里输出绝对安装路径（证据/工单里会泄漏部署拓扑）。
- `NatterCliOptions.Parse` 在**整条 argv** 上扫描全局选项，子命令自己的同名选项无法存在（例如将来 `plugins store install --root` 与全局 `--plugin-dir` 不冲突，但 `--profile` 会）。

---

## 4. Android 宿主：`src/Natter.Hosting.Android`

### R-49 [P1] Release 清单里没有 `INTERNET` 权限（Debug 有，因为 SDK 自动注入）
`src/Natter.Hosting.Android/Properties/AndroidManifest.xml:1-6`；证据：`obj/Release/net10.0-android/android/manifest/AndroidManifest.xml`

```xml
<manifest xmlns:android="http://schemas.android.com/apk/res/android">
  <application android:label="Natter CLI" android:allowBackup="false" android:supportsRtl="true" />
</manifest>
```

生成的 Release 清单里 **没有任何 `<uses-permission>`**：

```xml
<manifest package="dev.natter.hosting.android" ...>
  <uses-sdk android:minSdkVersion="24" android:targetSdkVersion="36" />
  <application ...>
    <activity android:name="...MainActivity" android:exported="true" ...>
```

- **影响**：Release APK 无法建立任何 socket → Discord 网关、DeepSeek/Spark 全部 `SocketException: Permission denied`。Debug 构建“能用”是因为 .NET for Android 在 Debug 下自动注入 `android.permission.INTERNET`（这也是最常见的“上架后才崩”原因）。`scripts/publish-android.sh` 默认发布 Release（`CONFIGURATION=Release`），`docs/PUBLISHING.md:108` 的发布路径因此**必然产出不可用包**。
- **修复方向**：在 `Properties/AndroidManifest.xml` 显式声明 `<uses-permission android:name="android.permission.INTERNET" />`（如确需 `ACCESS_NETWORK_STATE` 再按需加）；加一条 Release 构建后的 `aapt2 dump permissions` 断言（CI 若有 Android workload 时）。
- **待确认**：是否有意在 Release 里禁用网络（离线 host）？`MainActivity.cs:71` 的 `exec doctor / plugins list / demo / metrics / greetings recent` 全是离线命令，只有明确要起 upstream 才需要网络——但 `Natter.Cli` 的核心价值就是 Discord 网关，故按缺陷处理。

### R-50 [P2] Activity 被重建即再起一个 host；`RunCliAsync` 无取消、无生命周期挂钩
`src/Natter.Hosting.Android/MainActivity.cs:21-45`

```csharp
var console = new AndroidConsole(this, output);
_ = RunCliAsync(console);          // fire-and-forget，无 CancellationToken，无 OnDestroy 停止
```

- **影响**：旋转屏幕/切换深色主题/分屏都会重建 Activity → `OnCreate` 再跑一次 `NatterCli.RunAsync`：同进程内第二个 host、第二次插件 `CleanupAsync`、第二个 state 目录访问、`serve` 场景下第二次 `.lock` 获取失败（`StateDirectoryLock` 的 FileShare 锁在同一进程内会抛 IOException → 静默返回 null，退出 1，用户只看到 `exit code: 1`）。旧任务无人取消，泄漏运行到进程结束。
- **修复方向**：`CancellationTokenSource` 作为字段，`OnDestroy` 里 `Cancel()` + `await` 收尾；`OnCreate` 里加 `if (_runTask is { IsCompleted: false }) return;`；或把 host 移到 `Application` 级单例 + `[Activity(ConfigChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize)]`。

### R-51 [P2] 没有前台服务/通知，host 与 Activity 同生共死
`src/Natter.Hosting.Android/MainActivity.cs:18`、`Properties/AndroidManifest.xml`

- **影响**：一个“长期运行的 Discord host”跑在 Activity 里：切后台几分钟后进程被 GC/Doze 杀死，网关掉线；无 `[Service(ForegroundServiceType="...")]`、无 `POST_NOTIFICATIONS`，也没有 `START_STICKY`。这与 `docs/ANDROID_PLUGIN_HOST.md` 里“Android 宿主可长时间承载插件”的定位矛盾。
- **修复方向**：`ForegroundService` + `NotificationChannel`（API 33+ 需 `POST_NOTIFICATIONS` 运行时授权）+ `WakeLock`；Activity 只做控制面板。

### R-52 [P3] `AndroidConsole` 缓冲区无上限，每次写入全量重设 `TextView.Text`
`src/Natter.Hosting.Android/MainActivity.cs:93,112-125`

```csharp
private readonly StringBuilder _buffer = new();
...
_buffer.Append(text);
snapshot = _buffer.ToString();
...
_activity.RunOnUiThread(() => _view.Text = snapshot);
```

- **影响**：`serve`/`events watch` 长跑 + `--verbose` 时缓冲无限增长，且每条输出 O(n) 复制 + 跨线程全量 SetText → O(n²) 与 UI 卡顿/内存耗尽。`TextView.Text` 每帧跨 Binder 传一个大字符串。
- **修复方向**：环形缓冲（保留最后 N 行，例如 2000）+ `AddView`/`SpannableStringBuilder` 增量追加，或改用 `RecyclerView`+`ListAdapter`。

### R-53 [P3] Keystore 主密钥采用 check-then-create，并发下解密静默失败
`src/Natter.Hosting.Android/AndroidKeystoreSecretProvider.cs:94-113`

```csharp
if (keyStore.GetKey(KeystoreAlias, null) is ISecretKey existing) return existing;
var generator = KeyGenerator.GetInstance(KeyProperties.KeyAlgorithmAes, KeystoreProvider)!;
...
return generator.GenerateKey()!;
```

- **影响**：两个插件/线程同时首次取密钥 → 第二次 `GenerateKey()` 抛 `KeyStoreException`；`TryGet`（第 57-61 行）`catch { return false; }` 把异常吞成“secret 不存在”，插件拿到空 token 后以“未配置”降级，排查成本极高。另外 `Remove` 没有 `lock`（第 87-92 行），与 `Set` 不一致。
- **修复方向**：`lock` 整个 `GetKey`；捕获 `KeyStoreException` 后重读一次；`TryGet` 区分“不存在”和“解密失败”（`ISecretProvider` 若只能返回 bool，至少 `Debug.WriteLine` 打分类日志）。

### R-54 [P3] debug inbox / trusted-keys 从可写目录读取信任锚
`src/Natter.Hosting.Android/AndroidPluginHost.cs:93-131`、`:38-42`

```csharp
#if DEBUG
    AllowUnsigned = true,
#else
    AllowUnsigned = false,
#endif
...
if (Directory.EnumerateFiles(inbox, "*.natter-plugin.zip")) { await store.InstallAsync(package, allowUpdate: true); }
...
var path = Path.Combine(context.FilesDir!.AbsolutePath, "plugin-trusted-keys.txt");
```

- **影响**：`InstallInboxAsync` 对 `FilesDir/plugin-inbox` 下任意 zip 直接 `InstallAsync`（无签名校验，因为 DEBUG 下 `AllowUnsigned=true`），并在任何信任键文件存在时**以该文件为发布者信任锚**。Debug/侧载构建里，任何能写 `FilesDir` 的途径（`adb run-as`、`adb backup` 在 `allowBackup=false` 下被禁，但 debug 构建的 `run-as` 可用）即可提权到“加载任意原生 DLL”。生产构建应把发布者公钥编译进 APK。
- **修复方向**：生产构建把 trusted keys 作为 `AndroidAsset`/`DefineConstants` 嵌进 APK；`InstallInboxAsync` 加 `#if DEBUG` 并在每次安装前要求显式确认；`LoadTrustedKeys` 在非 DEBUG 下忽略 FilesDir。

---

## 5. Docker / systemd

### R-55 [P2] compose 没有任何资源限制
`deploy/docker/compose.yaml:10-67`（全文无 `mem_limit`/`cpus`/`pids_limit`/`ulimits`）

- **影响**：容器 OOM 会拖垮宿主；`serve` 泄漏（soak 的 RSS drift 检查就假定会漂移）会一路吃满整机内存；PID/线程无上限。`read_only`+`cap_drop: ALL`+`no-new-privileges` 做得不错，唯独缺最基础的一层。
- **修复方向**：`mem_limit: 512m`（按 soak 实测 RSS 定）、`mem_reservation`、`cpus: "1.0"`、`pids_limit: 256`、`ulimits.nofile`（对齐 systemd 的 `LimitNOFILE=4096`）。

### R-56 [P2] secret 经 `env_file` 注入，等于对 `docker inspect` 与 `/proc/1/environ` 公开
`deploy/docker/compose.yaml:21-22`、`deploy/docker/live.env.example:2-8`、`deploy/docker/verify-image.sh:184-190`

```yaml
    env_file:
      - ./live.env
```
```bash
# verify-image.sh:184-187  明确承认这条泄漏通道
# `docker inspect` prints .Config.Env, i.e. every secret passed with --env-file. Never store that:
docker inspect "$NAME" | jq 'del(.Config.Env)' >"$EVIDENCE/container-inspect.json"
```

- **影响**：任何有 docker API 权限的账号（docker 组成员、CI runner、监控 agent）可 `docker inspect` 拿到 bot token 与 API key。仓库自己已经把这个通道当风险处理（只敢删证据里的字段），但部署方式仍然是它。镜像本身的 `/health` 也会把这些字段回显（R-32）。
- **修复方向**：改用 `secrets:`（`/run/secrets/...`）+ 在宿主侧用 `natter` 的 secret provider 读文件（`${secret:FILE:...}` 之类），或至少 `--env-file` 换成 PID1 侧读取 + `EnvironmentFile=` 的等价方案；同步更新 `deploy/README.md` 的“secret 只在环境/secret provider”表述。

### R-57 [P2] `install.sh` 的三个目录参数不会写进 unit 文件
`deploy/systemd/install.sh:15-17,36-40,155` ↔ `deploy/systemd/natter.service:15,19-26`

```bash
--release) RELEASE_DIR="${2:-}"; shift 2 ;;
--config-dir) CONFIG_DIR="${2:-}"; shift 2 ;;
--state-dir) STATE_DIR="${2:-}"; shift 2 ;;
...
run install -m 0644 "$UNIT_SRC" /etc/systemd/system/natter.service   # 原样安装，不做替换
```

- **影响**：`sudo deploy/systemd/install.sh --release /srv/natter --state-dir /srv/state` 会正确创建 `/srv/state` 并 chown，但装上去的 unit 仍然 `WorkingDirectory=/opt/natter`、`NATTER_STATE_DIR=/var/lib/natter`、`ExecStart=/opt/natter/natter` → 服务起不来（或起来后用错目录，`.lock` 与 DB 落到 `/var/lib/natter`）。选项看起来生效、实际只影响脚本自己的 mkdir/chown 部分。同理 `docs/OPERATIONS_RUNBOOK.md` §1.1 写的布局与 unit 是硬耦合的，脚本却对外宣称可配。
- **修复方向**：用 `sed`/`envsubst` 生成临时 unit（`@RELEASE_DIR@` 占位符），或明确拒绝非默认路径（`[[ "$RELEASE_DIR" == "/opt/natter" ]] || { echo "unsupported"; exit 2; }`），并在 `verify.sh` 里断言 `systemctl show -p Environment` 与期望一致。

### R-58 [P2] `natter-soak.service` 的 `SuccessExitStatus=0` 是空操作
`deploy/systemd/natter-soak.service:19-20`

```ini
# Sampling must never accumulate failures that page an operator at 3am.
SuccessExitStatus=0
```

- **影响**：`SuccessExitStatus=0` 与默认行为完全相同（0 本来就是成功），注释想表达的“采样失败不算失败”并未实现。`soak-sample.sh` 在“输出目录不可写”时 `exit 3`（`soak-sample.sh:69-70`）→ 每 2 分钟一条 failed unit，`systemctl --failed` 长期红着，真正的失败被噪声淹没。
- **修复方向**：`SuccessExitStatus=0 3`（或 `SuccessExitStatus=0 3 4`），并在脚本里把“不可写”改走 journal + 退出 0 以外的可辨识码；或 `Restart=` 无关，直接把单元改成 `Type=oneshot` + `IgnoreOnIsolate`，用 `systemd-analyze verify` 断言行为。

### R-59 [P2] `verify.sh` 声称只读，却对活跃 SQLite 跑 `integrity_check`
`deploy/systemd/verify.sh:4,161-165`

```bash
# It is deliberately read-only: it never starts/stops the service and never reads secret values.
...
sqlite3 "$STATE_DIR/natter.db" 'pragma integrity_check;' >"$EVIDENCE/db-integrity.txt" 2>&1 || true
```

- **影响**：以读写方式打开正在被 `serve` 使用的库（默认 mode=rw）会创建/触碰 `-wal`/`-shm`，可能触发 checkpoint 与锁竞争，最佳情况下 `integrity_check` 因 `database is locked` / WAL 未决事务返回非 `ok` → `record "db integrity ok" fail` 假失败；最坏情况下与 host 的写事务互相拖慢。与脚本自己的“read-only、不产生写”契约冲突。
- **修复方向**：`sqlite3 "file:$STATE_DIR/natter.db?mode=ro&immutable=0" 'pragma integrity_check;'`（仍可能 busy，需 `-cmd ".timeout 5000"`），或先 `scripts/natter-backup.sh` 复制一份再校验副本；并把这个检查标注成“best-effort”。

### R-60 [P3] Dockerfile 的“restore 层缓存”注释与实现不符
`deploy/docker/Dockerfile:28-39`

```dockerfile
# Copy only what the build needs so source-only edits do not invalidate the restore layer.
COPY global.json Directory.Build.props Directory.Packages.props Natter.slnx ./
COPY build/ build/
COPY src/ src/
COPY samples/ samples/
COPY scripts/stage-tier1-plugins.sh scripts/stage-tier1-plugins.sh
RUN dotnet publish src/Natter.Cli/Natter.Cli.csproj -c "$CONFIGURATION" -o /out /p:UseAppHost=false
```

- **影响**：没有独立的 `dotnet restore` 层，`COPY src/` 与 `RUN publish` 同层 → **任何源码改动都会重跑完整 restore + build**，注释描述的优化不存在（构建时间与镜像层大小都受影响）。
- **修复方向**：`COPY */*.csproj` + `RUN dotnet restore Natter.slnx` 单独成层，再 `COPY` 源码。注意 `stage-tier1-plugins.sh` 也依赖 `dotnet build`，restore 层要覆盖 sample/plugin 工程。

### R-61 [P3] 基础镜像浮动 tag；`EXPOSE` 与“绝不发布端口”注释矛盾
`deploy/docker/Dockerfile:18,67,97-98`、`deploy/docker/compose.yaml:17`

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
# Loopback only: the health port must never be published to the host or the network.
EXPOSE 5099
```

- **影响**：`10.0` 浮动 tag 让同一 commit 在不同时间构建出不同镜像（不可复现，且供应量风险无声漂移）；`EXPOSE` 不发布端口，但配 `docker run -P`/某些编排模板会自动映射，与注释的强约束语义相反。
- **修复方向**：tag 固定到 `10.0.<patch>` 或 digest；删掉 `EXPOSE`（或保留 + 在 README 明确“仅元数据”）。

### R-62 [P3] `verify-image.sh` 重跑会删掉 `--keep` 保留的卷；泄漏扫描跳过 `*.err`
`deploy/docker/verify-image.sh:140-141,258`

```bash
docker rm -f "$NAME" >/dev/null 2>&1 || true
docker volume rm "${NAME}-state" >/dev/null 2>&1 || true
...
if grep -rIl --exclude='*.err' --exclude='*.env' --exclude='live.env*' -F "$value" "$EVIDENCE" >/dev/null 2>&1; then
```

- **影响**：`--keep` 之后为了排障再跑一次同一脚本 → 卷被删，现场丢失；`--exclude='*.err'` 让写入 stderr 的证据文件不参与泄漏扫描（而 `health-http.err`、`health-inside.err`、`container.log` 里的 stderr 完全可能带 token）。
- **修复方向**：删除前 `if [[ "$KEEP" == "1" ]]; then echo "kept, skipping cleanup"; exit 0; fi`；泄漏扫描改为对所有证据文件（含 `.err`/`.log`）扫描，只对 `.env` 源文件排除。

### R-63 [P3] `natter.service` 缺资源上限；`SystemCallFilter` 是否过严待确认
`deploy/systemd/natter.service:62,65`

```ini
SystemCallFilter=@system-service
LimitNOFILE=4096
```

- **影响**：没有 `MemoryMax`/`TasksMax`/`CPUWeight`（与 R-55 同构，systemd 侧同样缺）；`LimitNOFILE=4096` 对“每 upstream 一条 WS + SQLite + HttpClient 连接池”偏紧但可接受。`MemoryDenyWriteExecute` 已被注释正确排除。`@system-service` 是否覆盖 .NET 10 运行时的 `membarrier`/`rseq`/`io_uring` 取决于 libseccomp 版本——**待确认**：建议在目标发行版上跑一次 `systemd-analyze security natter.service` 与一次真实启动并检查 `journalctl -u natter | grep -i seccomp`。
- **修复方向**：加 `MemoryMax=768M`、`TasksMax=256`、`CPUWeight=100`；`SystemCallFilter=@system-service @memlock`（或用 `SystemCallFilter=~@mount...` 黑名单式）并在 runbook 记录已验证的内核/发行版组合。

---

## 6. 文档与配置一致性

### R-64 [P2] `install.sh` 的下一步提示让运维用错误的 `--interval` 审计 soak
`deploy/systemd/install.sh:200-202` ↔ `scripts/soak-sample.sh:28-29`、`deploy/systemd/natter-soak.timer:8-9`、`scripts/soak-report.py:985`

```bash
# install.sh:202
python3 scripts/soak-report.py $LOG_DIR/soak-*.jsonl --interval 300
```
```bash
# soak-sample.sh:29   DURATION=86400  INTERVAL=120
# natter-soak.timer:9  OnUnitActiveSec=2min
# timer 注释：this interval must match scripts/soak-report.py --interval (default 120)
```

- **影响**：timer 每 **120s** 采样，`install.sh` 教人按 **300s** 审计 → `expected = window/300 + 1`，实测样本数是它的 2.5 倍，覆盖率≥100%（不 FAIL）但 **gap 阈值 `interval*1.5=450s` 与 `missingSamples` 全部按错误间隔推算**；更糟的是运维若真按 timer 节奏理解，会把 300s 当“正常间隔”，对 200s 的真实漏采视而不见。这是“统计口径”错误的典型：**采样契约写在三个地方，且互相不一致**（120 / 300 / 默认 120）。
- **修复方向**：单一事实来源——让 `soak-report.py` 从头部行读 `intervalSeconds`（R-11 同源修复），`--interval` 只做覆盖并打印“正在用的 interval 与头部是否一致”；`install.sh` 与 runbook 的示例统一成从 `systemctl show natter-soak.timer -p ...` 推导出的值。

### R-65 [P3] runbook 对同一场景给出两种互相矛盾的 `status` 预期
`docs/OPERATIONS_RUNBOOK.md:122,129` ↔ `src/Natter.Cli.Core/Commands/RuntimeHealth.cs:71-78`、`deploy/docker/Dockerfile:100-103`

```
runbook:122  `health.status=healthy`（Tier 1 profile 的 provider 可用且 upstream 都连接…）
runbook:129  只部署 Tier 1（未装 Spark）时 `status=degraded` 属预期
Dockerfile:101  A Tier 1-only deployment reports status=degraded while Tier 2 profiles are absent
```
```csharp
var tier1Unavailable = providers.Any(provider => provider.SupportTier == 1 && !provider.Available);
```

- **影响**：代码只对 `SupportTier == 1` 的 provider 缺失去降级，Tier 2 缺失**不会**导致 `degraded`。所以 `runbook:129` 与 `Dockerfile:101-103` 的事实描述是错的 → 运维看到 `healthy` 会怀疑代码，看到 `degraded` 会去查 Tier 2；`ops-health-alert.sh:81` 的提示文案“Tier 2 absence is expected for Tier 1-only deployments”也建立在同一误解上。
- **修复方向**：统一为“Tier 2 缺失只会让对应 profile 不被选中，不改变 `status`”，并修掉三处文案；`HEALTHCHECK` 的 liveness 语义（只看 HTTP 200）与 readiness（看 `status`）在文档里分开写清。

### R-66 [P3] DEVELOPMENT.md 的门禁命令与 CI 实际执行的命令不是一回事
`docs/DEVELOPMENT.md:758` ↔ `.github/workflows/ci.yml:44-84`、`docs/DEVELOPMENT.md:32`

```
DEVELOPMENT:758  - [ ] `dotnet build Natter.slnx`（或桌面等价命令）无新增错误；
DEVELOPMENT:32   没有 Android 环境时，不要直接 `dotnet build Natter.slnx`
```

- **影响**：自查清单让贡献者跑 `dotnet build Natter.slnx`（需要 Android workload，多数机器直接失败），而第 32 行又明令不要跑；CI 从不跑 slnx，只跑 4 个工程（R-27）。三者对“什么算通过”的定义互相不覆盖，`PromptLibrary` 这类工程就掉进了缝里。
- **修复方向**：把“门禁命令”收敛成 `scripts/verify.sh`（restore slnx → build 桌面子集 → test 全部 `*Tests.csproj` → shellcheck → soak-report self-test），CI 与文档都调用同一个脚本。

### R-67 [P3] runbook 的 `/opt/natter` 属主与 `install.sh` 相反
`docs/OPERATIONS_RUNBOOK.md:58-63` ↔ `deploy/systemd/install.sh:87-91`

```
runbook:59   sudo chown -R natter:natter /opt/natter /etc/natter/config /var/lib/natter
install.sh:88 run chown -R root:root "$RELEASE_DIR"
install.sh:89 run chmod -R a+rX "$RELEASE_DIR"
```

- **影响**：runbook 的手工步骤让服务用户拥有自己的二进制目录（可自我篡改 = 提权面），`install.sh` 的做法（root 拥有 + go-w）才是正确的。运维按 runbook 手工部署会得到比脚本更弱的配置，且两份文档都自称是权威路径。
- **修复方向**：runbook §1.2 改成与 `install.sh` 一致（`root:root` + `a+rX` + `go-w`），并注明“服务用户不写发布目录”。

---

### R-68 [P3] `soak-report.py` 的覆盖率阈值有两份（硬编码 95.0 + `--min-coverage`）
`scripts/soak-report.py:296-297` ↔ `:987,1049-1054`

```python
if coverage < 95.0:
    reasons.append(f"coverage {coverage}% < 95%")
```
```python
parser.add_argument("--min-coverage", type=float, default=95.0, ...)
...
if summary["samples"] and summary["coveragePercent"] < args.min_coverage:
    summary["result"] = "FAIL"
    message = f"coverage {summary['coveragePercent']}% < required {args.min_coverage}%"
```

- **影响**：当前行为“碰巧正确”（main 会再判一次），但 `summarise` 内那条硬编码会让 `many samples but low coverage` 的场景同时产出 `... < 95%` 与 `... < required 99%` 两条措辞不一致的 reason；`summarise_cumulative` 复用 `summarise`（第 596 行）时也会带出这条 95% 硬编码的 reason 混入 cumulative verdict。政策阈值散落两处，改一处忘另一处就是下次的口径 bug。
- **修复方向**：把 `min_coverage` 作为 `summarise(...)` 的参数（与 `interval`/`max_gap` 同层），删掉 95.0 字面量。

## 7. Top 8 发现（按风险排序）

| # | 严重度 | 位置 | 一句话 |
|---|---|---|---|
| 1 | **P1** | `scripts/soak-report.py:115-118,329` | `error:"unreachable"` 样本被移出时间线且从不参与判定 → host 大面积不可达的 soak 仍判 PASS。 |
| 2 | **P1** | `scripts/soak-report.py:142-145` | 覆盖率分母是“观测跨度”而非头部声明的 `durationSeconds` → 提前结束/被截断的运行拿 100% 覆盖率。 |
| 3 | **P1** | `Directory.Build.props:18-19` + `.github/workflows/ci.yml:86-97` | 全仓 `NuGetAudit=false` + `NoWarn NU1900`，CI 只对单个测试工程重开审计并用英文文案 grep 判定 → 供应链门禁基本不存在。 |
| 4 | **P1** | `src/Natter.Cli.Core/Commands/RuntimeHealth.cs:61-64` | `/health` 与 `health --json` 只脱敏了 `lastDisconnectError`，`lastError`/`lastSendError` 原样输出到无认证的 loopback 端点并落进证据文件。 |
| 5 | **P1** | `src/Natter.Cli.Core/Cli/NatterCli.cs:96-124`（对照 runbook §2.3:119-121,134） | 除 `health --offline` 外**所有**命令都会 `StartAsync` 起完整 host 并连 Discord 网关，单实例锁只保护 `serve` → 巡检命令抢 bot 会话，而文档正教人这么用。 |
| 6 | **P1** | `src/Natter.Hosting.Android/Properties/AndroidManifest.xml:1-6` | Release 清单没有 `INTERNET` 权限（Debug 靠 SDK 注入），`publish-android.sh` 默认发 Release → 发布的 APK 无法联网。 |
| 7 | **P1** | `scripts/p2-collect.sh:38-39` | Discord bot token 作为 `curl -H` 的 argv → `ps`/`/proc/*/cmdline` 可见，直接违反同仓 `rotate-secret.sh` 的设计约束。 |
| 8 | **P2** | `deploy/systemd/install.sh:202` + `scripts/soak-sample.sh:29` + `natter-soak.timer:9` | 采样间隔三处不一致（120/300/默认 120），按 `install.sh` 的提示审计必然得到错误口径的 gap/coverage。 |

> 备选（若按“运维影响”替换 #8）：`src/Natter.Cli.Core/Cli/NatterCliOptions.cs:100-108` 的 `--no-plugins` 连带丢弃 `NATTER_CONFIG_DIR/NATTER_STATE_DIR` 并绕过单实例锁（P2，容器 read-only 根文件系统上会直接写失败）。

---

## 8. 最关键的 5 个文件

| 文件 | 为什么关键 | 关联发现 |
|---|---|---|
| `scripts/soak-report.py` | 24h soak 的**唯一**裁决者，却存在两条可假 PASS 的路径（不可达样本被丢弃、覆盖率分母自证），且头部契约解析后不用。修这里等于修验收链的地基。 | R-10, R-11, R-12, R-68, R-64 |
| `src/Natter.Cli.Core/Cli/NatterCli.cs` | host 启动时机的唯一决策点：决定了“哪些命令会抢 bot 会话”“`--offline` 为什么没用”“单实例锁覆盖范围”。绝大多数 CLI 级问题的根因。 | R-33, R-34, R-38, R-39, R-44 |
| `Directory.Build.props` | 一行 `NuGetAudit=false` + 一行 `NoWarn NU1900` 让全仓供应链审计失效，并连带 `EnableNETAnalyzers=false` 让分析器门禁为零。影响面覆盖所有工程与所有构建路径（本地/Docker/CI）。 | R-25, R-31 |
| `src/Natter.Cli.Core/Commands/RuntimeHealth.cs` | `/health` 与 `health --json` 的唯一数据源，脱敏策略在这里；它同时是 soak/verify/alert 三个运维工具的输入，一个字段漏脱敏会被复制到三处证据里。 | R-32, R-42, R-12 |
| `deploy/systemd/install.sh` | systemd 路径的唯一安装器：参数不回写 unit（R-57）、误导的 soak 审计间隔（R-64）、发布目录属主与 runbook 相反（R-67），三处都会让“装好了”与“装对了”脱钩。 | R-57, R-64, R-67, R-58 |

---

## 9. 未覆盖 / 待确认

1. **`natter.service:62 SystemCallFilter=@system-service`** 对 .NET 10 运行时是否过严（`membarrier`/`rseq`/`io_uring` 依赖 libseccomp 版本）——需在目标发行版实测 `journalctl -u natter | grep -i seccomp`。
2. **Android Release 是否“故意”无网络权限**——若是产品决定（纯离线演示包），R-49 应降级为“文档缺失”；但 `docs/PUBLISHING.md:108` 把 `publish-android.sh` 当正式发布路径，故按缺陷记录。
3. **`PluginStore` 内部的并发/原子性**（`plugins store install` 与运行中 host 并发）不在本次范围（属 `02-plugins.md`）。
4. **`soak-report.py --mode cumulative`** 的分段/RSS 漂移判定只做了结构审查（`summarise_cumulative:496-696` + self-test 用例 6-11），未做数值级复核；self-test 覆盖 11 个场景，但 R-10/R-11 的两个假 PASS 路径**不在** self-test 覆盖里（建议补 fixture：`{host 前 20 分钟健康 + 之后 4 小时全是 unreachable}` 应当 FAIL）。
5. **WebConsole 插件的 HTTP 安全面**（token 校验、CSRF、命令白名单）属 `04-*`/插件审查范围；本报告只在其与 CLI/ops 脚本交界处取证（`webconsole-e2e.sh`）。
6. `scripts/soak-report.py` 未逐一核对 1064 行全部逻辑，重点覆盖 `load` / `summarise` / `summarise_cumulative` 的判定入口与 `main` 的退出码映射。

---

### 附：发现索引

P1：R-01, R-10, R-11, R-25, R-32, R-33, R-49（7 条）
P2：R-02, R-03, R-04, R-05, R-06, R-07, R-08, R-09, R-12, R-13, R-14, R-26, R-27, R-28, R-34, R-35, R-36, R-37, R-38, R-39, R-40, R-41, R-42, R-50, R-51, R-55, R-56, R-57, R-58, R-59, R-64（31 条）
P3：R-15, R-16, R-17, R-18, R-19, R-20, R-21, R-22, R-23, R-24, R-29, R-30, R-31, R-43, R-44, R-45, R-46, R-47, R-48, R-52, R-53, R-54, R-60, R-61, R-62, R-63, R-65, R-66, R-67, R-68（30 条）

合计 68 条（P0 0 / P1 7 / P2 31 / P3 30），与第 0 节统计表一致。

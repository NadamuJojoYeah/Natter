# Natter deployment (Tier 1)

## systemd

1. Install the published binary under `/opt/natter`.
2. Put config/plugins under `/etc/natter/config`; put secrets in `/etc/natter/live.env` (0600).
3. `useradd --system natter`; make `/var/lib/natter` owned by `natter` mode 0700.
4. `systemctl enable --now natter`.

`natter serve` handles SIGINT/SIGTERM; systemd stops are graceful within 10 seconds.

## Docker

```bash
cp live.env.example live.env   # 0600, never commit
docker compose -f deploy/docker/compose.yaml up -d --build
```

The container runs as UID 10001, state is a named volume, config is read-only.

## SQLite backup

```bash
scripts/natter-backup.sh /var/lib/natter/natter.db /backup/natter-$(date +%F).db
```

The script checkpoints WAL, uses `sqlite3 .backup`, and sets the backup file to 0600.
Restore while the service is stopped:

```bash
systemctl stop natter
cp /backup/natter-YYYY-MM-DD.db /var/lib/natter/natter.db
chmod 600 /var/lib/natter/natter.db
systemctl start natter
natter --config-dir /etc/natter/config --state-dir /var/lib/natter health
```

Never copy `-wal`/`-shm` separately; use the backup API while stopped or checkpointed.

## 24h soak

Run `serve`, enable `/health`, monitor logs for duplicate replies, reconnect loops, memory growth, and non-zero `sendFailures`; record the evidence before P4 sign-off.

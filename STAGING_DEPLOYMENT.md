# A18_2 Staging 與公開測試網址

## 環境

- 正式版：`https://realtime.offdutylab.xyz/swing` → 原 Cloudflare Tunnel → `127.0.0.1:5220`。
- Staging：`https://staging-realtime.offdutylab.xyz/swing` → **獨立 Cloudflare Tunnel** `a18-realtime-staging`（ID `156721ab-f228-4996-9115-fe9a766ad154`）→ `127.0.0.1:5221`。
- 測試容器：`docker compose -f compose.staging.yaml up -d --build`；獨立 Compose project、映像與可寫狀態目錄 `staging-data/`。
- Tunnel 設定：`~/.cloudflared/config-a18-realtime-staging.yml`；systemd user unit `~/.config/systemd/user/cloudflared-a18-realtime-staging.service`，已啟用。

## 測試隔離

- Stub provider，不登入元大 SPARK；不啟動 Shioaji Collector。
- A18_10 歷史倉庫唯讀、設定狀態與正式分離，不掛券商憑證。
- `A18__StagingSafeMode=true` 時全部 `/api/runtime21` 被阻擋並返回 `503 STAGING_RUNTIME21_DISABLED`（禁止真單、查帳戶和交易控制）。
- `/health/ready` 在 Stub 下可能回傳 503，不應用此端點決定是否能測試 Swing UI。

## 驗證紀錄（2026-10-10）

- Staging 本機 `/swing`：HTTP 200；`/api/bars/1615?date=2026-10-08&interval=60`：HTTP 200。
- `/api/runtime21/health`：HTTP 503 且出現 `STAGING_RUNTIME21_DISABLED`。
- 公開 DNS CNAME 新增成功，Cloudflare Tunnel 已建立連線，systemd user unit 啟動成功。
- **公開網址尚未完成瀏覽器端成功驗證**：同一測試來源連正式與測試網址均得到 Cloudflare 403/1010，可能是來源 IP 的 Cloudflare 防護設定；請用手機或外部瀏覽器確認。
- 正式 Tunnel 設定未更動，正式容器沒有因建立 Staging 而重啟。

## 下一階段

Blue-Green 流量切換、零中斷發布、回滾及 DEP-OP UI 尚未實作。禁止直接將目前的隔離 Staging 映像當成正式候選版本切換（它的 Stub、只讀資料與真單攔截是刻意設計）。進行切換前應另規劃行情連線交接、狀態遷移及交易安全檢查。

# A18_2 隔離測試環境（第一階段）

正式版：Docker Compose `compose.yaml`，5220，Cloudflare `realtime.offdutylab.xyz`。

測試版：`docker compose -f compose.staging.yaml up -d --build`，僅綁定本機 `127.0.0.1:5221`，不公開到 Internet。Docker Compose 獨立專案 `a18-2-staging`、映像 `a18-realtime:staging`。

安全限制：Staging 使用 `A18__Provider=Stub`；`A18__FullMarketStartShioajiCollectors=false`；市場倉庫唯讀；可寫設定獨立在 `staging-data/`；不掛載元大憑證；設定 `A18__StagingSafeMode=true` 時伺服器對所有 `/api/runtime21` 請求返回 503，不會轉送真單、查帳戶或變更交易控制。請勿直接將 Staging 接入真單憑證或正式寫入的 volume。

測試方式：在 Ubuntu 執行 `curl -f http://127.0.0.1:5221/swing`；測試歷史 K 棒 `curl -f 'http://127.0.0.1:5221/api/bars/1615?date=2026-10-08&interval=60'`；確認 `/api/runtime21/health` 為 503 並返回 `STAGING_RUNTIME21_DISABLED`。Stub 模式的 `/health/ready` 目前可能回傳 503，不適用作 Staging 啟動檢查。

重要：這只是部署流程的第一階段，目前尚未實作 reverse proxy 藍綠切換、回滾或公開測試網址。禁止用 `docker compose up` 重建正式容器作為測試。後續需要先引入穩定 proxy、獨立候選映像版本、健康檢查和資料寫入切換保護，才可導流。

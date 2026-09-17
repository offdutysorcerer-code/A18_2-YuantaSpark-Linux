# A18_2 - Yuanta SPARK Realtime Market Data Gateway

## 目標

A18_2 重新打造為單一責任的即時行情 Gateway：

- ASP.NET Core API
- Yuanta SPARK API Adapter
- 明確的 Session / Subscription lifecycle
- Docker Compose 作為正式執行方式
- 主機路徑、帳密與 proprietary SDK 與程式碼解耦
- 可移植到新的 Linux 主機

## 不屬於 A18_2 的責任

- 策略研究 / 回測
- 下單 / 部位 / 風控
- Theme Rotation
- Shadow Trader
- Cloudflare Tunnel lifecycle

## 執行位置

專案：

```text
/media/pcl7986/New Volume/AIProjects/A18_2-元大SPARK_API_IN_Linux
```

正式執行方式：Docker Compose。API 在 container 內聽 8080，主機預設映射為：

```text
http://127.0.0.1:5220
```

## 一鍵腳本

在專案根目錄：

```text
A18_2-run.sh      build + start + 等 API + 開瀏覽器 UI
A18_2-start.sh    build + start
A18_2-stop.sh     stop / remove container
A18_2-restart.sh  restart container
A18_2-status.sh   Docker + live/ready + session + subscriptions
A18_2-test.sh     訂閱並測試股票；預設 TWSE/2330
A18_2-ui.sh       開啟 Diagnostic UI
A18_2-logs.sh     follow container logs
```

例如：

```bash
./A18_2-run.sh
./A18_2-test.sh 2330 TWSE
./A18_2-test.sh 6488 TWSE
./A18_2-status.sh
```

## Diagnostic UI

瀏覽器開：

```text
http://127.0.0.1:5220/
```

UI 可：

- 看 LIVE / READY
- 看 Session
- 選 TWSE / TPEX / TWEMERGING
- 輸入股票代號並 Subscribe
- 看 subscriptions lifecycle / backfill state
- 查 latest Tick
- 指定交易日查 Tick；沒有資料時回 `NO_DATA_YET`，不 fallback 到其他日期

## API

```text
GET  /health/live
GET  /health/ready
GET  /api/session
GET  /api/subscriptions
GET  /api/readiness?symbols=2330,2454
POST /api/subscriptions/{market}/{symbol}
GET  /api/ticks/{symbol}/latest
GET  /api/ticks/{symbol}?date=yyyy-MM-dd
```

## Session / 跨日期規則

A18_2 不把「Process 還活著」視為「行情可用」。Session 有三個核心邊界：

```text
BootId            = 本次 process/container 啟動 ID
SessionGeneration = 每次成功 Login 建立新 session 後 +1
SessionTradeDate  = 該 session 所屬的 Asia/Taipei 日期
```

規則：

1. 所有日期判斷固定以 `Asia/Taipei` 為準，不依賴主機當下 timezone。
2. 跨過 00:00 後，如果 `SessionTradeDate != CurrentTaipeiDate`，舊 session 立即視為 stale。
3. stale session 的舊 Tick / callback 不得讓 `/health/ready` 成功。
4. Session supervisor 每 30 秒檢查一次。
5. 預設 08:45 之後若仍是前一日 session，自動執行 `Disconnect -> Login -> SessionGeneration + 1`。
6. `data/required-symbols.json` 是跨 process/container 的盤前預訂閱清單；啟動成功後立即訂閱，跨日 08:45 session rebuild 後重新訂閱。
7. 同一 process 內動態 requested/desired subscriptions 也會在 session rebuild 後重新 Subscribe。
8. callback 必須和目前 SessionGeneration + TradeDate 相符才可把狀態推進 `LIVE_OBSERVED`。
9. `/health/ready` 是 channel-level 健康：至少一個當前 session 的 live callback 足夠新；它不代表所有觀察股票都成交。
10. 策略必須用 `/api/readiness?symbols=...` 做 per-symbol gate；`readyForRequestedSymbols=true` 才代表指定標的全部具有 fresh live callback。
11. `preopenPrepared=true` 代表指定/盤前 universe 已透過 `GetQuoteListSync` 確認訂閱存在；盤前不要求成交 callback。
12. `SUBMITTED` 只代表 `SubscribeStockTick` 呼叫已返回；`ACK_CONFIRMED` 才代表 SPARK 端訂閱清單已確認存在，收到真實 callback 後才進入 `LIVE_OBSERVED`。
13. 所有 explicit `date` 查詢都不可 fallback 到其他日期。

## Intraday backfill

盤中新增標的或盤中重新建立 SPARK session 時，A18_2 採 **Subscribe first, backfill second**：

```text
SubscribeStockTick
  -> SUBMITTED
  -> GetQuoteListSync 驗證後 ACK_CONFIRMED
  -> 背景 GetStkTickDetail 09:00~訂閱時刻
  -> live/backfill 以 Sequence 去重後寫入同一當日 Tick store
  -> 收到真實 callback 後 LIVE_OBSERVED
```

盤前已成功 submitted 的標的沒有「訂閱前盤中缺口」，其 `backfillState=NOT_REQUIRED`。Backfill 狀態獨立於 live readiness：`QUEUED / RUNNING / COMPLETED / FAILED / NOT_REQUIRED`，backfill 絕不可以把 subscription 推成 `LIVE_OBSERVED`。

## Subscription lifecycle

```text
QUEUED
  -> SUBMITTING
  -> SUBMITTED
  -> ACK_CONFIRMED
  -> LIVE_OBSERVED

ACK 查不到 -> ACK_MISSING -> REARM_REQUIRED -> 重新 paced subscribe
任何階段都可能 -> FAILED
解除 / session 結束 -> DEACTIVATED
```

## Linux SPARK runtime

Linux Login 使用：

```text
Login(pfxPath, pfxPassword, account, password)
```

正式 runtime 需要包含元大 Linux 簽章 native library：

```text
libCGCGCrypt.so
```

PFX 以唯讀方式掛載到 container：

```text
/run/secrets/yuanta.pfx
```

帳密、PFX、SDK proprietary runtime 不可提交 Git。

## 移植驗收

新 Linux 主機只應需要：

1. Docker Engine + Docker Compose plugin
2. Clone / 複製 repository
3. 提供 `.env`
4. 提供 `secrets/yuanta.pfx`
5. 提供 Yuanta SDK/runtime
6. `./A18_2-run.sh`

不得依賴 `D:\...`、固定 `/home/<user>/...` 或另一個專案的 `bin/Debug`。

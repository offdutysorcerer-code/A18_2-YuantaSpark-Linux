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
- 看 subscriptions lifecycle
- 查 latest Tick

## API

```text
GET  /health/live
GET  /health/ready
GET  /api/session
GET  /api/subscriptions
POST /api/subscriptions/{market}/{symbol}
GET  /api/ticks/{symbol}/latest
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
6. 同一 process 內，使用者已要求的 desired subscriptions 會在新 session 自動重新 Subscribe。
7. callback 必須和目前 SessionGeneration + TradeDate 相符才可把狀態推進 `LIVE_OBSERVED`。
8. `/health/ready` 除了要求 `LIVE_OBSERVED`，也要求 callback 足夠新；預設 freshness 300 秒。
9. `SUBMITTED_WAITING_LIVE` 只代表 `SubscribeStockTick` 呼叫已返回且未拋例外，不代表 vendor ACK。

目前 desired subscriptions 只存在 process memory；**container/process 重啟後不會自動恢復**。若未來要做到重開機後自動恢復指定股票，應再加入明確的 required-symbol configuration / persistence，而不是把舊 runtime state 當真實來源。

## Subscription lifecycle

```text
QUEUED
  -> SUBMITTING
  -> SUBMITTED_WAITING_LIVE
  -> LIVE_OBSERVED

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

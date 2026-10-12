# Staging PAPER isolated lifecycle test (2026-10-12)

**Scope:** Separate disposable container `a18-realtime:staging` on loopback port 5299, with private `/tmp/e2e` volume. Not the persistent staging service. Feed `Stub`; synthetic 6257 prices/timestamps. No Runtime21 or broker connectivity and no real order.

- PUT Paper-enabled 6257 with one-entry and 500,000 TWD budget: accepted.
- Reserve 1,000 shares with 09:15 signal: SHADOW_RESERVED.
- Reserve identical signal again: SIGNAL_ALREADY_PROCESSED.
- Simulate fill/open at 195: PAPER_OPENED.
- Open same intent again: ALREADY_OPENED.
- Observe 09:16 bar high 204: position remains OPEN, high-water increases to 204.
- Observe next bar low 200: PRIOR_WATERMARK_STOP, CLOSED at 200.94 (204 × 0.985).
- Observe further bar after closing: POSITION_ALREADY_CLOSED.
- Integrity check: ready=true and no unreconciled positions.
- Restart the disposable container: position remains CLOSED, intent FILLED, integrity stays ready.
- Remove disposable container; temporary data not connected to staging account.

**Not tested / NOT implied:** Automatic detection of a live signal, full live-feed Paper execution, cost/slippage, A18_21 live trading integration, advanced C/T9 strategy, cross-day positions, or broker reconciliation. Staging `/api/runtime21/health` continues to return HTTP 503 `STAGING_RUNTIME21_DISABLED`.

**Next:** An opt-in test using explicitly approved symbol with fresh upstream quote ticks and dedicated paper-only account, plus fee/tax/slippage checks. Maintain fail-closed live routing.

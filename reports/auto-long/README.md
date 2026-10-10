# AutoLong V1 deterministic replay, 2026-10-08

Run `python3 scripts/auto_long_v1_replay.py --output reports/auto-long/2026-10-08-v2-60s-A.json` from A18_2 project with the isolated Staging server at localhost:5221.

- 5-second completed-bar V2 close-based signal, 60-second inclusive window, mode A/B.
- 1-second warehouse bars for prices, prior-high-watermark fixed 1%/trail 1.5% stop.
- Entries before 09:30 only; earliest 1-second bar at or after confirmation must be within eight seconds. Open exits continue after 09:30.
- 1000 shares per entry, maximum 10 entries, buy-only budget 10 million TWD, no pyramiding; commission 0.0855% each side and tax 0.15% on sale, excluding sliding fills/minimum fee.
- This is a read-only independent replay script, **not** a deterministic invocation of the running C# AutoLongPaperRunner. The runner scans 2s and requires recently observed tick data, which may differ from 1s replay. It cannot be certified against the live broker on Stub provider.
- Note stale 5225 09:14:25 signal is rejected since next observed 1s fill 09:14:36 is >8s later. Later 09:16:25 signal has 09:16:33 observed bar; live scheduler latency could also prevent entry. This explains deviation from earlier 4.59% 5225 claim.

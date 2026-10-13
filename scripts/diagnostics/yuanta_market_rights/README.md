# Yuanta market-rights offline regression

Run `bash scripts/diagnostics/yuanta_market_rights/run.sh` on an Ubuntu host with Docker and the local .NET 8 SDK image. It **does not log in** to the broker, use live credentials, change subscriptions, or modify production services. The test runs solely against `vendor/yuanta/YuantaSparkAPI.dll` in an isolated container.

The probe calls the SDK's internal `APIHandle.UpdateMarketLimit` using synthetic account objects. Expected outcomes:

| Input | Expected |
|---|---|
| Account object null (matching a failed `dicAcno.TryGetValue`) | `NullReferenceException` |
| Account exists, `LstMarketRight` null | `NullReferenceException` |
| Account exists, initialized empty rights list | no exception |

Observed on 2026-10-13 against SDK SHA256 `127d2cb2e4a4b1bd880bbf210e86e124fea97671a6a4f29f5d31ab5aa752dc22`: PASS.

The SDK's `CheckMarket` calls `dicAcno.TryGetValue(Account)` and forwards the result to `UpdateMarketLimit` without a null guard. The A18_2 provider currently uses the account identifier from the first `LoginResult.LoginList` item. The production `NullReferenceException` is consistent with *either* a missing account dictionary entry *or* a missing market-rights list. **This probe does not inspect the live connection, distinguish these causes in production, or prove that a subscription-free query is supported.**

Next verification requires observing the SDK's runtime account-map match and rights-list presence within its existing session. Avoid a second broker login while provider connection limits and Shioaji `451 Too Many Connections` errors are unresolved. Prefer an opt-in, one-time, redacted readiness metric in the existing provider process during a controlled deployment window. Never expose account identifiers, login secrets, rights content, or SDK private data on a public endpoint.

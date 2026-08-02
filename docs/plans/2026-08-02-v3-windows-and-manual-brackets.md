# v3 — Three session windows, 30-min entry hunt, hand-draggable brackets

**Date:** 2026-08-02 · **Scope:** `LatigoBreakStrategy.cs` only (lab iteration, no research rerun).

## Requested changes (Javier)

1. Three trading windows, each with an enable checkbox: **09:30 ET** (US open), **18:00 ET** (Globex reopen, current), **20:00 ET**.
2. Drop **Time stop** (the 1800 s flatten). Per window: hunt breaks/entries for **max 30 minutes** after the window opens; an open position is NEVER closed by the clock — it runs until stop/target. If a position is still open when the next window arrives, that window is skipped.
3. Once in a trade, SL/TP must be **draggable by hand** in Chart Trader. Today they snap back (v2 uses `SetStopLoss`/`SetProfitTarget`, which the strategy engine re-asserts).

## Design

- **Windows as offsets from `ActualSessionBegin`** (ETH template ⇒ session begins 18:00 ET): `+0 s` = 18:00, `+7 200 s` = 20:00, `+55 800 s` = 09:30 next morning (same trading day). Zero time-zone code; DST transitions always fall in the closed weekend, so offsets are exact. RTH template would break this — requirement unchanged from v2, documented.
- **Per-window state machine.** `Phase.Idle` between windows. When flat + idle, advance `_nextWin` past disabled windows and windows whose opening candle already elapsed (that is what "skip if in a position" degrades to), then arm when `t >= w0`. Everything that was per-session (candle H/L, break state, `_trades`, one-✕/one-dot bars, `_tag`) becomes per-window. Day-level state (governor, lockout, drawings history, rewind reset) unchanged.
- **One `EntryWindowMinutes` param (default 30)** replaces `WatchEndSeconds` + `EntryDeadlineSeconds`. Breaks, gap-throughs and entries all die at `w0 + 30 min`. `TimeStopSeconds` + `CheckTimeStop` + `_timeExitSent` deleted. `IsExitOnSessionCloseStrategy` stays (the 17:00 ET halt forces it anyway — that is the position's natural outer bound).
- **Brackets: `Set*` → `Exit*` with `isLiveUntilCancelled: true**` (`ExitLongStopMarket`/`ExitLongLimit` + short mirrors, signals `LB_Stop`/`LB_Target`), submitted from `OnExecutionUpdate` on entry fills (official SampleOnOrderUpdate pattern). The engine never re-asserts these orders ⇒ manual drags persist. Docs confirm: managed exits protecting the same position auto-reduce/cancel each other on fill, and Set* cannot coexist with Exit* (internal order-handling rules), so Set* is removed entirely. Bracket prices computed off the ACTUAL average fill (ATR logic unchanged, structural fallback kept, 1-tick clamps). `OnOrderUpdate` adopts price changes on `LB_Stop`/`LB_Target` into `_stopPx`/`_targetPx` (keeps breakeven guards honest; prints when a hand-move is detected; warns if a bracket is hand-cancelled while in position). Breakeven, when enabled, still overrides the stop once at trigger — documented in the property description.
- `MaxTradesPerSession` → `MaxTradesPerWindow` (default 1): re-arm inside the window while budget and the 30-min clock allow.

## Adversarial review outcome (12-agent workflow, trading-code-reviewer lenses)

9 findings, 5 confirmed and fixed, 4 refuted with doc-backed traces (bracket auto-cancel on flat is engine-guaranteed; BE-vs-flatten same-tick race is platform-absorbed; forward-run drawing accumulation is the intended audit trail):

1. **Late-armed window truncated the opening candle** (position closing inside the next window's candle → understated R30, silent). Fixed with `_freeSince`: a window arms only if we were already free at its open, else it is skipped — which is the user's own skip rule, now airtight.
2. **`EntryWindowMinutes` > the 2 h gap swallowed the 20:00 window with no position involved.** Fixed: effective deadline capped at the next enabled window's open.
3. **`_drawTags.Clear()` ran on every session rollover** (pre-existing v2), so a cross-day Playback rewind could only wipe the last session of the discarded pass. Fixed: ledger clears only on the rewind path.
4. **`CandleSeconds` > entry window starved the whole hunt** for legal param combos. Fixed: deadline floored at `CandleSeconds + 60 s`, disclosed via Print.
5. **Governor lockout left an in-flight entry alive a full tick** (fill → brackets → next-tick flatten). Fixed: a fill arriving under lockout flattens on the fill event itself, no brackets.

Plus one self-caught fix: the went-flat bookkeeping in `OnExecutionUpdate` is now phase-gated (`InPosition`/`Pending`) so stale exit events around a rewind can't consume a fresh day's window, and a stop+target double-fill can't double-count.

## Acceptance

Implemented → `nt8c` build passes against the NT8 Custom dir → adversarial review (trading-code-reviewer) findings fixed → README + header updated → committed/pushed → `.cs` copied to `Documents/NinjaTrader 8/bin/Custom/Strategies/` (Javier compiles F5).

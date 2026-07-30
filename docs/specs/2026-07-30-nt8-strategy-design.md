# LatigoBreakStrategy — NT8 Implementation Design

**Date:** 2026-07-30 · **Status:** Approved (user decision: implement in NT8 despite G1 FAIL — Playback/sim laboratory for live iteration of the redesign; the research verdict in `docs/research/` stays on record).

## What it is

`LatigoBreakStrategy.cs` — a single self-contained NinjaScript strategy that ports the validated Python event engine + causal detector 1:1 to NT8: builds the first N-second candle of the 18:00 ET Globex reopen, watches breaks of its H/L, vetoes whipsaws (re-entry ≥1 tick inside before confirmation), re-arms, and chases confirmed breaks with a market order + structural bracket. Defaults = best Phase-1 cell (hold 30 s, extension 0.25×R30).

## Architecture

- **Series:** primary = whatever chart it is applied to (30 s recommended, visual only). Logic runs entirely on an added **1-tick series** (`AddDataSeries(BarsPeriodType.Tick, 1)`) — same physics as the research tape; works historical (Analyzer, with Order Fill Resolution **High/Tick/1** mandatory — Pullback lesson), Playback, and live.
- **Session anchor:** `SessionIterator.ActualSessionBegin` on the tick series (no hardcoded clock times; requires an ETH/24-7 trading-hours template so the session begins at the 18:00 ET reopen — using an RTH template would silently trade the 9:30 open; documented in README).
- **State machine per session:** `Candle → Armed ⇄ Active → Pending → Position → (Armed | Done)`, all offsets in seconds from session begin: candle 0–30, new breaks until 930, entry deadline 1200, time stop 1800. Playback rewind guard: tick time going backwards ⇒ full session reset.
- **Detector semantics (identical to `research/phase1.confirm_index`):** break = inside→outside transition ≥1 tick beyond H/L; confirmation = hold elapsed AND cumulative max-extension ≥ ceil(ExtensionR30×R30) with no re-entry; re-entry first ⇒ whipsaw, visual mark, re-arm; gap-through print opens the opposite break immediately. `UseWhipsawFilter=false` ⇒ naive chase at the break print (so the filter's value is visible live). No label timeout X is needed live (labels were for research; the causal detector never used X).
- **Orders (managed, race-safe):** per-side signal names; `SetStopLoss(signal, Price, oppositeExtreme)` + `SetProfitTarget(signal, Price, entryEst ± RewardMultiple·R)` set **before** `EnterLong/Short(0, Contracts, signal)` submitted on the **primary bars context** (multi-series same-instrument rule). In-flight flag set BEFORE the Enter call, cleared name-gated (workspace order-event-race rule). Time stop exits via `Exit*(signal-gated)` once. Trade closed → re-arm until `MaxTradesPerSession`, else Done.
- **Visuals (the Playback lab):** H/L rays of the opening candle, dot per break, red "X + ret seconds" per vetoed whipsaw, triangle at entry, candle stats text. All no-op when no chart.

## Parameters

| Group | Param | Default |
|---|---|---|
| 01. Signal | UseWhipsawFilter | true |
| | HoldSeconds / ExtensionR30 | 30 / 0.25 (best Phase-1 cell) |
| | CandleSeconds / WatchEndSeconds / EntryDeadlineSeconds | 30 / 930 / 1200 |
| | MinR30Ticks (degenerate-session skip) | 4 |
| 02. Trade | Contracts / RewardMultiple / MaxTradesPerSession / TimeStopSeconds | 1 / 1.0 / 1 / 1800 |
| 03. Visuals | ShowDrawings | true |

## Honest-use box (README + property description)

Research on 203 NQ sessions says: naive chase −$110/trade; best filter cell ≈ −$33/trade (filter recovers most, crosses zero never). This strategy is a **sim/Playback laboratory** for iterating the redesign (order-flow trigger, entry style), not a validated edge. Running it live real-money contradicts the project's own research.

## Deltas vs research engine (disclosed)

1. Live detector drops the X-timeout (research-label concept only; re-arm live happens on re-entry).
2. After the entry deadline the session hunt ends (`Done`) — Python kept resolving for corpus completeness.
3. Bracket target computed from the confirmation print (like research entry model); actual fill may differ by slippage.

## Validation

nt8c compile (hook), trading-code-reviewer adversarial pass, Playback checklist (in README): candle build, break dot, whipsaw X + re-arm, confirmation entry + bracket, naive-mode comparison, rewind reset, MaxTrades lockout.

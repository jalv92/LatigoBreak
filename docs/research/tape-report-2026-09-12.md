# LatigoBreak on the NT8 tick tape — 18:00 ET, 1 NQ, $500 target (2026-09-12)

Question (Javier): is the 18:00 reopen break profitable with one contract, a
$500 target and a 1.5-2 ATR stop, and what would make us enter only the right
breaks?

## Setup

- Entries: `engine.LatigoBreak` in PropSim, the closed mirror of
  `LatigoBreakStrategy.cs` v3 (4/5 trades identical to the cent vs Market
  Replay). 18:00 window only, whipsaw filter ON (hold 30 s, extension 0.25 R30),
  candle 30 s, `MinR30Ticks` 4, `EntryWindowMinutes` 30, one trade per window,
  breakeven off, daily governor OFF, no time stop (flat at the 17:00 halt).
- ATR: Wilder 14 on 30-second bars, forming bar included (as `OnEachTick`).
- Target: fixed $500 (25 NQ points) instead of the strategy's 2xATR; the 2xATR
  row is kept for comparison. Stop: 1.5x and 2.0x ATR.
- Fills/costs: `engine.resolve`, `Costs(commission=5.76, slippage_ticks=2)`.
- Data: `ALL` (NQ continuous, 275 sessions 2025-08-01..2026-08-04, in-sample)
  and `NQ 09-26` from 2026-08-05 to 09-04 (27 sessions, never used before for
  this strategy: out-of-sample).
- Baselines: fade (opposite side, same tick, mirrored bracket), naive (filter
  OFF: chase the break print), and the 2xATR target.
- Harness: `research/tape_run.py` (`--filters`, `--dials`). Raw output in `tmp/`.

Gate = memory `strategy-profitability-gates`: n >= 100, avg >= $30, PF >= 1.3.

## 1. As asked: $500 target, ATR stop

| Tape | stop | row | n | win | net | avg | PF | Gate |
|---|---|---|---|---|---|---|---|---|
| ALL | 1.5 | **signal** | 202 | 32% | -$164 | -$1 | 0.99 | FAIL |
| ALL | 1.5 | fade | 202 | 28% | -$6,199 | -$31 | 0.81 | |
| ALL | 1.5 | naive chase | 216 | 26% | -$15,379 | -$71 | 0.64 | |
| ALL | 1.5 | 2xATR target | 202 | 50% | +$10,076 | $50 | 1.42 | **PASS** |
| ALL | 2.0 | **signal** | 202 | 38% | -$224 | -$1 | 0.99 | FAIL |
| ALL | 2.0 | fade | 202 | 31% | -$12,389 | -$61 | 0.70 | |
| ALL | 2.0 | naive chase | 216 | 31% | -$18,584 | -$86 | 0.63 | |
| ALL | 2.0 | 2xATR target | 202 | 57% | +$9,176 | $45 | 1.33 | **PASS** |
| OOS | 1.5 | signal | 21 | 33% | +$304 | $14 | 1.10 | n<100 |
| OOS | 1.5 | 2xATR target | 21 | 38% | -$256 | -$12 | 0.91 | n<100 |
| OOS | 2.0 | signal | 21 | 43% | +$1,004 | $48 | 1.31 | n<100 |
| OOS | 2.0 | 2xATR target | 21 | 52% | +$429 | $20 | 1.15 | n<100 |

- **$500 is the wrong unit.** The median ATR stop at 18:00 is ~38 ticks
  (1.5x) / ~50 ticks (2x), i.e. $190-250. A fixed $500 target is then 2-2.5R,
  and a 32-38% win rate pays for exactly that and nothing more: PF 0.99 on
  202 trades, both stops. The 2xATR target (the strategy's own default) is
  1:1 in ATR units and passes the gate in-sample on the same entries.
- **The whipsaw filter is worth $70-85 per trade** (naive PF 0.63-0.64 vs
  signal 0.99). Same finding as G1 (2026-07): it recovers the chase, it does
  not create edge by itself.
- **Out-of-sample is 21 trades**: the signs are right (signal positive, fade
  clearly negative: PF 0.44-0.46) but nothing there is a result.

## 2. Which breaks paid (ALL, $500, terciles of each feature at entry time)

Only features known at the entry tick. Same picture at both stops; 2.0x shown.

| Feature (at entry) | low | mid | high |
|---|---|---|---|
| Opening 30 s range, ticks | 20-60: PF 0.88 | 61-96: 0.88 | **97-582: 1.16** (win 54%) |
| Volume in the 30 s candle | 93-219: 0.91 | 220-342: 0.81 | **346+: 1.19** (win 54%) |
| Seconds from candle close to entry | 30-97: **1.21** | 97-227: **1.28** | **232-1650: 0.63** |
| Entry beyond the level, ticks | 0-17: 0.77 | 18-34: 0.96 | 35-210: 1.17 |
| Candle delta in the break's favour | -568..-15: 1.31 | -13..30: 0.76 | 31-397: 0.95 |
| Reopen gap vs 17:00 close, in favour | 0.90 | 1.03 | 1.07 |
| Side | long n=94 PF 0.74 | short n=108 PF 1.27 | |
| Weekday | Tue 0.64, Thu 0.55 | Mon 1.01 | Wed 1.58, Sun 1.37 |

Reading: the breaks that pay come from a **wide, active reopen candle** (range
>= ~97 ticks, >= ~350 contracts in 30 s; the "entry beyond the level" column is
the same thing, since extension = 0.25 x R30) and are **confirmed within ~4
minutes**. A break that needs more than 4 minutes to survive the hold is a
loser (PF 0.63). Candle delta AGAINST the break paying better is the sweep-
and-reverse shape; n=65, noted, not acted on. Side and weekday are the kind of
split that flips out-of-sample (shorts-only: PF 1.27 in, 0.68-0.78 out): noise.

## 3. The two cuts as NT8 dials (no new code)

Both survivors are existing properties: `MinR30Ticks` (skip the window if the
candle is narrower) and `EntryWindowMinutes` (stop hunting after N minutes).

| Tape | stop | target | MinR30 | Window | n | win | net | avg | PF |
|---|---|---|---|---|---|---|---|---|---|
| ALL | 1.5 | $500 | 4 | 30 | 202 | 32% | -$164 | -$1 | 0.99 |
| ALL | 1.5 | $500 | 97 | 5 | 46 | 59% | +$6,345 | $138 | 1.98 |
| ALL | 1.5 | 2xATR | 97 | 5 | 46 | 63% | +$13,190 | $287 | 3.20 |
| ALL | 1.5 | 2xATR | 4 | 5 | 145 | 56% | +$13,925 | $96 | 1.93 |
| ALL | 2.0 | $500 | 97 | 5 | 46 | 63% | +$5,820 | $127 | 1.73 |
| ALL | 2.0 | 2xATR | 97 | 5 | 46 | 67% | +$12,405 | $270 | 2.70 |
| ALL | 2.0 | 2xATR | 4 | 5 | 145 | 64% | +$15,160 | $105 | 1.93 |
| OOS | 1.5 | $500 | 97 | 5 | 3 | 67% | +$703 | | |
| OOS | 2.0 | $500 | 97 | 30 | 7 | 57% | +$920 | $131 | 1.94 |
| OOS | 2.0 | $500 | 4 | 5 | 14 | 43% | +$624 | $45 | 1.28 |
| OOS | 2.0 | 2xATR | 80 | 5 | 7 | 57% | +$900 | $129 | 1.99 |

- **`EntryWindowMinutes` 30 -> 5 alone** keeps 145 of 202 trades and turns
  the 2xATR-target strategy into PF 1.93 in-sample (gate PASS at n=145). It
  is the cheapest change and the one with the most trades behind it.
- **`MinR30Ticks` ~97** (roughly "the reopen candle moved 24+ points") keeps
  the best third of sessions; combined with the 5-minute window it is
  PF 1.7-3.2 in-sample on 46 trades, and 3-7 trades out-of-sample, all
  positive. Thresholds were read off the in-sample terciles, so these numbers
  are optimistic by construction.
- The $500 target only makes sense once the candle filter is on (a 97-tick
  candle means an ATR stop of ~$300+, so $500 becomes ~1.5R).

## Verdict

- As specified (18:00, 1 NQ, $500, 1.5-2 ATR): **not profitable, PF 0.99 on 202
  trades**. Not because the break is bad, because the target is in the wrong
  unit: keep the target in ATRs (2x) and the same entries pass in-sample.
- What to change to "enter the right breaks": **`EntryWindowMinutes` = 5**
  (the confirmed break must come in the first minutes; late survivors lose) and
  **`MinR30Ticks` ~80-100** (trade only when the reopen candle is wide and
  active). Both are dials already in the `.cs`; nothing to code.
- What is NOT proven: the out-of-sample slice is 27 sessions (3-14 trades per
  row). The candidate settings need the Playback routine or the next ~3 months
  of tape before they are a result. Pre-registered here so the next check is
  honest: stop 2.0, target 2xATR, Window 5, MinR30 80. Kill if PF < 1.2 on the
  next 100 trades.

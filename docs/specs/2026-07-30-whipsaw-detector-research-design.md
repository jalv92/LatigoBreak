# LatigoBreak — Whipsaw Detector Research Design

**Date:** 2026-07-30
**Status:** Approved design — research phases only. No NinjaScript exists or gets written until Gate G1 passes.

## 1. Concept

Opening-range breakout on the first 30-second candle of the Globex reopen (18:00:00–18:00:30 ET) on NQ/MNQ. After the first candle closes, a break of its high (long) or low (short) is a trade candidate — but only if the break is *real*. The core problem this project attacks is detecting **whipsaws** ("latigazos"): breaks that immediately snap back inside the range. A detected whipsaw is vetoed and the strategy re-arms for the next break, either side, until the watch window ends.

Entry style (user decision): **chase** — market order after confirmation, accepting a worse price, never a limit at the level.

## 2. Scope

This spec covers the **research pipeline only** (Phases 0–2, Python, fully offline). Deliverable: a validated (or honestly killed) whipsaw detector with pre-registered gates. The NT8 strategy is a separate future spec, contingent on G1.

Method debt this design inherits deliberately (workspace lessons):
- Python-first hard gate — no NinjaScript before the signal proves itself on existing data.
- Thresholds in fractions of the candle range / ATR, never fixed ticks (Pullback lesson #1).
- Risk management cannot manufacture expectancy; if the signal is dead, archive (Pullback lesson #4).
- Pre-registered trial budget; all grid cells reported, never just the winner (BigPrints-50 lesson).

## 3. Data & corpus

- **Source:** NT8 hourly tick files `<NT8_HOME>/db/tick/<contract>/*.ncd`. File naming is **end-of-hour**: `*1900.Last.ncd` contains 18:00:00–18:59:59 ET (verified empirically; the near-empty `*1800` bucket is the 17:00–18:00 maintenance halt). Context hour from `*1700` (16:00–17:00) for pre-halt reference levels.
- **Parser:** vendored copy of `ncd_parse.py` (validated port from MFF-Sim; price corr 0.9998 vs external reference; timestamps naive ET wall clock).
- **Known .ncd limits (empirically established 2026-07-28, respected here):** aggressor-side heuristic usable (imbalance/return corr +0.52); embedded spread field **unusable** — never derive costs from it.
- **Front-month rule:** per calendar date, the contract directory with dominant volume in the 18:00–19:00 hour.
- **Universe:** NQ is the primary corpus; every adopted conclusion must replicate on MNQ or be flagged fragile. Estimated ~200–230 front-month sessions (NQ 09-25 → NQ 09-26); exact count is a Phase 0 output.
- **Cost model:** pre-registered conservative assumption — 2-tick spread + round-trip commission (NQ $4.50, MNQ $1.50) — cross-checked with a Roll effective-spread estimator on the reopen tape and the partial `Ask.ncd` stream (NQ 03-26 only). Optional refinement: download Bid/Ask tick streams in NT8 (free, manual).

## 4. Phase 0 — Taxonomy and kill gate

Two steps, so labels are never pre-registered blind against unknown scales.

**0a — Descriptive, no labels.** Distributions of: first-candle range R30; print density and size in the first minutes; breaks per session; gap vs pre-halt close. Defines the *degenerate-session exclusion*: sessions where R30 is too small to trade (threshold set from the 0a distribution, e.g. a low quantile or ≤ spread multiple, then frozen).

**Freeze.** Label definitions written to `research/preregistration.json`, immutable afterwards:
- *Break:* first print ≥ 1 tick beyond H (or below L) between 18:00:30 and 18:15:30 ET.
- *Re-arm:* after a labeled whipsaw, keep watching both sides until window end. All break events enter the corpus; the simulated strategy takes only the first confirmed entry per session.
- *Whipsaw vs real (ground truth, detector-independent):* after a break, whichever comes first — price trades back inside the range (≥1 tick inside) within timeout X ⇒ **whipsaw**; price reaches extension E = k×R30 beyond the level ⇒ **real**. If X expires with neither, the break failed to extend ⇒ **whipsaw** (conservative). k and X are chosen from 0a quantiles, then frozen.

**0b — Base rates + Oracle gate (G0).** Whipsaw fraction, MFE/MAE by horizon, and the project's most important number: **expectancy of entering only real breaks with perfect foresight, net of costs**. No detector can beat the oracle; if the oracle can't pay, archive here.

## 5. Phase 1 — Detector A (price/time confirmation)

After a break, confirmation fires when **both** conditions are met without price re-entering the range: T seconds elapsed since the break, and extension beyond the level reached. Setting a component to 0 disables it, so hold-only and extension-only rules are representable.

- Grid (pre-registered, 24 cells): hold T ∈ {0, 2, 5, 10, 20, 30} s × confirmation extension ∈ {0, 0.25, 0.5, 1.0}×R30. The (T=0, ext=0) cell **is** the naive no-filter baseline.
- Simulated entry: market at confirmation price + 1 tick slippage + spread (chasing pays the full spread — modeled explicitly).
- Fixed management for detector comparison (not optimized): structural stop at the opposite candle extreme; two exit views — bracket 1R/2R (primary) and fixed horizon 5/15 min (tie-break). Primary metric: **expectancy per session in R, bracket view**.
- Chronological 60/40 split: calibrate on the first tramo, validate on the last. All cells reported.
- Baseline: the naive strategy (enter at break, no filter). A detector cell must beat naive *and* costs on the validation tramo.

## 6. Phase 2 — Overlay B (order flow)

- **Degeneracy check first:** print density/size in the 5 s post-break at the reopen. If the tape is too thin for flow features to carry information (floor frozen after 0a), Phase 2 closes documented and the strategy stays price-only.
- Candidate features (recycled from the BigPrints recorder pair analysis, thresholds recalibrated for reopen tape): signed aggressor delta in windows (0,1s], (1,2s], (2,5s] post-break; sweep throughput/uniformity during the break leg; max-print causal percentile vs trailing ≥30 s history.
- Rule form: **one additional veto** on top of the best Phase 1 cell. Adopted only if it improves the validation tramo.

## 7. Gates

| Gate | Kill condition |
|------|----------------|
| **G0** oracle | Perfect-foresight expectancy ≤ costs → archive project |
| **G1** detector A | Best cell on validation ≤ costs or ≤ naive baseline → archive or redesign the signal; never re-tune past the split |
| **G2** overlay B | No validation improvement → ship price-only |
| **G3** NinjaScript | Written only after G1; Playback must show Python↔NT8 parity |

## 8. Pipeline validation

- Anti-lookahead by construction: the simulator consumes the tape strictly in time order; every decision uses only prints ≤ t (asserted in code).
- `research/test_events.py`: one synthetic session with fabricated breaks/whipsaws verifying labeling, re-arm, and confirmation logic. One test file, no framework ceremony.

## 9. Repo layout

```
LatigoBreak/
  README.md
  docs/specs/          this document; future NT8 spec if G1 passes
  research/
    ncd_parse.py       vendored parser (origin: MFF-Sim)
    preregistration.json   frozen after 0a
    ...                phase scripts + outputs
```

## 10. Open items

- Optional: trigger NT8 Bid/Ask historical tick download for the NQ/MNQ contracts to replace the Roll-estimator spread with measured spread (manual, ~2 clicks, non-blocking).

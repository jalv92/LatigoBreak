# Phase 1 — Detector Grid & G1 Verdict

NQ usable sessions: 211 (cal 126 / val 85, chronological; split date 2026-01-28). Eligibility: >=15 cal entries. Entry deadline 18:20:00 ET (pre-registered).

| cell | cal n | cal E[R]/sess | val n | val E[R]/sess | val E[R]/trade | val win% | val $/trade | chase med (t) |
|---|---|---|---|---|---|---|---|---|
| h0_x0 | 119 | -0.0734 | 84 | -0.2635 | -0.267 | 38 | -167.12 | 2 |
| h0_x0.25 | 103 | -0.0489 | 81 | -0.2539 | -0.266 | 37 | -172.71 | 29 |
| h0_x0.5 | 87 | -0.0606 | 65 | -0.1193 | -0.156 | 40 | -86.35 | 48 |
| h0_x1.0 | 62 | -0.0626 | 41 | -0.0274 | -0.057 | 44 | -25.11 | 87 |
| h2_x0 | 113 | -0.0749 | 84 | -0.2395 | -0.242 | 39 | -176.76 | 9 |
| h2_x0.25 | 103 | -0.0557 | 81 | -0.2404 | -0.252 | 38 | -159.50 | 28 |
| h2_x0.5 | 87 | -0.0500 | 65 | -0.1024 | -0.134 | 42 | -64.27 | 45 |
| h2_x1.0 | 62 | -0.0626 | 41 | -0.0232 | -0.048 | 44 | -27.30 | 85 |
| h5_x0 | 113 | -0.0857 | 84 | -0.2713 | -0.274 | 37 | -181.29 | 15 |
| h5_x0.25 | 103 | -0.0702 | 81 | -0.2801 | -0.294 | 36 | -179.19 | 26 |
| h5_x0.5 | 87 | -0.0601 | 65 | -0.1158 | -0.151 | 40 | -78.27 | 45 |
| h5_x1.0 | 62 | -0.0608 | 41 | -0.0274 | -0.057 | 44 | -25.11 | 87 |
| h10_x0 | 113 | -0.0451 | 82 | -0.1431 | -0.148 | 44 | -104.32 | 19 |
| h10_x0.25 | 102 | -0.0416 | 80 | -0.1652 | -0.175 | 41 | -123.81 | 29 |
| h10_x0.5 | 87 | -0.0575 | 65 | -0.1162 | -0.152 | 40 | -81.04 | 48 |
| h10_x1.0 | 62 | -0.0615 | 41 | -0.0274 | -0.057 | 44 | -25.11 | 87 |
| h20_x0 | 110 | -0.0853 | 82 | -0.1640 | -0.170 | 43 | -104.74 | 22 |
| h20_x0.25 | 101 | -0.0474 | 80 | -0.1627 | -0.173 | 41 | -119.50 | 26 |
| h20_x0.5 | 87 | -0.0710 | 65 | -0.0766 | -0.100 | 45 | -64.04 | 47 |
| h20_x1.0 | 62 | -0.0670 | 41 | -0.0198 | -0.041 | 44 | -14.74 | 85 |
| h30_x0 | 107 | -0.0615 | 82 | -0.0272 | -0.028 | 49 | -9.99 | 29 |
| h30_x0.25 | 101 | -0.0406 | 80 | -0.0442 | -0.047 | 48 | -33.31 | 32 |
| h30_x0.5 | 87 | -0.0494 | 65 | -0.0398 | -0.052 | 46 | -15.65 | 48 |
| h30_x1.0 | 62 | -0.0739 | 41 | -0.0191 | -0.040 | 44 | +1.35 | 85 |

**Selected on calibration: h30_x0.25** (hold=30s, ext=0.25xR30) -> validation E[R]/session = -0.0442 vs naive -0.2635.

MNQ replication (69 sessions, no split): n=61, E[R]/session=-0.0383, E[$/trade]=-0.80.

## Reading (analyst notes, 2026-07-30)

Every one of the 24 cells is negative on both tramos — but not equally: loss
shrinks monotonically as confirmation gets stricter (naive −0.264 R/session on
validation → h30 family −0.02..−0.04; h30_x1.0 reaches −$−0 per trade,
n=41). Two conclusions follow:

1. **The whipsaw filter works as designed** — it recovers most of the money the
   naive chase burns (validates the project's core intuition directionally).
2. **The chase entry eats the entire residual edge.** Median chase for the
   x1.0 cells is ~85 ticks beyond the level: by the time price has *proven*
   the break, the entry is ~21 NQ points late and the oracle's +$236 is gone.
   The gap oracle→causal is an entry-price problem, not a detection problem.

Any continuation is a REDESIGN under spec §7 (new pre-registration; this
validation tramo is partially burned — a second pass over it must be reported
with that caveat), not a re-tune of this grid.

## G1 verdict

**G1 FAIL** — chosen cell validation E[R]/session = -0.0442 (<= 0 [required]; beats naive -0.2635 [required]). Per spec section 7: archive or redesign the signal; no re-tuning past the split.
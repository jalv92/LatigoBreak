# v4 flow-gate scan — big-print support vs break outcome

Cluster rules: same-side aggressor prints, <=150 ms gap, <=1500 ms span; support window 120 s pre-break + trigger grace 5 s. Aggressor side = bid/ask HEURISTIC (corr +0.52) — NT8 tape is ground truth. 18:00 tramo is burned (Phase 0/1) — **calibration-only numbers.**

## NQ (203 sessions)

### threshold 30c — supported 680/2918 breaks (23%)
- real-rate: supported **1.5%** (10/680) vs unsupported 0.5% (11/2238) | z=+2.65
- MFE@120s median (ticks): supported 33 vs unsupported 23
- TRIGGER 1R (first supported break/session): n=41 | win%=46 | E[$/trade]=-126.33 (t=-1.30) | total=$-5180
- TRIGGER 2R (first supported break/session): n=41 | win%=44 | E[$/trade]=-163.28 (t=-1.72) | total=$-6694

### threshold 50c — supported 313/2918 breaks (11%)
- real-rate: supported **1.9%** (6/313) vs unsupported 0.6% (15/2605) | z=+2.65
- MFE@120s median (ticks): supported 28 vs unsupported 24
- TRIGGER 1R (first supported break/session): n=17 | win%=35 | E[$/trade]=-223.91 (t=-1.53) | total=$-3806
- TRIGGER 2R (first supported break/session): n=17 | win%=35 | E[$/trade]=-176.56 (t=-1.02) | total=$-3002

### threshold 80c — supported 146/2918 breaks (5%)
- real-rate: supported **2.1%** (3/146) vs unsupported 0.6% (18/2772) | z=+1.96
- MFE@120s median (ticks): supported 23 vs unsupported 25
- TRIGGER 1R (first supported break/session): n=9 | win%=44 | E[$/trade]=-79.50 (t=-0.38) | total=$-716
- TRIGGER 2R (first supported break/session): n=9 | win%=44 | E[$/trade]=+9.94 (t=0.04) | total=$+90

### threshold 120c — supported 96/2918 breaks (3%)
- real-rate: supported **2.1%** (2/96) vs unsupported 0.7% (19/2822) | z=+1.61
- MFE@120s median (ticks): supported 22 vs unsupported 25
- TRIGGER 1R (first supported break/session): n=1 | win%=100 | E[$/trade]=+55.50 (t=55500000000.00) | total=$+56
- TRIGGER 2R (first supported break/session): n=1 | win%=100 | E[$/trade]=+55.50 (t=55500000000.00) | total=$+56

## MNQ (67 sessions)

### threshold 30c — supported 1178/2186 breaks (54%)
- real-rate: supported **0.2%** (2/1178) vs unsupported 0.3% (3/1008) | z=-0.62
- MFE@120s median (ticks): supported 38 vs unsupported 31
- TRIGGER 1R (first supported break/session): n=47 | win%=36 | E[$/trade]=-14.61 (t=-1.66) | total=$-686
- TRIGGER 2R (first supported break/session): n=47 | win%=30 | E[$/trade]=-20.21 (t=-2.38) | total=$-950

### threshold 50c — supported 755/2186 breaks (35%)
- real-rate: supported **0.4%** (3/755) vs unsupported 0.1% (2/1431) | z=+1.20
- MFE@120s median (ticks): supported 44 vs unsupported 30
- TRIGGER 1R (first supported break/session): n=25 | win%=32 | E[$/trade]=-17.62 (t=-1.27) | total=$-440
- TRIGGER 2R (first supported break/session): n=25 | win%=32 | E[$/trade]=-17.54 (t=-1.22) | total=$-438

### threshold 80c — supported 438/2186 breaks (20%)
- real-rate: supported **0.5%** (2/438) vs unsupported 0.2% (3/1748) | z=+1.12
- MFE@120s median (ticks): supported 47 vs unsupported 30
- TRIGGER 1R (first supported break/session): n=14 | win%=29 | E[$/trade]=-36.93 (t=-2.34) | total=$-517
- TRIGGER 2R (first supported break/session): n=14 | win%=29 | E[$/trade]=-33.39 (t=-1.92) | total=$-468

### threshold 120c — supported 196/2186 breaks (9%)
- real-rate: supported **0.0%** (0/196) vs unsupported 0.3% (5/1990) | z=-0.70
- MFE@120s median (ticks): supported 42 vs unsupported 34
- TRIGGER 1R (first supported break/session): n=7 | win%=29 | E[$/trade]=-53.57 (t=-2.70) | total=$-375
- TRIGGER 2R (first supported break/session): n=7 | win%=29 | E[$/trade]=-53.57 (t=-2.70) | total=$-375

## Reference baselines (phase0b/phase1, same cost model)

- Naive first-break 1R: -$110.34/trade (n=203). Oracle 1R: +$236.29 (n=19).
- Best Phase-1 cell h30_x0.25: -0.0442 R/session on validation.

## How to read this

- The Filter arm needs supported real-rate >= ~38-47% to break even at the level.
- Kill signals: no separation (z ~ 0), support fires on nearly every break, or supported events rarer than 1 per 15 sessions.
- Playback `latigo_flow_log.jsonl` (real tape) is ground truth over this scan.
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

### threshold 55c — supported 268/2918 breaks (9%)
- real-rate: supported **1.9%** (5/268) vs unsupported 0.6% (16/2650) | z=+2.33
- MFE@120s median (ticks): supported 26 vs unsupported 25
- TRIGGER 1R (first supported break/session): n=14 | win%=36 | E[$/trade]=-164.50 (t=-1.07) | total=$-2303
- TRIGGER 2R (first supported break/session): n=14 | win%=36 | E[$/trade]=-107.00 (t=-0.57) | total=$-1498

### threshold 60c — supported 214/2918 breaks (7%)
- real-rate: supported **2.3%** (5/214) vs unsupported 0.6% (16/2704) | z=+2.91
- MFE@120s median (ticks): supported 26 vs unsupported 25
- TRIGGER 1R (first supported break/session): n=12 | win%=42 | E[$/trade]=-131.58 (t=-0.74) | total=$-1579
- TRIGGER 2R (first supported break/session): n=12 | win%=42 | E[$/trade]=-64.50 (t=-0.30) | total=$-774

### threshold 65c — supported 181/2918 breaks (6%)
- real-rate: supported **2.2%** (4/181) vs unsupported 0.6% (17/2737) | z=+2.45
- MFE@120s median (ticks): supported 26 vs unsupported 25
- TRIGGER 1R (first supported break/session): n=11 | win%=45 | E[$/trade]=-104.50 (t=-0.55) | total=$-1150
- TRIGGER 2R (first supported break/session): n=11 | win%=45 | E[$/trade]=-31.32 (t=-0.13) | total=$-344

### threshold 70c — supported 179/2918 breaks (6%)
- real-rate: supported **2.2%** (4/179) vs unsupported 0.6% (17/2739) | z=+2.48
- MFE@120s median (ticks): supported 26 vs unsupported 25
- TRIGGER 1R (first supported break/session): n=10 | win%=50 | E[$/trade]=-26.50 (t=-0.14) | total=$-265
- TRIGGER 2R (first supported break/session): n=10 | win%=50 | E[$/trade]=+54.00 (t=0.22) | total=$+540

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

### threshold 60c + max-print >= 3c — supported 203/2918 breaks (7%)
- real-rate: supported **2.0%** (4/203) vs unsupported 0.6% (17/2715) | z=+2.19
- MFE@120s median (ticks): supported 26 vs unsupported 25
- TRIGGER 1R (first supported break/session): n=11 | win%=45 | E[$/trade]=-116.32 (t=-0.60) | total=$-1280
- TRIGGER 2R (first supported break/session): n=11 | win%=45 | E[$/trade]=-43.14 (t=-0.18) | total=$-474

### threshold 60c + max-print >= 5c — supported 185/2918 breaks (6%)
- real-rate: supported **1.6%** (3/185) vs unsupported 0.7% (18/2733) | z=+1.50
- MFE@120s median (ticks): supported 26 vs unsupported 25
- TRIGGER 1R (first supported break/session): n=6 | win%=50 | E[$/trade]=-314.50 (t=-1.22) | total=$-1887
- TRIGGER 2R (first supported break/session): n=6 | win%=50 | E[$/trade]=-314.50 (t=-1.22) | total=$-1887

### threshold 60c + max-print >= 8c — supported 172/2918 breaks (6%)
- real-rate: supported **1.7%** (3/172) vs unsupported 0.7% (18/2746) | z=+1.64
- MFE@120s median (ticks): supported 26 vs unsupported 25
- TRIGGER 1R (first supported break/session): n=5 | win%=60 | E[$/trade]=-308.50 (t=-1.00) | total=$-1542
- TRIGGER 2R (first supported break/session): n=5 | win%=60 | E[$/trade]=-308.50 (t=-1.00) | total=$-1542

### threshold 60c + max-print >= 10c — supported 170/2918 breaks (6%)
- real-rate: supported **1.8%** (3/170) vs unsupported 0.7% (18/2748) | z=+1.66
- MFE@120s median (ticks): supported 26 vs unsupported 25
- TRIGGER 1R (first supported break/session): n=4 | win%=50 | E[$/trade]=-487.00 (t=-1.48) | total=$-1948
- TRIGGER 2R (first supported break/session): n=4 | win%=50 | E[$/trade]=-487.00 (t=-1.48) | total=$-1948

### threshold 60c + max-print >= 15c — supported 166/2918 breaks (6%)
- real-rate: supported **1.8%** (3/166) vs unsupported 0.7% (18/2752) | z=+1.71
- MFE@120s median (ticks): supported 26 vs unsupported 25
- TRIGGER 1R (first supported break/session): n=2 | win%=100 | E[$/trade]=+148.00 (t=2.26) | total=$+296
- TRIGGER 2R (first supported break/session): n=2 | win%=100 | E[$/trade]=+148.00 (t=2.26) | total=$+296

### threshold 60c + max-print >= 20c — supported 162/2918 breaks (6%)
- real-rate: supported **1.9%** (3/162) vs unsupported 0.7% (18/2756) | z=+1.75
- MFE@120s median (ticks): supported 26 vs unsupported 25
- TRIGGER 1R (first supported break/session): n=2 | win%=100 | E[$/trade]=+148.00 (t=2.26) | total=$+296
- TRIGGER 2R (first supported break/session): n=2 | win%=100 | E[$/trade]=+148.00 (t=2.26) | total=$+296

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

### threshold 55c — supported 733/2186 breaks (34%)
- real-rate: supported **0.3%** (2/733) vs unsupported 0.2% (3/1453) | z=+0.31
- MFE@120s median (ticks): supported 44 vs unsupported 29
- TRIGGER 1R (first supported break/session): n=24 | win%=33 | E[$/trade]=-20.40 (t=-1.46) | total=$-490
- TRIGGER 2R (first supported break/session): n=24 | win%=33 | E[$/trade]=-17.23 (t=-1.14) | total=$-414

### threshold 60c — supported 674/2186 breaks (31%)
- real-rate: supported **0.3%** (2/674) vs unsupported 0.2% (3/1512) | z=+0.44
- MFE@120s median (ticks): supported 41 vs unsupported 30
- TRIGGER 1R (first supported break/session): n=22 | win%=32 | E[$/trade]=-24.00 (t=-1.60) | total=$-528
- TRIGGER 2R (first supported break/session): n=22 | win%=32 | E[$/trade]=-22.55 (t=-1.43) | total=$-496

### threshold 65c — supported 624/2186 breaks (29%)
- real-rate: supported **0.3%** (2/624) vs unsupported 0.2% (3/1562) | z=+0.57
- MFE@120s median (ticks): supported 45 vs unsupported 30
- TRIGGER 1R (first supported break/session): n=19 | win%=32 | E[$/trade]=-20.87 (t=-1.28) | total=$-396
- TRIGGER 2R (first supported break/session): n=19 | win%=32 | E[$/trade]=-17.82 (t=-1.02) | total=$-338

### threshold 70c — supported 551/2186 breaks (25%)
- real-rate: supported **0.4%** (2/551) vs unsupported 0.2% (3/1635) | z=+0.76
- MFE@120s median (ticks): supported 47 vs unsupported 29
- TRIGGER 1R (first supported break/session): n=17 | win%=29 | E[$/trade]=-23.56 (t=-1.33) | total=$-400
- TRIGGER 2R (first supported break/session): n=17 | win%=29 | E[$/trade]=-19.68 (t=-1.03) | total=$-334

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

### threshold 60c + max-print >= 3c — supported 662/2186 breaks (30%)
- real-rate: supported **0.3%** (2/662) vs unsupported 0.2% (3/1524) | z=+0.47
- MFE@120s median (ticks): supported 42 vs unsupported 31
- TRIGGER 1R (first supported break/session): n=22 | win%=32 | E[$/trade]=-24.00 (t=-1.60) | total=$-528
- TRIGGER 2R (first supported break/session): n=22 | win%=32 | E[$/trade]=-22.55 (t=-1.43) | total=$-496

### threshold 60c + max-print >= 5c — supported 554/2186 breaks (25%)
- real-rate: supported **0.4%** (2/554) vs unsupported 0.2% (3/1632) | z=+0.75
- MFE@120s median (ticks): supported 45 vs unsupported 30
- TRIGGER 1R (first supported break/session): n=16 | win%=25 | E[$/trade]=-29.38 (t=-1.54) | total=$-470
- TRIGGER 2R (first supported break/session): n=16 | win%=25 | E[$/trade]=-29.53 (t=-1.56) | total=$-472

### threshold 60c + max-print >= 8c — supported 366/2186 breaks (17%)
- real-rate: supported **0.3%** (1/366) vs unsupported 0.2% (4/1820) | z=+0.20
- MFE@120s median (ticks): supported 37 vs unsupported 33
- TRIGGER 1R (first supported break/session): n=8 | win%=25 | E[$/trade]=-37.25 (t=-3.01) | total=$-298
- TRIGGER 2R (first supported break/session): n=8 | win%=25 | E[$/trade]=-37.25 (t=-3.01) | total=$-298

### threshold 60c + max-print >= 10c — supported 321/2186 breaks (15%)
- real-rate: supported **0.0%** (0/321) vs unsupported 0.3% (5/1865) | z=-0.93
- MFE@120s median (ticks): supported 38 vs unsupported 33
- TRIGGER 1R (first supported break/session): n=8 | win%=25 | E[$/trade]=-37.25 (t=-3.01) | total=$-298
- TRIGGER 2R (first supported break/session): n=8 | win%=25 | E[$/trade]=-37.25 (t=-3.01) | total=$-298

### threshold 60c + max-print >= 15c — supported 289/2186 breaks (13%)
- real-rate: supported **0.3%** (1/289) vs unsupported 0.2% (4/1897) | z=+0.45
- MFE@120s median (ticks): supported 38 vs unsupported 33
- TRIGGER 1R (first supported break/session): n=8 | win%=25 | E[$/trade]=-36.19 (t=-2.99) | total=$-290
- TRIGGER 2R (first supported break/session): n=8 | win%=25 | E[$/trade]=-36.19 (t=-2.99) | total=$-290

### threshold 60c + max-print >= 20c — supported 255/2186 breaks (12%)
- real-rate: supported **0.4%** (1/255) vs unsupported 0.2% (4/1931) | z=+0.58
- MFE@120s median (ticks): supported 37 vs unsupported 34
- TRIGGER 1R (first supported break/session): n=6 | win%=0 | E[$/trade]=-52.67 (t=-6.00) | total=$-316
- TRIGGER 2R (first supported break/session): n=6 | win%=0 | E[$/trade]=-52.67 (t=-6.00) | total=$-316

## Reference baselines (phase0b/phase1, same cost model)

- Naive first-break 1R: -$110.34/trade (n=203). Oracle 1R: +$236.29 (n=19).
- Best Phase-1 cell h30_x0.25: -0.0442 R/session on validation.

## How to read this

- The Filter arm needs supported real-rate >= ~38-47% to break even at the level.
- Kill signals: no separation (z ~ 0), support fires on nearly every break, or supported events rarer than 1 per 15 sessions.
- Playback `latigo_flow_log.jsonl` (real tape) is ground truth over this scan.
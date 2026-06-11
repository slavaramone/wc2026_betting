# Wc26.Betting.ScoreModel

Urgent WC2026 score-props model project.

## Step 1: market-implied fixture xG

Build fixture xG from group-stage 1X2 + total odds:

```bash
dotnet run --project src/Wc26.Betting.Console -- build-market-xg \
  --match-odds-file data/raw/odds/wc2026_group_stage_fresh_odds_1x2_handicap_totals.csv \
  --output-folder C:\Temp\wc26\score-model \
  --overwrite
```

Outputs:

- `wc26-fixture-market-xg.csv`
- `wc26-fixture-market-xg.json`
- `wc26-fixture-market-xg-diagnostics.csv`
- `wc26-fixture-market-xg-group-diagnostics.csv`

## Step 2: fixture score matrix

Build normalized 0..N score probabilities from market xG:

```bash
dotnet run --project src/Wc26.Betting.Console -- build-score-matrix \
  --market-xg-file C:\Temp\wc26\score-model\wc26-fixture-market-xg.json \
  --output-folder C:\Temp\wc26\score-model \
  --max-goals 10 \
  --overwrite
```

CSV input also works:

```bash
dotnet run --project src/Wc26.Betting.Console -- build-score-matrix \
  --market-xg-file C:\Temp\wc26\score-model\wc26-fixture-market-xg.csv \
  --output-folder C:\Temp\wc26\score-model \
  --max-goals 10 \
  --overwrite
```

Outputs:

- `wc26-fixture-score-matrix.csv`
- `wc26-fixture-score-matrix.json`
- `wc26-fixture-score-matrix-diagnostics.csv`

`Probability` is normalized inside the 0..N score grid. `RawProbability` is the direct independent-Poisson probability before tail normalization. Use diagnostics `GridMass` to check tail loss.

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

## Step 3: group prop simulation

Simulate group special props from fixture score matrix:

```bash
dotnet run --project src/Wc26.Betting.Console -- simulate-group-props --score-matrix-file C:\Temp\wc26\score-model\wc26-fixture-score-matrix.json --output-folder C:\Temp\wc26\score-model --iterations 200000 --seed 2026 --overwrite
```

Outputs:

- `wc26-group-prop-simulation.json`
- `wc26-group-special-prop-probabilities.csv`
- `wc26-group-simulation-diagnostics.csv`

## Step 4: group prop market comparison

Compare simulated prop probabilities with group special props odds:

```bash
dotnet run --project src/Wc26.Betting.Console -- compare-group-props --prop-probabilities-file C:\Temp\wc26\score-model\wc26-group-prop-simulation.json --prop-odds-file data\raw\odds\wc2026_group_special_props_score_modelable_odds.csv --output-folder C:\Temp\wc26\score-model --edge-threshold 0.08 --min-odds 1.60 --overwrite
```

Outputs:

- `wc26-group-special-props-comparison.csv`
- `wc26-group-special-props-edges.csv`
- `wc26-group-special-props-comparison-diagnostics.csv`
- `wc26-group-special-props-comparison.json`

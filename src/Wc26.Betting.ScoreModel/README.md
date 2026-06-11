# Wc26.Betting.ScoreModel

Urgent WC2026 score-props model project.

Step 1 implemented: market-implied fixture xG from group-stage 1X2 + total odds.

Run:

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

The diagnostics CSV is sorted by biggest 1X2 fitting error.

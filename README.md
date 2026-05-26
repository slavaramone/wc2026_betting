# wc26_betting

Skeleton C#/.NET 10 solution for WC2026 betting-model research.

## Projects

- `src/Wc26.Betting.Console` — CLI entry point.
- `src/Wc26.Betting.Core` — domain/services project.

## First command

```bash
dotnet run --project src/Wc26.Betting.Console -- grab-sofascore
```

With options:

```bash
dotnet run --project src/Wc26.Betting.Console -- grab-sofascore \
  --output C:\Temp\wc26\sofascore \
  --date-from 2026-06-01 \
  --date-to 2026-07-20 \
  --tournament-id 16 \
  --season-id 0
```

Current behavior: creates a stub JSON file. No Sofascore network parsing is implemented yet.

## Next planned modules

- Sofascore parser/downloader
- player ratings CSV import
- national-team rating builder
- World Cup tournament simulator
- market odds import
- value report generator

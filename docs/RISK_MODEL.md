# Risk model file (JSON)

One JSON file describes everything uncertain about a schedule. It refers to
activities through **filters**, so one model can cover thousands of activities
and still apply after the schedule is updated. See `testdata/synth_500.risk.json`
for a complete example.

```json
{
  "name": "Q3 update - risk workshop 14 Sep",
  "simulation": {
    "iterations": 2000,
    "seed": 20260921,
    "sampling": "lhs",
    "convergence": { "enabled": false, "percentile": 80, "toleranceDays": 1,
                     "batchSize": 250, "minIterations": 500, "maxIterations": 10000 }
  },
  "uncertainty":  [ ... ],
  "risks":        [ ... ],
  "drivers":      [ ... ],
  "correlations": [ ... ]
}
```

## Filters

A filter is an object; every key present must match (logical AND).

| Key | Matches | Example |
| --- | --- | --- |
| `all` | every activity | `{ "all": true }` |
| `activities` | listed activity IDs (unknown IDs are an error) | `{ "activities": ["A1010", "A1020"] }` |
| `wbs` | WBS path segment(s), e.g. `CIV` matches `PRJ.CIV` and `PRJ.CIV.FOUND` | `{ "wbs": ["CIV", "MECH"] }` |
| `code` | activity code type → value(s) | `{ "code": { "Discipline": ["PIP", "INS"] } }` |
| `namePattern` | regular expression on the activity name, case-insensitive | `{ "namePattern": "^Install" }` |
| `critical` | on the deterministic critical path, with float measured to the project's own finish (a Must Finish By does not change it) | `{ "critical": true }` |
| `exclude` | removes listed IDs from the result | `{ "wbs": "CIV", "exclude": ["A1500"] }` |

Milestones, LOE/WBS summaries and completed activities are skipped automatically
(with a warning where it matters).

## Duration uncertainty

```json
{ "filter": { "all": true }, "distribution": "pert", "min": 90, "mostLikely": 100, "max": 125, "units": "percent" }
```

- `distribution`: `triangle`, `pert` (Beta-PERT) or `uniform`.
- `units: "percent"` (default) scales the **remaining** duration: 90/100/125 means -10% / as planned / +25%.
- `units: "days"` gives absolute remaining durations in working days of the activity's calendar.
- Entries are applied in order; a later entry **replaces** an earlier one for the activities it selects,
  so start with a broad default and follow it with specific overrides.

## Risk register (discrete risks)

```json
{ "id": "R01", "title": "Late vendor data", "probability": 0.40,
  "impact": { "distribution": "triangle", "min": 10, "mostLikely": 20, "max": 45, "units": "days" },
  "filter": { "wbs": "PROC" },
  "mitigated": { "probability": 0.15,
                 "impact": { "distribution": "triangle", "min": 5, "mostLikely": 10, "max": 20, "units": "days" } } }
```

- Sampled **once per iteration**. If it occurs, the same impact is added to the remaining duration of
  **every** mapped activity (`days` = working days on each activity's calendar; `percent` = % of that
  activity's remaining duration). Mapping one risk to a long chain therefore compounds - map it to the
  activities it really hits.
- `mitigated` is optional; missing fields fall back to the pre-mitigation values. `--scenario both` runs
  pre and post with the same seed so the difference is the effect of the mitigation, not sampling noise.
- `activities: [...]` can be used instead of `filter`.

## Risk drivers

```json
{ "id": "D01", "title": "Site productivity", "probability": 1.0,
  "distribution": "triangle", "min": 95, "mostLikely": 105, "max": 125,
  "filter": { "wbs": ["CIV", "MECH", "ELEC"] } }
```

A driver is a percentage multiplier sampled once per iteration and applied to every mapped activity,
so it correlates them naturally. Several drivers on one activity multiply. Order of application per
activity: uncertainty, then drivers (in file order), then risks (in file order).

## Correlation

```json
{ "filter": { "code": { "Discipline": "ELE" } }, "coefficient": 0.6 }
```

Rank correlation between the **uncertainty** of the selected activities (Iman-Conover, marginals
unchanged). Activities need an uncertainty entry to take part. If the groups you define cannot all be
true at once, off-diagonal coefficients are scaled down until they can, and a warning says by how much.

## Simulation settings

- `sampling`: `lhs` (Latin Hypercube, default) or `mc` (plain Monte Carlo).
- `seed`: same seed + same files = identical results on any machine and any number of CPU cores.
- `convergence.enabled`: run in batches of `batchSize` until the chosen percentile moves by no more than
  `toleranceDays` for two consecutive batches (after `minIterations`), capped at `maxIterations`.

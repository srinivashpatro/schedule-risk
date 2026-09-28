# Risk register file (Excel or JSON)

The qualitative risk register: a probability-severity matrix and the risks scored on it. The app saves
and opens it as a file on your device, like the risk model (docs/RISK_MODEL.md); nothing is uploaded.
The file is an Excel workbook (.xlsx, [below](#excel-workbook)) or JSON; both hold the whole register, and
the app and every `--register` option read either.
Approved risks that meet the promote rule become quantified risks in the model (step "Promote").
Read and written by `ScheduleRisk.Core.Risk.Register.RiskRegister`.

```json
{
  "version": 1,
  "name": "Q3 risk workshop",
  "matrix": { ... },
  "risks": [ ... ]
}
```

`version` is the file format (1). A file from a newer version is refused. Missing fields take their
defaults, and a file without `matrix` uses the default matrix below.

## Matrix

```json
"matrix": {
  "probability": [
    { "letter": "A", "label": "Remote",      "min": 0,    "max": 0.05, "guidance": "..." },
    { "letter": "B", "label": "Unlikely",    "min": 0.05, "max": 0.25 },
    { "letter": "C", "label": "Occasional",  "min": 0.25, "max": 0.50 },
    { "letter": "D", "label": "Likely",      "min": 0.50, "max": 0.70 },
    { "letter": "E", "label": "Most likely", "min": 0.70, "max": 0.95 }
  ],
  "severityLevels": [ "Insignificant", "Minor", "Significant", "Major", "Very high" ],
  "dimensions": [
    { "id": "schedule", "name": "Schedule", "unit": "% of planned project duration",
      "bands": [ { "text": "No delay to the finish; absorbed by the available float", "min": 0, "max": 0 },
                 { "text": "...", "min": 0, "max": 3 }, { "text": "...", "min": 3, "max": 6 },
                 { "text": "...", "min": 6, "max": 10 }, { "text": "...", "min": 10, "max": 20 } ] },
    { "id": "safety", "name": "Health and safety", "bands": [ { "text": "..." }, ... ] }
  ],
  "ratings": [ "GGGAA", "GGGAA", "GGAAA", "GGAAR", "GAARR" ],
  "guidance": [ { "rating": "red", "label": "Red", "actions": [ "Report to senior management at every review" ] } ],
  "promote": { "minRating": "amber", "scheduleDimension": "schedule", "minScheduleSeverity": "II" },
  "categories": [ "Design", "Procurement", "Construction" ],
  "costOfDelayPerDay": 0,
  "currency": ""
}
```

- **probability**: bands from least to most likely, as fractions (0.25 = 25%), without overlaps. A band
  reaching above 95% gets a warning: a risk that close to certainty belongs in the base schedule or
  estimate, not the register.
- **severityLevels**: the columns, numbered I, II, III... (2 to 10 levels).
- **dimensions**: the areas severity is judged on, each with one band per level. With a `unit` the area is
  measured and each band has `min` and `max` in that unit (the last band may leave out `max`, meaning "and
  above"); without one the bands are described in words. The default matrix has schedule (% of the
  planned project duration; V is capped at 20% for turning it into days), cost (% of the approved control
  budget), quality and performance, health and safety, environment and regulatory.
- **ratings**: one row per probability band (first row = least likely), one letter per severity level:
  `R` red, `A` amber, `G` green. Ratings are looked up, not computed from a score, so any matrix can be
  entered. Cells are named severity.probability, for example `IV.D`.
- **guidance**: what each rating asks of the project.
- **promote**: which risks go on to the quantitative model: approved, rated at least `minRating` on the
  current assessment, and at least `minScheduleSeverity` on the schedule area.
- **costOfDelayPerDay** and **currency**: the value of one working day of delay to the finish, for the
  cost-benefit of responses (0 = not set).

## Risks

```json
{ "id": "R01", "title": "Late heavy-lift crane",
  "cause": "Single crane supplier", "event": "Crane arrives late", "effect": "Erection slips",
  "category": "Procurement", "kind": "threat", "status": "approved",
  "raisedBy": "Planner", "raised": "2026-09-14", "owner": "Site manager",
  "inherent": { "probability": "D", "severity": { "schedule": "V", "cost": "IV" } },
  "current":  { "probability": "B", "severity": { "schedule": "V" } },
  "target":   { "probability": "B", "severity": { "schedule": "I" } },
  "response": "mitigate", "responseDescription": "Book a second supplier", "responseCost": 2500000,
  "actions": [ { "text": "Enquiry to a second supplier", "owner": "Procurement", "due": "2026-10-15", "status": "open" } ] }
```

- **kind**: `threat` or `opportunity`. **status**: `proposed`, `approved`, `rejected` or `closed`.
- **inherent**, **current**, **target**: the risk before any controls, with today's controls, and once the
  response is carried out. Probability is a band letter; severity is a level (I, II...) for each area that
  applies. The risk's severity is its worst area, and its cell and rating follow from that. Current and
  target become the pre- and post-mitigation risk in the model.
- **response**: `avoid`, `transfer`, `mitigate` or `accept` for a threat; `exploit`, `share`, `enhance`
  or `accept` for an opportunity; `none` until chosen. **responseCost** is in the register's currency.
- **actions**: `status` is `open`, `done` or `cancelled`; an open action past its `due` date is overdue.
- Dates are `yyyy-mm-dd`.
- **promotion** (optional): how the risk goes into the model at Promote.

```json
"promotion": { "activities": ["A04700", "A04720"],
               "probability": 0.4, "impact": { "distribution": "triangle", "min": 20, "mostLikely": 35, "max": 70 },
               "mitigatedProbability": 0.1, "mitigatedImpact": { "min": 0, "mostLikely": 5, "max": 10 } }
```

  `activities` are the activity IDs the risk would delay. The other fields are values set by hand; left out,
  they are worked out from the matrix as below. `impactUnits` (`days`, the default, or `percent` of each
  activity's remaining duration) applies to the impacts set by hand. `always: true` promotes the risk
  whatever its rating (it must still be approved); risks moved from a model get it, so moving them does
  not change the model.

## Excel workbook

The register as a workbook, for filling in and reviewing in Excel. In the app, the register file control
(steps 01 to 03 and Promote) has **Open...** (.xlsx or .json), **Save as Excel**, **Save as JSON**, **Excel
template** (an empty workbook on the register's matrix) and **New**. Opening a file replaces the register.
Read and written by `ScheduleRisk.Core.Risk.Register.RegisterWorkbook`, with nothing but .NET's own zip and
XML readers, so the file never leaves the browser. From the command line:

```
sra register template --out register.xlsx              # empty workbook on the default matrix
sra register register.json --out register.xlsx         # JSON to Excel, or the other way round
```

A blank template on the default matrix is also in the repository: [risk-register-template.xlsx](risk-register-template.xlsx).

The workbook has four sheets:

| Sheet | Holds |
| --- | --- |
| **Guide** | How to fill it in, what the app checks, and every column. Not read back. |
| **Risks** | One row per risk, under a row of group headings: the risk; Inherent, Current and Target (probability, a severity column per area, and a Rating); the response; the model link. |
| **Actions** | One row per action: Risk ID, Action, Owner, Due, Status. |
| **Matrix** | The matrix in blocks, each a title in column A, a header row and its rows: Settings, Probability bands, Severity levels, Rating grid, Severity areas, Severity ranges, Rating guidance, Categories. |

Columns of the Risks sheet: ID, Title, Cause, Event, Effect, Category, Type (Threat or Opportunity), Status,
Raised by, Raised, Owner; then for each of Inherent, Current and Target: Probability (a band letter), one
column per severity area (a level I, II... or empty), and Rating; then Response, Response description,
Response cost; then the model link, all optional: Model activities (IDs separated by commas), Always promote
(Yes), Impact units (days or percent), Probability (by hand), Impact distribution (triangle, pert or
uniform), Impact min, most likely and max, and the same four for Mitigated.

What the workbook does for the person filling it in:

- Dropdowns on every coded column: type, status, response, the probability letters, the severity levels
  and the categories come from the Matrix sheet (defined names `ProbabilityLetters`, `SeverityLevels`,
  `Categories`, `RiskGrid`, `RiskGridLetters`, `RiskIds`). Wrong entries get a warning, not a block.
- Each Rating column is an Excel formula (`INDEX` and `MATCH` on the rating grid, the worst area setting the
  column) coloured Red, Amber or Green, saved with the app's own rating as its value, so it shows before
  Excel recalculates. The app ignores these columns and works every rating out again.
- Frozen group and header rows and ID and Title columns, filters, 200 empty rows ready for new risks, and
  the app's look: ink headers, the red accent, thin rules.

Reading it back:

- Columns are found by their header (any capitals and spacing), so they can be moved, and columns of your
  own are ignored. Assessment columns belong to the group heading above them; a header such as
  `Current schedule` works without one. Severity columns match an area's name or id.
- Empty rows are skipped. An empty ID gets the next free one (R01, R02...).
- Enumerations accept any capitals. Dates are date cells or text `yyyy-mm-dd` (also `dd-MMM-yyyy`).
  Probabilities read 30%, 0.3 and 30 alike.
- A workbook without a Matrix sheet, or without one of its blocks, uses the default matrix for that part;
  only the Risks sheet is required.
- A value that cannot be read stops the file opening, with its sheet, row and column, for example
  `Risks, row 4, column Status: "Maybe" is not one of: Proposed, Approved, Rejected, Closed.`
- A register written as Excel and read back is the same register: saving it as JSON gives the same file.

## Promote

Approved risks that meet the promote rule on their current assessment become discrete risks in the risk
model (docs/RISK_MODEL.md), with the register's ids, in the app (step "Promote") or from the command line:

```
sra promote schedule.xer --register register.json [--risk model.json] [--out promoted.risk.json]
```

- **Planned duration**: from the project start (the earliest start in the deterministic schedule, actual
  starts included) to the deterministic finish, in working days of the project calendar; the same figure
  the Results call the deterministic duration.
- **Probability**: the midpoint of the probability band (A 2.5%, B 15%, C 37.5%, D 60%, E 82.5% by default).
- **Impact**: a triangle in working days from the schedule band: its low, middle and high % of the planned
  duration, rounded to 0.1 day. Level IV (6-10%) on a 500-day plan gives 30 / 40 / 50. A band without an
  upper limit cannot be turned into days. An opportunity's days are negative (time saved).
- **Pre- and post-mitigation**: the current assessment before mitigation, the target after it. Without a
  target there is no mitigation; a target without a schedule severity means no delay after mitigation.
- The risk applies to the activities in `promotion.activities`; a risk mapped to none is skipped.
- The model's discrete risks come from the register: the app promotes before every run, and
  `sra simulate --risk model.json --register register.json` does the same.
- In the model, promoted risks carry `"source": "register"`. Promoting again updates them and removes the
  ones that no longer meet the rule; risks typed into the model are never changed. When a model risk
  typed by hand already uses a register id, that risk is skipped until one of them is renamed.

## Moving a model's risks to the register

Risks typed into a model (no `"source": "register"`) can be moved to the register: in step 05 ("Move to the
register") or with `sra promote schedule.xer --register register.json --risk model.json --import
[--register-out register.json] [--out model.json]`. Each becomes an approved register risk that promotes
back to exactly the same model risk, so results stay the same for the same seed:

- its probability, impacts and mitigation are kept as values set by hand, in their own unit;
- its filter becomes the list of activities it applies to (noted when it was a WBS, code, name or critical
  filter, since the list no longer follows changes to the schedule);
- it is marked `always`, so it stays in the model whatever its rating;
- its current assessment is the probability band its probability falls in and the schedule level of its
  mean impact as a share of the planned duration (a % impact on the mean remaining duration of its
  activities); a mitigated risk gets a target assessment the same way, for the heat map;
- an id the register already uses is renamed, in the register and the model.

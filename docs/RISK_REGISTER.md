# Risk register file (JSON)

The qualitative risk register: a probability-severity matrix and the risks scored on it. The app saves
and opens it as a file on your device, like the risk model (docs/RISK_MODEL.md); nothing is uploaded.
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

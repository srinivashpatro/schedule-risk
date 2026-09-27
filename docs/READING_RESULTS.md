# Reading the results: "What the results mean"

Every run gets a short set of plain-language notes, one per figure in the Summary, followed by a glossary of the
terms they use. They are for readers with no statistics background: what the P50 and P80 promise, what the mean
and median say, what skewness and kurtosis mean for the finish, and how far to trust the run.

The notes are built from fixed templates and thresholds by `ResultsNarrative` in the core library
(`src/ScheduleRisk.Core/Reporting/ResultsNarrative.cs`), from the same figures the Summary shows. No model and no
network call is involved: the same run always reads the same way, and nothing leaves the browser. They appear:

- in the browser app, under the Summary panel on the Results page (the glossary starts folded; a click opens it);
- in the HTML report, straight after the Summary;
- in the PDF and Word reports, straight after the Summary, as label and text rows;
- in the PowerPoint deck, as two slides after the Summary's: the notes, then the glossary.

## Conventions

- Days are working days of the project calendar. In the prose they are rounded to whole days with "about"
  ("about 128 working days"), and shares of the planned duration to whole percents.
- Dates are the Summary's dates (dd-MMM-yyyy). Skewness, kurtosis and sensitivities are the values shown, to 2
  decimals, and every threshold on them is read on that shown value, so a note never contradicts the figure beside it.
- A chance is the whole percent shown in the Summary. When it would read 0% or 100% without being exactly none or
  all of the outcomes, the notes count them instead ("only 3 of the 500 simulated outcomes finish by then").
- With post-mitigation results, the notes on drivers and critical activities start "Before mitigation," because
  those figures come from the pre-mitigation run.
- Every sentence has at most 25 words (a test checks every template on several runs).

## Thresholds

All are named constants in `ResultsNarrative`.

| Constant | Value | Used for |
| --- | --- | --- |
| `VeryUnlikelyBelow` | 10 | a chance under 10% is "very unlikely" |
| `MoreLikelyFrom` | 50 | under 50% "less likely than not", from 50% "more likely than not" |
| `LikelyFrom` | 80 | from 80% "likely" |
| `LevelTolerance` | 2 | a P-date reads as its stated chance when the share finishing by it is within 2 points; otherwise the actual share is given |
| `SameCentreWithin` | 1.0 | mean and median less than 1 working day apart are "about the same" |
| `SymmetricBelow` | 0.5 | skewness under 0.5 either way: "spread roughly evenly" |
| `StrongAbove` | 1.0 | skewness over 1 either way: a "strong" tail (else "moderate") |
| `HeavyAbove` | 1.0 | excess kurtosis over 1: very early or late outcomes more common than in a bell curve |
| `FlatBelow` | -1.0 | excess kurtosis under -1: flatter than a bell curve, or two groups |
| `NearMeanWindow` | 0.05 | two groups: the window around the mean, 5% of the P10 to P90 range (at least 1 working day) |
| `NearMeanShare` | 0.03 | two groups: fewer than 3% of outcomes inside that window |
| `DominatesRatio` | 2.0 | a driver "dominates" when its sensitivity is at least twice the next one's |
| `RoughBelow` | 1000 | with fewer outcomes the P-dates are "rough estimates" |

Critical (50% or more of outcomes) and near-critical (10% to 49%) are the Summary's own thresholds, in
`ResultsSummary`.

## The notes, in order

| Label | Shown | What it says |
| --- | --- | --- |
| Current finish | always | The deterministic finish, with no allowance for risk, and how likely it is (the bands above) with the share of outcomes finishing by it. |
| Must Finish By | the project has one | The deadline set in P6 and how likely it is, the same way. |
| No spread | every outcome is the same | Replaces the P50 to Spread notes: the model adds no variation. |
| P50 | there is spread | A coin flip (when half the outcomes finish by it, within the tolerance; else the actual share), how far it is from the current finish, and that a coin flip is not a date to promise. |
| P80 | there is spread | An 80% chance and a 1-in-5 chance of finishing later (or the actual share), and the contingency needed to promise it; or that the current finish already has that chance. |
| P*n* (chosen) | the level chosen on the chart is not 50 or 80 | Its chance and date, and how far it is from the current finish. Highlighted on the Results page. |
| Mean and median | there is spread | Both dates; whether they agree, or the mean is dragged later or earlier by the tail. With two groups (kurtosis under -1 and few outcomes near the mean) it says the average is not a likely finish date. |
| Skewness | there are 3 or more outcomes | Which way the outcomes lean, and how strongly; a late tail adds "often when risks occur" when the model has risks. |
| Kurtosis | there are 4 or more outcomes | Close to a bell curve, heavier tails, or flatter / two groups (with a pointer to the histogram). |
| Spread | there is spread | The P10 to P90 dates and the width of that range, in working days and as a share of the planned duration. |
| Mitigation | post-mitigation results | Where the P80 moves and how the chance of the current finish changes. |
| What drives it | always | The driver that dominates, the top one and the next two, the activity durations when the model has no risks or drivers, or that nothing moves the finish. |
| Critical activities | always | How many activities are critical and near-critical, or that the critical chain keeps changing. |
| About these results | always | How many outcomes the results rest on, and that they are only as good as the schedule and model; rough with fewer than 1,000 outcomes, and whether the run converged. |

## Terms used

The glossary explains P-level, P50, P80, deterministic finish, contingency, mean, median, standard deviation,
skewness, kurtosis, criticality, sensitivity and working days, one or two sentences each. It replaces the note
that used to sit under the Summary.

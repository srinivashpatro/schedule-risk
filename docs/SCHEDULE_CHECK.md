# Schedule check (step 04)

Before a schedule is used for risk analysis, the app checks it two ways, side by side:

1. **P6 Check Schedule**: the parameters of P6 Professional's Tools > Check Schedule dialog, in its three
   tabs (Relationships and Assignments, Dates and Durations, Constraints).
2. **DCMA 14-Point Assessment**: the published schedule-quality checklist.

The two measure mostly the same facts against different targets, so both are shown. Built by
`ScheduleRisk.Core.Analysis.HealthCheck`; shown in step 04, in every report (HTML, PDF, Word, PowerPoint),
and by `sra validate`, which also writes the JSON:

```
sra validate schedule.xer [--json health.json] [--long-lag-hr 352] [--large-float-hr 352] [--large-duration-hr 352]
```

It exits with 1 when any check fails.

## Where the values come from

Float, remaining durations and early finishes come from the schedule as the engine recalculated it. When
`sra verify` (the engine check in step 04) matches, these are exactly the values P6 stored in the file,
and they are also available for files P6 never scheduled. Actual dates, constraints, relationships, the
status code and the baseline (target) dates are read from the XER.

## P6 Check Schedule parameters

Each is `count / denominator` compared with a target by an operator. The defaults are the owner's P6
template; every threshold, operator and target can be changed (`HealthCheckSettings`).

| Tab | Parameter | Counts | Denominator | Default |
|---|---|---|---|---|
| Relationships | Logic | activities missing a predecessor or successor | activities | < 5% |
| Relationships | Negative lags | relationships with lag < 0 | relationships | < 1% |
| Relationships | Positive Lags | relationships with lag > 0 | relationships | > 5% (see below) |
| Relationships | Long Lags | relationships with lag > 352 h | relationships | < 5% |
| Relationships | Relationship Types | Finish to Start relationships | relationships | < 90% (see below) |
| Relationships | Out Of Sequence | activities progressed before a predecessor allowed it | activities | < 2% |
| Relationships | Resources / Cost | work activities without a resource or expense | activities | < 1% |
| Relationships | Dangling Start | not-started activities whose start no FS or SS predecessor drives | activities | <= 0% |
| Relationships | Dangling Finish | activities whose finish drives no FS or FF successor | activities | <= 0% |
| Dates | Large Float | total float > 352 h | activities | < 1% |
| Dates | Negative Float | total float < 0 | activities | < 1% |
| Dates | Large Durations | remaining duration > 352 h (not LOE or milestones) | activities | < 5% |
| Dates | Invalid progress dates | actual dates after the data date, status and actuals that disagree, finish before start | activities | < 1% |
| Dates | Late Activities | finishing after their baseline finish | activities | < 5% |
| Dates | BEI | completed / due by the data date per the baseline | due activities | > 0.95 |
| Constraints | Hard Constraints | Start On, Finish On, Mandatory Start, Mandatory Finish | activities | < 1% |
| Constraints | Soft Constraints | On or Before / After, As Late As Possible | activities | < 5% |

"Activities" leaves out WBS summaries. 352 hours is 44 eight-hour working days, P6's default and DCMA's
threshold. Relationships include links to and from activities in other projects in the file.

**Two operators read against their own description** in the owner's template: Positive Lags is `> 5%`
(most guidance, DCMA included, wants less lag) and Relationship Types is `< 90%` ("the majority should be
Finish to Start"). Both are reported as configured and never silently turned round; Relationship Types
also carries `status_conventional` (FS share of 90% or more), and DCMA #3 and #4 give the conventional
reading. Confirm which your organisation means.

**Not applicable**: Out Of Sequence, Late Activities and BEI need progress and a baseline, so a file with no
actual dates (a baseline export) gives N/A with the reason. Resources / Cost needs a TASKRSRC table in the
file; without one, assignments were not exported, which is not the same as "unresourced".

**Logic** exempts the project's start and finish: activities without a predecessor (or successor) are not
counted when they are at most 1% of the activities, the intended open ends.

## DCMA 14-Point Assessment

| # | Check | Target |
|---|---|---|
| 1 | Logic | < 5% |
| 2 | Leads (negative lag) | 0 |
| 3 | Lags (positive lag) | < 5% |
| 4 | Relationship Types (FS share) | >= 90% |
| 5 | Hard Constraints | < 5% |
| 6 | High Float (> 44 working days) | < 5% |
| 7 | Negative Float | 0 |
| 8 | High Duration (> 44 working days) | < 5% |
| 9 | Invalid Dates | 0 |
| 10 | Resources | < 5%, informational |
| 11 | Missed Activities | < 5% |
| 12 | Critical Path Test | pass or fail |
| 13 | Critical Path Length Index (CPLI) | >= 0.95 |
| 14 | Baseline Execution Index (BEI) | >= 0.95 |

**#12 Critical Path Test**, by the DCMA method on the engine: the open activity with the least float
(earliest first) is delayed by 600 working days; the test passes when the project finish moves as far as
that activity's finish, which shows an unbroken critical path to the finish.

**#13 CPLI** = (critical path length + project total float) / critical path length, in working days of the
project calendar. The critical path length runs from the data date to the finish; the project total
float is the Must Finish By minus the finish, or 0 without one.

Both differ from a check based on P6's `driving_path_flag`, which fails files that have no flags (for
example files P6 never scheduled).

## JSON

`sra validate --json` and step 04's "Download JSON" write the owner's health-check format, so their Word
report builder reads it unchanged:

- `meta`: project, data date, activity and relationship counts, `is_unstarted_or_baseline_only`,
  `has_resource_cost_data`, `task_type_breakdown`, and `app_notes` (for example constraints the engine does
  not model yet).
- `p6_check_schedule`: one object per parameter with `section`, `key`, `label`, `description`, `operator`,
  `target`, `unit`, `actual`, `count`, `denominator`, `status` (`PASS`, `FAIL`, `N/A`), `note`,
  `flag_key`, `flagged` and `flagged_total`; Relationship Types adds `status_conventional` and
  `breakdown`.
- `dcma_14_point`: the same with `number` and `name`. `status` can also be `FAIL (informational)` (#10).
- `flagged` lists every flagged activity (`task_id`, `task_code`, `task_name` and what flagged it:
  `total_float_hr`, `remaining_duration_hr`, `constraint`, `reason`, `reasons`, `baseline_finish` and
  `current_finish`) or relationship (`pred_task_code`, `pred_task_name`, `succ_task_code`,
  `succ_task_name`, `pred_type`, `lag_hr`). A P6 parameter and its DCMA twin share one list (`flag_key`).

Checked against the owner's `health_check.py` on every fixture in testdata/: the same counts, statuses
and flagged activities, except DCMA #12 and #13 (method above) and float shown to 4 decimals of an hour.

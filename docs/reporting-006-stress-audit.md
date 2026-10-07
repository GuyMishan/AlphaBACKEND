# Employer Interface 006 reporting stress audit

Last reviewed: 2026-10-02

## Scope

This audit covers ALPHA's Employer Interface 006 pension reporting flow end-to-end:

1. manual report creation and resumable drafts
2. Excel/CSV intake
3. XML/DAT/TST intake
4. canonical report snapshots
5. employee/product/contribution/payment validation
6. official Version 006 workbook rules and XSD validation
7. export and file naming
8. transmission and failure handling
9. clearing-house feedback ingestion and normalization
10. correction workspaces, negative/current technical documents and revision promotion
11. tenant authorization and immutable evidence
12. concurrency, duplicate delivery and database constraints
13. security and large/failing input paths
14. Reports & Feedback operational reads
15. frontend flow review (audit only unless separately approved)

Authoritative sources remain:

- `docs/specifications/employer-interface/006/Employer interface V 6.xlsx`
- the four official Version 006 XSD files in that directory
- `docs/specifications/mislaka/`

## Safety invariants

- A submitted/transmitted business report is immutable.
- A browser timeout or disconnect does not imply the clearing house did not receive the payload.
- A provider exception after dispatch is an ambiguous transmission outcome and must not become a blind retry.
- Once a report has entered transmission, downloadable XML evidence must be the exact persisted transmitted payload, not a regenerated approximation.
- Only one concurrent sender may claim a Validated report.
- Duplicate clearing-house feedback must be idempotent at the database boundary.
- Feedback association must use exact saved identifiers; it must not guess across unrelated transfers.
- Multiple transfer blocks for the same fund remain distinct when operation/payment/previous-reference semantics differ.
- A correction workspace never mutates its immutable source.
- Correction materialization is atomic: either all technical correction documents are created and the workspace is claimed, or the whole transaction rolls back.
- Current correction previous references follow product lineage to the matching negative technical product.
- Added / Changed / Removed / Unchanged classification is derived from source-vs-workspace state, not sticky UI flags.
- Unchanged products are not retransmitted.
- Manual, Excel and XML paths must converge on the same persisted reporting model and final backend validation/export rules.
- Tenant scope is enforced server-side for report, product, payment evidence, feedback and export access.
- XML parsing prohibits DTD/XXE and enforces bounded document size.
- Sensitive report identifiers, bank accounts, provider payloads and official feedback evidence remain encrypted at rest.

## Findings fixed during this audit

| Severity | Finding | Fix | Regression |
| --- | --- | --- | --- |
| Critical | Provider exception after V006 dispatch changed the report to editable `Error`, allowing a potentially duplicated retry after an unknown remote outcome | Ambiguous provider exceptions keep the report locked in `Processing` and return `transmission_reconciliation_required` | `ReportTransmissionSafetyRegressionTests` |
| High | Feedback `MISPAR-ZIHUI` propagation updated every product in the same fund, even when that fund contained multiple distinct transfer blocks | Propagation is restricted to the exact transfer semantics rather than fund alone | `EmployerInterfaceTransferCorrelationRegressionTests` |
| High | Current correction previous references were refreshed by fund and could select the wrong negative transfer when the same fund was split | Previous reference refresh follows current product -> workspace product -> original product -> matching negative product lineage | `CorrectionWorkflowRegressionTests.Correction_previous_references_follow_product_lineage_not_only_fund` |
| High | XML download for `Processing/Sent/Completed` reports regenerated Version 006 XML with a new preparation time/default sequence rather than returning the exact sent evidence | Post-transmission XML download decrypts and returns the persisted `ReportTransmission.Payload` byte-for-byte; if evidence is not available, generation fails closed | `EmployerInterfaceImmutableEvidenceRegressionTests` |
| High | Repeated employee blocks across different transfer groups could contain conflicting employee snapshots and import silently kept whichever block was encountered first | Current-report import rejects conflicting snapshots for the same identifier/type across transfer blocks | `EmployerInterfaceImportConsistencyRegressionTests` |
| Medium | Correction materialization catch path rolled back a transaction and then attempted another update through the completed transaction | Failure path now performs a full atomic rollback and does not issue post-rollback writes through that transaction | `CorrectionMaterializationRollbackRegressionTests` |
| Medium | Concurrent final validations could surface an unhandled concurrency exception instead of explicitly rejecting stale validation | `DbUpdateConcurrencyException` returns `409 report_changed_during_validation`; stale validation cannot be committed | `ReportValidationConcurrencyRegressionTests` |

## Verified implementation areas

| Area | Scenario / invariant | Status |
| --- | --- | --- |
| XML security | DTD/XXE disabled, XmlResolver null, document character ceiling | Passed |
| XML upload | Empty/oversized/wrong extension rejected | Passed |
| XSD | Current and negative output validated against dedicated official XSDs | Passed |
| Workbook rules | Operation/payment combinations and conditional bank/trust-account rules | Passed |
| Contribution mapping | Official contribution codes and irrelevant empty editor placeholders | Passed |
| Employee identifier | Israeli ID and passport snapshots supported in reporting/import | Passed |
| Import tenant binding | Uploaded employer registration must match selected employer | Passed |
| Import month | Exactly one salary month required | Passed |
| Free-plan import | Staged employments included in entitlement check | Passed |
| Draft resume | Existing editable report IDs are resumed instead of duplicated | Reviewed / covered |
| Dirty state | Changes while the report is still editable mark it dirty; successful Final Validation then freezes the report until transmission | Passed |
| Double validation | Optimistic concurrency rejects stale commit | Fixed |
| Double send | Atomic `Validated -> Processing` claim | Passed |
| Browser disconnect after persisted transmission evidence | Provider send detached from request-abort token | Passed |
| Provider exception / uncertain result | Blind retry blocked pending reconciliation | Fixed |
| File sequence | PostgreSQL upsert increments sender/day sequence atomically and caps at 9999 | Passed |
| Duplicate feedback | Unique employer + payload hash database boundary | Passed |
| Feedback source evidence | Raw official feedback preserved as encrypted immutable evidence | Passed |
| Feedback contribution association | Saved contribution record identifiers used; unmatched rows are not guessed | Passed |
| Same-fund multi-transfer feedback | Transfer correlation does not bleed across transfer semantics | Fixed |
| Correction workspace uniqueness | Partial unique index prevents multiple open workspaces for one source | Passed |
| Correction materialization claim | Workspace atomically claimed as Processing inside transaction | Passed |
| Correction rollback | Partial materialization is rolled back completely | Fixed |
| Delta | Added / Changed / Removed / Unchanged computed source-vs-workspace | Passed |
| New product in correction | Emitted as current operation 1 | Passed |
| Changed product | Negative operation 6 + current operation 2/3 | Passed |
| Removed product | Negative operation 6 only | Passed |
| Previous contribution IDs | Current correction derives previous record from matching negative contribution | Passed |
| Previous transfer IDs | Current correction follows product lineage | Fixed |
| Revision promotion | Workspace promoted only after all technical documents are sent/completed | Passed |
| Old revision correction | Newer completed revision prevents correction of superseded revision | Passed |
| Tenant isolation | Main report/read/write/transmission/export queries scope organization + employer server-side | Reviewed / passed on inspected endpoints |
| Evidence download | Sent XML comes from immutable transmission payload | Fixed |
| Payment evidence | Report/product ownership checked server-side; immutable versioned records | Passed |
| Large tables/API shape | Report and deposit lists are paged/infinite-loaded; deposit evidence has report-wide listing | Reviewed |
| N+1 hotspots | Deposit/report screens batch major contribution/payment/feedback reads | Reviewed |

## Frontend audit findings awaiting explicit approval

No frontend code was modified during this audit.

### High — hard-coded mock transmission provider

`src/lib/report-transmission-api.ts` currently defines:

`send(..., provider = "MockClearinghouse")`

The normal report wizard calls `send(...)` without passing a provider. That makes the browser explicitly request the mock provider rather than letting the backend select the configured real provider. In production, where mock transmission correctly fails closed, this can make a valid real-provider configuration fail at the frontend boundary.

Recommended frontend change: do not send a provider unless the user/operator explicitly selected one. Let the backend choose its configured provider by default.

This remains **not changed** pending approval.

## Additional aggressive regression round

The follow-up stress round added ten explicit cases to the existing reporting test suites:

1. same-fund correction with two different original transfer identifiers; negative cancellation must preserve the exact source-product reference
2. simulated process crash after a report has been claimed for transmission; Processing remains immutable/non-editable
3. late/old feedback projection after a newer transmission attempt; operational reads use only feedback correlated to the latest attempt
4. duplicate feedback at the PostgreSQL boundary; employer + payload hash remains a unique idempotency key
5. large current report with 120 separate transfer groups; builder + workbook validation + official current-report XSD must all pass
6. manual and Employer Interface import paths converge on the same ManualReport/Employee/Product/Contribution canonical persistence model
7. a successfully Final-Validated report is immutable before transmission; edits are rejected so validated bytes cannot drift before send
8. multi-user/stale validation is protected by the EF concurrency token plus an explicit 409 conflict path
9. mutation sweep corrupts one field at a time in an otherwise valid V006 payload; workbook or XSD validation must reject every mutation
10. ambiguous provider/reconciliation path remains non-retryable and correction materialization rollback remains atomic

This round found an additional same-fund correction bug: negative operation 6 previously derived its previous transfer identifier from the first product in the fund. It now uses the exact original product's saved InterfaceTransferIdentifier, falling back only to that product's own immutable ID.

## Additional transmission/import hardening round

A further stress pass added:

- domain-level transmission lifecycle tests for immutable payload copying, accepted timestamps, explicit rejection semantics, retry eligibility after an explicit rejection, duplicate Processing claims and terminal-status validation
- a same-fund current-correction case where two products have different previous transfer/clearing references; the exporter must emit two separate transfer blocks
- imported-correction source correlation now fails closed when official previous identifiers resolve to more than one local source report or more than one local transmission source, instead of selecting an arbitrary FirstOrDefault match
- a PostgreSQL concurrency test that reserves 20 Employer Interface file sequences simultaneously for one sender/day and requires the exact unique monotonic set 1..20
- mutation tests restricted to structurally/schema-invalid corruptions to avoid treating values that the official V006 schema permits as invalid merely by assumption

The imported-correction ambiguity check is a safety fix discovered by this round: ALPHA's correction model has one SourceReportId, so a file that resolves to multiple local ancestors is rejected rather than silently attaching the correction to the wrong revision.

### Feedback matching hardening

The feedback stress pass also removed the dormant fund-only fallback from `PropagateTransferIdentifiersAsync`. Transfer feedback now requires an exact saved product/transfer identifier match; ALPHA will not fall back to "first product in the same fund" when the official transfer identifier is unknown. A regression guard ensures the fund-only fallback cannot be reintroduced silently.

### Full-suite recheck and new database boundary tests

The existing backend test suite was re-run after the reporting fixes: 284 tests passed together on the green reporting head with 0 failures, 0 skipped and 0 build warnings.

The next stress increment adds:
- PostgreSQL exhaustion coverage for daily file sequence 9999 (must fail closed without wrapping or reusing a number)
- tenant-scope guards for transmission, Employer Interface export/preflight and report-feedback endpoints
- PostgreSQL unique-boundary tests for duplicate (ReportId, AttemptNumber) transmission attempts
- PostgreSQL unique-boundary tests for duplicate (EmployerId, PayloadHash) feedback, while allowing the same payload hash for a different employer

## Coverage still requiring real external integration

The repository has no real clearing-house test environment. The following cannot be truthfully marked as end-to-end Passed from CI alone:

- actual clearing-house acceptance of generated DAT/TST/XML
- real provider timeout after remote acceptance
- real asynchronous feedback timing/order
- actual clearing-house interpretation of the documented birth-date wire-format discrepancy
- production recipient/sender credentials and clearing identifiers
- very large production-scale files at the clearing-house boundary
- operational reconciliation procedure for a report intentionally left in `Processing` after an ambiguous provider outcome

For these, ALPHA intentionally fails closed rather than assuming success/failure.

## Automated regression files

Primary reporting audit coverage currently lives in:

- `tests/Alpha.Api.Tests/EmployerInterface006XmlBuilderTests.cs`
- `tests/Alpha.Api.Tests/EmployerInterface006PreflightValidationTests.cs`
- `tests/Alpha.Api.Tests/EmployerInterface006FileNamingTests.cs`
- `tests/Alpha.Api.Tests/EmployerInterface006WorkbookDiscoveryTests.cs`
- `tests/Alpha.Api.Tests/EmployerInterfaceLineFeedbackParserTests.cs`
- `tests/Alpha.Api.Tests/CorrectionWorkflowRegressionTests.cs`
- `tests/Alpha.Api.Tests/CorrectionDeltaIntegrationTests.cs`
- `tests/Alpha.Api.Tests/ReportingSchemaSqlGuardTests.cs`

PostgreSQL integration scenarios use the repository's existing integration-test setup where required.

## Verification

Repository verification remains the authoritative completion gate:

```bash
dotnet restore AlphaBackend.slnx
dotnet build AlphaBackend.slnx --no-restore --configuration Release
dotnet test --solution AlphaBackend.slnx --no-build --configuration Release
```

GitHub `backend-ci` runs the same Release restore/build/test sequence. Do not mark a change verified until its workflow succeeds.


## Feedback resolution Stage 9

The feedback-resolution audit now cross-checks the simulator `all-errors` expansion against the complete actionable playbook catalog. Resolution invariants include per-ProblemId persistence, unsupported/stale fail-closed behavior, durable correction-workspace links across refresh, server-side employee/deposit/workspace revalidation, a non-zero correction delta requirement, correction lineage for negative/current technical documents, durable external/reconciliation cases, and actual previous-movement selection for codes 97/98. A newer clearing-house feedback file produces new ProblemIds and therefore cannot be hidden by a historic resolution.

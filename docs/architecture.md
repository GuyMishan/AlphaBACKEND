# Backend architecture

## Context hierarchy

```text
Platform
  Organization
    Employer
      Employment
        Pension product
          Payroll contribution
            Report item
              Payment allocation
              Feedback item
```

Platform roles do not imply organization membership. Organization membership grants access to
either every employer in that organization or an explicit set of employers.

## Internal referents

Referents are explicitly designated users (identity.users.IsReferent) assigned by platform admins to complete organizations and/or individually selected employers across organizations. Two identity-owned assignment tables preserve the distinction; employer assignments reference the employer ID directly and survive ownership transfers. Referents are not platform admins, organization members, employer customer seats or billing customers. All effective access requires the active referent marker and a live assignment. Organization assignment provides operational access to its employers plus editing the organization's general profile; employer assignment provides operational access only to the named employer. Neither grants billing configuration, user/permission administration, or platform administration. Administrative changes are audited; deactivating a referent removes assignments transactionally.

## Organization size classification

SmallOrganization (type 7) is restricted to a single employer across all states; RegularOrganization (type 8) supports multiple employers subject to the subscription plan. Historic organization types 1–6 retain their existing semantics. Employer creation and transfer check the limit under the existing organization entitlement advisory locks; changing a multi-employer organization to SmallOrganization is rejected until extra employers are transferred. Self-service onboarding preserves its existing type 6 semantics.

## Full employer ownership transfer

Each employer has precisely one organization. Platform administrators can atomically transfer an employer to a different organization. The transfer acquires source and target quota advisory locks, refuses duplicate employer identities and duplicate employee identifiers (Israeli IDs or passports), enforces target plan limits and rejects pending transmissions. Employee identities shared with other source employers are cloned so those employers retain their own records. All employer-specific references (employment, report, transmission, feedback and employer payment accounts) are re-scoped; historical encrypted evidence stays bound to its existing record identity and is not decrypted or rewritten. Direct employer-only grants follow the employer, former organization membership-based grants are revoked, and pending employer invitations from the former organization are cancelled. Historic billing ledger and append-only audit records remain untouched. Billing for periods spanning an employer move is deferred and must not be automatically retroactively invoiced without a separate effective-date attribution feature.

## Module roadmap

Implemented foundation:

- Identity and platform users
- Organizations and memberships
- Employers and employer-level access
- People and employments
- Append-only audit events

Next modules:

1. Pension providers, funds, products, and effective-dated contribution settings
2. Payroll periods and staged imports
3. Versioned validation rules and validation findings
4. Immutable report snapshots, batches, and submissions
5. Payment instructions, references, allocations, and reconciliation
6. Raw feedback files, parsed feedback items, and operational exceptions
7. Blob storage and queue-backed background jobs
8. Clearing-house gateway adapters

## Source-of-truth rule

Employer assertions, submitted snapshots, payment evidence, and institutional feedback must be
stored separately. One source must never overwrite another; reconciliation describes the gaps.

Employer Interface 006 summary feedback is retained as encrypted immutable raw evidence and normalized
into query-oriented transfer/contribution feedback rows. Operational treatment status and free-text notes
are a third, separate layer with append-only treatment history. UI comparisons therefore distinguish
what the employer reported, what the manufacturer/clearinghouse returned, and how Alpha users are treating
the exception.

## Security invariants

- Scope every organization query server-side.
- Never trust an organization or employer identifier from the browser without access validation.
- Keep platform support access separate, time-bound, and audited.
- Store secrets outside configuration files in production.
- Do not log national IDs, bank data, report files, or tokens.
- Do not delete submitted financial records; reverse or supersede them.


## Clearing-house transport boundary

ALPHA's default clearing-house role is service bureau/intermediary: outbound Employer Interface traffic uses Annex VI direction `006`. Direct-employer direction `003` is retained as an explicit alternative for a future employer-owned vault, not as the ALPHA default.

Employer Interface transmission is separated from reporting semantics behind `IReportTransmissionProvider`. Before production clearing-house credentials exist, Alpha can exercise the same outbound/inbound boundary with an explicit TEST-only filesystem vault: validated outbound files are atomically written to an employer-scoped outbox, and a hosted inbox worker claims feedback files, validates them against the committed official Version 006 schemas, and sends them through the same encrypted feedback ingestion/correlation/normalization flow used by real clearing-house feedback. The simulator is enabled only when `EmployerInterface006:EnvironmentCode=1` and `EmployerInterface006:SimulatedVault:Enabled=true`. Replacing it with SFTP/API must not change report, transmission, correction, or feedback domain semantics. The optional TEST auto-responder simulates asynchronous clearing-house outcomes after an outbound file appears. Its generated `.simulation.json` inbox messages are a simulator control/data artifact, not an assertion about the clearing house's real feedback file format; genuine XML/DAT/TST feedback continues through official XSD validation.

Initial clearing-house acknowledgements are a separate lifecycle layer from employer summary/business feedback. FEDBKA technical rejections affect the transmission/report state; they must not be normalized as EMPFED contribution feedback. FEDBKB remains a distinct content-level stage and its request-specific error catalog is fail-closed until the authoritative Events Interface schema/codebook is available.

Before transport is invoked, final report validation generates the outbound V006 package and runs a local FEDBKA-style technical preflight. Locally knowable file/format/hierarchy/date failures block transmission. Clearing-house-only facts such as sender authorization and remote duplicate history remain outside local validation and are resolved by the real FEDBKA acknowledgement.

For local development, the simulated clearing-house adapter uses the repository-root `simulated-clearinghouse-vault/` as its persistent filesystem boundary. Runtime payload/feedback files are intentionally excluded from Git; the directory exists so developers can inspect outbox/inbox/processed/failed flow while the TEST simulator is enabled.

## Feedback resolution playbooks

Operational treatment destination is separate from the official V006 feedback error scope. `FeedbackTreatmentTargetCatalog` defines a default Employer/Report/Employee/Deposit/Informational destination for every recognized code, and offers an explicit allow-list for ambiguous codes (18, 45, 56, 102, 103, 111, 112). A money event is not automatically an employer configuration error. Where the evidence does not prove a global employer defect, an authorized operator can persist a treatment-target decision via `POST /report-feedback/{reportId}/resolution-actions/target`. The selected destination is stored as an append-only `target:*` entry against each immutable `problemId` in `FeedbackProblemDecisions`, while actual business decisions and feedback resolutions remain separate. Read models route unresolved problems by the latest valid target decision; this neither rewrites historic reports nor treats target selection as confirmation that a correction succeeded.

Post-feedback resolution is modeled separately from immutable clearing-house evidence and manual treatment history. `Alpha.Application/Reporting/FeedbackResolutionPlaybookCatalog.cs` assigns every official Employer Interface 006 summary-feedback code a product routing contract: resolution type, business family, resolver type, business scope, grouping strategy, correction behavior and allowed actions.

The catalog is intentionally declarative. It does not edit submitted reports, create correction documents, send external messages or close feedback. Future resolution orchestration must consume the catalog, load current/report-time context, and may refine a decision dynamically (for example, an identity mismatch may remain an internal edit when ALPHA is wrong or escalate externally when ALPHA's current value already matches the submitted value). Unknown codes fail closed instead of receiving a guessed resolver.

Informational codes are retained in the catalog for complete official-code coverage but are not actionable resolution steps. Resolution grouping is a product concern (employee, employee+product, contribution, report, transfer, document requirement or original movement) and must not change the provenance or scope of the underlying feedback evidence.

The report-feedback API exposes three read-only resolution contexts: employer scope, report scope and deposit scope. Queue grouping keys include the resolver destination as well as the target entity, preventing identity, employment, document and other resolver steps for the same employee/product from being merged. Live pension-product matching is deterministic: policy/fund identifiers must uniquely match when present, and product-type fallback is permitted only when the immutable snapshot has no stable product reference. Current contribution matching is also unique-or-null. Payment/refund contexts include the effective current employer pension-payment account for comparison without overwriting the submitted payment snapshot. Actionable feedback codes that are not present in the committed playbook catalog are returned explicitly as unsupported and block resolution mode; the API never silently discards or guesses routing for a future/unknown clearing-house code. Each response contains actionable problems only and projects the playbook routing metadata together with stable entity references, an ordered resolver-qualified `groups` queue derived by the backend from the canonical grouping key, and three distinct value bags: `reportedValues` from the immutable report snapshot, `currentValues` from authorized live employer/employee/pension-master data when a deterministic match exists, and `feedbackValues` from normalized clearing-house/manufacturer feedback. A missing or ambiguous live product match is represented as missing current data rather than guessed. The context endpoints do not edit data, create correction reports, send external messages, or mark feedback resolved. Stage 5 intentionally keeps that boundary: the first employee resolver reuses the existing tenant-authorized employee mutation endpoint, while the resolution context supplies the server-owned target identity and a separate `canEditEmployee` capability. Updating live employee master data does not mutate the immutable submitted report or mark its clearing-house feedback resolved. Immediately before an employee edit initiated by resolution mode, the frontend calls a dedicated read/validation action that rebuilds the active deposit resolution context server-side and validates the exact group, employment target and `editEmployee` playbook capability; stale or no-longer-routable groups fail closed.


## Reporting correction revision model

Submitted employer reports are business revisions, while Employer Interface 006 negative/current files created to move between revisions are technical transmission documents. A correction workspace is a full desired next-state snapshot. The delta planner compares it to the latest effective revision and emits only the Added/Changed/Removed rows required by Version 006. Technical documents never become the source of a later user correction; after successful transmission, the workspace itself is promoted to the next immutable business revision. This keeps lineage linear (Revision 1 -> Revision 2 -> Revision 3) while preserving every technical transmission as immutable evidence.


### Feedback resolution internal correction resolvers

Stage 6 introduces resolver-specific internal handling without assuming that every edit belongs in a correction workspace. Contribution, employment-status and report-correction groups whose playbook explicitly declares `CorrectionWorkspace` may enter the existing correction-workspace boundary. Payment groups marked `RevalidateOnly`/dynamic and document groups marked `ExternalFollowUp` stay outside that path and fail closed until their dedicated resolver actions are used. For correction-workspace groups, `ReportFeedbackEndpoints` rebuilds the active feedback context, verifies the exact resolver-qualified group and playbook action, derives the source product when applicable, and only then delegates to `CorrectionWorkflowService.EnsureWorkspaceAsync`.

The correction-workspace resolution action is behavior-gated, not resolver-name-gated: only `Edit + CorrectionWorkspace + PrepareCorrection` playbook groups may create/reuse a correction workspace. Revalidation-only, dynamic and external-follow-up groups remain in their own resolver paths and fail closed if a browser attempts to use the correction-workspace action.

### Per-problem feedback resolution state

Resolution state is operational metadata separate from immutable clearing-house feedback. Each normalized actionable row has a deterministic problem ID and may receive its own persisted resolution record with resolver source, user and timestamp. Resolver groups are navigation only: resolving one problem never resolves sibling errors implicitly. The server re-loads the active feedback row before accepting a resolution, validates that the action source is allowed by its playbook, and excludes only that exact persisted problem from later resolution contexts. Document-resolution evidence is PDF-only, malware-scanned and encrypted at rest.


### Decision/review resolution flow

Stage 7 treats a decision as its own append-only operational fact, separate from both immutable clearing-house feedback and terminal resolution. Each Decision problem is re-authorized and re-derived server-side before a choice is accepted. Allowed outcomes are derived from the committed playbook actions: confirmation, correction preparation, external escalation, financial reconciliation, or linking an original movement. The browser cannot submit an arbitrary outcome.

`confirm` is terminal only for playbooks that explicitly expose `Confirm`, and closes only that `ProblemId`. Choosing `correction` creates/reuses the existing focused correction workspace and records the choice, but the problem remains active until that exact problem passes the relevant workspace validation; the closing endpoint verifies that the latest persisted decision is still `correction`. External/reconcile/link-original choices are persisted as pending handoffs with a required operator note and remain active until their dedicated resolver is implemented/completed. Decision history is append-only in `reporting.feedback_problem_decisions`, and every accepted decision also emits an audit event.


### External feedback cases

Stage 8 models external handling as a durable case rather than a mailto or a terminal flag. A case is keyed by the canonical resolver-qualified business group key and is separate from immutable feedback evidence, so subsequent feedback rows for the same transfer/product/movement can join the same operational case. The case stores status, current assignee, prepared destination/subject/body, linked ProblemIds, append-only events and encrypted attachments. Case mutations are tenant-authorized; attachments are extension-, signature- and malware-validated before encrypted persistence.

Opening a case re-derives the active resolution group and permits only ProblemIds whose committed playbooks expose `OpenExternalCase`. Decision problems routed externally receive an append-only `external` decision as part of the same operation. Closing a case is the only terminal external action and creates per-problem resolution records only for linked problems that are still active and still allow external resolution. New clearing-house feedback therefore remains independently actionable even when an older case was resolved. Email/provider dispatch remains a separate future adapter because recipient and delivery-channel policy is not yet authoritative.


### Stage 9 revalidation and correction closure

Terminal feedback state is now guarded by backend-owned evidence. A ProblemId may be marked resolved only after the action-specific server path proves its completion condition: canonical report validation for editable report data, a non-empty correction delta for correction workspaces, validated evidence upload for document issues, explicit confirmation where allowed, or closure of a durable external/reconciliation case. The browser never supplies an authoritative resolved flag.

Correction-backed problems are linked to workspaces in `reporting.feedback_correction_resolution_links`. The link survives refresh and later continuation, and workspace validation requires the exact pending ProblemIds to be linked to that exact source revision. Employee corrections copy the live, already input-validated master values into the correction report snapshot using report-scoped encryption before validation. A workspace with no semantic change is rejected as a resolution source.

Original-movement errors (97/98) cannot be dismissed with a note. Candidate movements are restricted to immutable sent/completed reports for the same tenant, person, pension product identity and contribution component. Selecting one writes its immutable record identifier into the correction workspace, validates the workspace and resolves only the selected ProblemId. Reconciliation actions use the same durable case lifecycle as other downstream handling so a pending financial investigation remains active until the case is explicitly resolved.


## Manufacturer transmission routing (incremental rollout)

The existing Employer Interface 006 exporter and transmission endpoint operate on an entire report and may contain multiple manufacturers. The first routing foundation, `ManufacturerTransmissionRouting`, reads per-fund-code provider overrides from `Reporting:ManufacturerRouting:Funds:<fundCode>`. Unconfigured funds use the existing configured/default provider. It determines all distinct recipient providers before a transmission is claimed. If a report contains different destinations, the send endpoint rejects it with `manufacturer_route_split_not_implemented` **before** reserving a filename, saving a transmission, or calling any provider. A uniform override is accepted only when the selected request/default provider matches it and the named provider exists and is configured. This prevents a mixed report from leaking recipients' data to the wrong endpoint.

This is a safe routing-preflight foundation, **not yet** automatic hybrid splitting. The subsequent implementation must create immutable recipient-specific, officially validated Employer Interface 006 packages with independent sequence numbers, payload hashes, attempts and idempotency; map transmitted contribution/transfer identifiers back to the source report and product; correlate institution/clearinghouse feedback to the correct attempt and retain the correction's original destination for negatives, replays and deltas; and expose an aggregate user-level status while preserving per-recipient states. Direct provider adapters must not go live without their own contracts, credentials and test fixtures. A fail-closed route is preferable to silently dispatching an entire multi-manufacturer report directly.


### Feature-gated recipient transmission (implementation)

An opt-in `Reporting:ManufacturerRouting:EnableHybridDispatch=true` enables the `HybridReportTransmissionDispatcher`. By default this flag is **off**: existing production behavior does not send split files. A hybrid report is partitioned by the server-resolved recipient provider. The standard 006 exporter can generate one scoped, independently workbook/XSD-validated package per provider without changing the original report. Each recipient has its own immutable `ReportTransmission` record with `RoutingKey`, `RoutedProductIdsJson`, reserved filename, encrypted payload and attachment manifest. The complete plan is saved before dispatch. Individual sends are preceded by a durable `Pending -> Sending` transition. A successful transport response means only that the recipient's transport accepted the document, not that its institution accepted the contributions.

`POST /api/organizations/{organizationId}/employers/{employerId}/manual-reports/{reportId}/transmissions/resume-hybrid` is restricted to authorized transmitters; it continues prepared `Pending` recipients and creates an immutable new attempt for definitively `Rejected` recipients. Already-`Accepted` routes are never re-sent. `Sending`/`Error` outcomes are treated as ambiguous and require manual reconciliation. An original report remains `Processing` until all provider routes accept transport, and only then becomes `Sent`. Historical routing for correction documents uses `ManufacturerHistoricalRoutingResolver` to pin existing product movements to their original transmission recipients; new products use current policy. Feedback active-attempt selection and correction confirmation aggregate the latest attempt independently for each route.

Tenant-authorized inspection endpoints: `GET /{reportId}/transmission-routing` and `GET /{reportId}/transmission-routing/validate` under the manual-reports prefix. The latter validates each scoped outgoing 006 package without dispatch or filename reservation. Transmissions/history report recipient progress. Routing rules use `Reporting:ManufacturerRouting:Funds:<fundCode>=<registered-provider-name>`; the provider must actually be registered and configured. **No hypothetical producer endpoint is constructed**. Direct institution APIs/SFTP, credential mapping and inbound producer-specific feedback normalization require that producer's official integration contract. Even with the hybrid feature enabled, fail closed if a target provider is unconfigured.

Verification: mandatory .NET solution restore/build/test, route-specific XSD fixtures and a stateful Postgres + TEST vault simulation covering at least two recipients, partial rejection, resume, positive/negative producer feedback, amendment and subsequent revision. A Render build alone cannot validate this lifecycle. Do not enable the feature flag in production before this end-to-end evidence is green and real producer adapters are authorized.

### Operational manufacturer-parent routing

`ManufacturerCatalogRouting` uses the curated `reference_data.manufacturer_company_groups` table and pension-products catalog to resolve the manufacturer behind each submitted product using its **fund code together with its legal managing-company name**. Parent-group records in `historical_review` cannot be automatically routed to direct vaults; they continue through the default clearinghouse unless separately reviewed. This matters because numeric fund codes 101, 103 and 163 recur under different companies in the source catalog. A managed parent provider is configured once under `Reporting:ManufacturerRouting:Manufacturers:<ascii-key>`, with `Reporting:ManufacturerRouting:Aliases:<ascii-key>` pointing to its Hebrew manufacturer label. Product-specific fund exceptions override parent routing, while immutable correction-history destinations override current catalog policy. The catalog query is skipped when no manufacturer parent rule is enabled and otherwise executed once per report. The grouping serves product-level routing only; no manual-report source rows are altered.

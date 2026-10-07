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

Post-feedback resolution is modeled separately from immutable clearing-house evidence and manual treatment history. `Alpha.Application/Reporting/FeedbackResolutionPlaybookCatalog.cs` assigns every official Employer Interface 006 summary-feedback code a product routing contract: resolution type, business family, resolver type, business scope, grouping strategy, correction behavior and allowed actions.

The catalog is intentionally declarative. It does not edit submitted reports, create correction documents, send external messages or close feedback. Future resolution orchestration must consume the catalog, load current/report-time context, and may refine a decision dynamically (for example, an identity mismatch may remain an internal edit when ALPHA is wrong or escalate externally when ALPHA's current value already matches the submitted value). Unknown codes fail closed instead of receiving a guessed resolver.

Informational codes are retained in the catalog for complete official-code coverage but are not actionable resolution steps. Resolution grouping is a product concern (employee, employee+product, contribution, report, transfer, document requirement or original movement) and must not change the provenance or scope of the underlying feedback evidence.

The report-feedback API exposes three read-only resolution contexts: employer scope, report scope and deposit scope. Queue grouping keys include the resolver destination as well as the target entity, preventing identity, employment, document and other resolver steps for the same employee/product from being merged. Live pension-product matching is deterministic: policy/fund identifiers must uniquely match when present, and product-type fallback is permitted only when the immutable snapshot has no stable product reference. Current contribution matching is also unique-or-null. Payment/refund contexts include the effective current employer pension-payment account for comparison without overwriting the submitted payment snapshot. Actionable feedback codes that are not present in the committed playbook catalog are returned explicitly as unsupported and block resolution mode; the API never silently discards or guesses routing for a future/unknown clearing-house code. Each response contains actionable problems only and projects the playbook routing metadata together with stable entity references, an ordered resolver-qualified `groups` queue derived by the backend from the canonical grouping key, and three distinct value bags: `reportedValues` from the immutable report snapshot, `currentValues` from authorized live employer/employee/pension-master data when a deterministic match exists, and `feedbackValues` from normalized clearing-house/manufacturer feedback. A missing or ambiguous live product match is represented as missing current data rather than guessed. The context endpoints do not edit data, create correction reports, send external messages, or mark feedback resolved. Stage 5 intentionally keeps that boundary: the first employee resolver reuses the existing tenant-authorized employee mutation endpoint, while the resolution context supplies the server-owned target identity and a separate `canEditEmployee` capability. Updating live employee master data does not mutate the immutable submitted report or mark its clearing-house feedback resolved.


## Reporting correction revision model

Submitted employer reports are business revisions, while Employer Interface 006 negative/current files created to move between revisions are technical transmission documents. A correction workspace is a full desired next-state snapshot. The delta planner compares it to the latest effective revision and emits only the Added/Changed/Removed rows required by Version 006. Technical documents never become the source of a later user correction; after successful transmission, the workspace itself is promoted to the next immutable business revision. This keeps lineage linear (Revision 1 -> Revision 2 -> Revision 3) while preserving every technical transmission as immutable evidence.

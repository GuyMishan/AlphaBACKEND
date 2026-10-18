# Reporting and Employer Interface 006

Business rules of the Reporting module that are not obvious from the code. Authoritative sources for formats and codes: `docs/specifications/employer-interface/006` (XSDs, `Employer interface V 6.xlsx`, error-code workbook) and `docs/specifications/mislaka`. Never infer 006 rules from UI behavior. File locations are in [CODEMAP.md](CODEMAP.md#reporting).

## Safety invariants

- A submitted or transmitted report is immutable. Corrections are separate revisions; a correction workspace never mutates its source.
- Manual, Excel and XML/DAT/TST input converge on the same persisted model and the same final validation and export rules.
- After transmission starts, downloadable XML evidence is the exact persisted payload, never a regeneration.
- Only one sender can claim a validated report. A provider exception after dispatch is an **ambiguous** outcome (report stays `Processing` for reconciliation), never a blind retry; a browser timeout does not mean the clearinghouse did not receive the file.
- Duplicate feedback is idempotent at the database boundary. Feedback is associated by exact saved identifiers, never by guessing across transfers; transfer blocks for the same fund stay distinct when operation, payment or previous-reference semantics differ.
- Correction materialization is atomic: all technical documents are created and the workspace is claimed, or everything rolls back. Added/Changed/Removed/Unchanged comes from source-versus-workspace state; unchanged products are not retransmitted. Current-correction previous references follow product lineage to the matching negative technical product.
- XML parsing disables DTD/XXE and bounds document size. Report identifiers, bank accounts, payloads and feedback evidence are encrypted at rest ([security.md](security.md#sensitive-data)).

## Correction route preview (in development)

`GET /api/organizations/{organizationId}/employers/{employerId}/manual-reports/{workspaceId}/correction-routes`
requires report-creation permission and previews the existing correction materialization
algorithm without mutating any report. It returns `{ routes, canComplete }`. At present,
only the supported `delta-replacement` route is returned, when the workspace
has changes and its transmitted current source is valid. The route describes
whether negative and/or current technical documents would be generated and
how many source/workspace products each would include. An empty result is not
permission to transmit: it means no executable route is currently available.

**Important:** The preview endpoint is read-only; it neither chooses nor persists a route.
`materialize-correction` still has the existing semantics and does not accept a
route selection. The `POST /api/organizations/{organizationId}/employers/{employerId}/manual-reports/{workspaceId}/complete-correction`
endpoint accepts `{ "routeId": "delta-replacement" }`, rechecks the preview on the server,
and materializes the negative/current technical drafts atomically using the existing workflow.
It returns the technical report IDs with `transmitted: false`. This completes preparation,
**not transmission**; the technical documents must still pass validation and be sent in
the prescribed order. No unsupported alternative route is exposed.


API compatibility review: the feature adds exactly two tenant-scoped routes, `GET /{reportId}/correction-routes` and `POST /{reportId}/complete-correction` within the manual-reports group. The architecture route baseline is intentionally updated from 246 to 248 declarations; no existing route was removed or changed.

## Feedback resolution

- Actionable feedback is projected into deterministic per-feedback/contribution/sequence/error **ProblemIds**. A newer feedback file produces new ProblemIds, so an old resolution cannot hide it. Codes without a playbook entry (`FeedbackResolutionPlaybookCatalog`) are explicit "unsupported", never silently mapped; unsupported or stale actions fail closed.
- Report-scoped problems are grouped by official error code within the report (`ReportFeedbackEndpoints.BuildResolutionContextAsync`), not per manufacturer feedback row. Code 102 is not report-scoped: its invalid collective-agreement declaration is tied to the affected employee pension product and is routed through the deposit/product context.
- The report-list `reportIssueCount` uses exactly the same active feedback, persisted treatment-target choices and `(ErrorCode, ErrorDescription)` grouping as the report details (`ReportContextAsync`), so badges always match the issues shown.
- Treatment destination (`FeedbackTreatmentTargetCatalog`, `TreatmentTargetsAsync`) is separate from the official feedback error scope and is not operator-editable. Code 56 targets employer payment settings only for a verified employer-account mismatch; otherwise the deposit payment.
- **Correction lifecycle.** A correction requires an already sent/completed, non-superseded source, a non-zero delta and passing 006 validation. Technical negative/current documents must both reach Sent/Completed before the business workspace is promoted. Promotion means *transmitted, awaiting producer feedback* (`resolutionWorkflowStatus`: `needs-treatment` → `correction-in-progress` → `transmitted-awaiting-feedback`), not institutional confirmation. A linked ProblemId resolves only when later feedback on the latest transmission of the technical reports covers every linked product with no actionable error. Partial, missing, unrelated or stale feedback never closes it.
- **Local change signals** (computed on every resolution-context read, never stored, never a substitute for official feedback): a `needs-treatment` problem becomes `document-updated-awaiting-report` when a matching library document was uploaded after the feedback (codes 111/112: employer-wide type 3; codes 102/103: type 6 on the affected employee pension product — a type-3 upload never satisfies 102/103), and `source-data-updated-awaiting-report` when an `Edit` problem's live source (employer, employment/person or matched pension product, by treatment target) has an `UpdatedAt` later than the feedback. Neither status closes the problem; a corrective report and later manufacturer acceptance are still required. General validation-on-write across all 006-affecting endpoints is not implemented.
- **Codes 102/103 on negative reports** expose `negativeCorrectionSourceReportId`, `negativeCorrectionSourceProductId` and `negativeCorrectionSourceMonth` (`YYYY-MM`) for the correction deep link. Code 102 is grouped by the exact employee + report product so its resolver never aggregates unrelated employees/products. New negative reports record each product's `SourceReportProductId` (`DerivedReportEndpoints`); legacy negatives are matched against the source report by employee, product type, fund, policy, salary month and allocation order, and get a link only for a unique match.
- **Code 50** (duplicate transfer identifier, error-code sheet `סוג שגיאה וסטטוס רשומה`; not payment-treatment status 50) routes to a decision: review, prepare correction or open an external case. No direct confirm or revalidate-only closure.
- External cases and operator decisions are durable records of an operator's decision; they are not proof of delivery or third-party settlement (e-mail dispatch is not implemented).

## Code 18 (missing conditional field)

- The official workbook is copied to `Specifications/EmployerInterface/006/Employer interface V 6.xlsx` in build, publish and the container. At startup `EmployerInterface006FieldMapInitializer` loads it into `reference_data.employer_interface_006_fields`: 393 field records (95 each for outgoing types 12 and 13, 72 each for incoming 14 and 18, 59 for 16) with an `edit_surface` for outgoing fields. The application fails to start if the workbook is missing.
- For code 18 only, `ErrorAmount` is read as the missing field number (`Code18FieldNumber` accepts positive whole numbers). This convention comes from the simulator and is **not yet confirmed by real provider feedback**; treat it as unverified until a real sample confirms it.
- `Code18TargetForField` (XML block ownership) is the single target mapping used for issue scope and resolution context. A code-18 group is keyed by original report, field number, target and, for deposit errors, report product.
- An `edit_surface` classification is not an editor. Correction preparation is allowed only for fields with a real source editor: 35, 37–38, 40–42, 44–46, 49–53 (payment/transfer) and the employer-contact fields 25–29. Negative reports (type 13) have no automated field editing. Unsupported fields stay non-editable instead of opening an approximate form.

Field triage for outgoing types 12/13 (source: `EmployerInterface006XmlBuilder`):

| Fields | Ownership | Treatment |
|---|---|---|
| 1–24, 30–34, 36, 39, 43, 83, 85–95 | generated, configuration, master data or aggregates | fix the source/configuration or exporter; no field editor |
| 47–48, 54–56, 61–62, 69, 84 | emitted nil or derived from correction lineage | validate official obligation per operation before adding data |
| 25–29 | employer contact (`BuildRequester`; nil when sender code is 5) | employer form; check sender configuration first |
| 35, 37–38, 40–42, 44–46, 49–53 | payment/transfer | payment editor |
| 57–58 | report-event document | attachment flow |
| 59–60 | product type / old-fund subtype | product and 006 metadata editor |
| 63–68 | employee identity | employee form |
| 70–78 | deposit status and product values | split: 71, 74–77 deposit metadata; 70, 72, 73, 78 product editor (not the payment editor) |
| 79–82 | contribution lines | contribution editor |

Before production sign-off, an executable matrix over every supported `(interfaceType, fieldNumber)` and playbook code is still needed.

## Employer and product documents (006 attachments)

- `EmployerDocument` is an encrypted, reusable PDF library owned by the employer. Type **3** (employer affidavit) is employer-wide; types **4–6** require the owning `EmploymentId` and `EmployeePensionProductId`; type 5 applies to pension funds only and not to employee status 14.
- Endpoints: `/api/organizations/{org}/employers/{employer}/documents` (list, upload with type/signature/size/malware checks, download, delete, one-time `PATCH /{id}/product` assignment of legacy documents).
- A library document is **copied** into an editable current or negative report (`GET .../attachments/library-candidates`, `POST .../attachments/from-library`) only after server-side matching of employer, employee, fund/policy and report kind; the copy is re-encrypted under the report's own purpose. Difference reports carry no 006 attachments.
- The pension-mix save keeps matched product IDs stable and refuses to delete a product that still has documents.
- Negative operation 5 stores a nullable `CollectiveAgreementRefund` answer per report product (workflow metadata, not an official XML element; accepted and returned by the 006 product-metadata endpoint). XML preflight fails without an explicit answer, and a `true` answer requires a type-6 attachment on the same report product.

## Transmission routing and manufacturers

- Default delivery is the clearinghouse provider. `Reporting:ManufacturerRouting:Funds:<fundCode>=<provider>` overrides are explicit and reviewed; they are never derived automatically.
- `fund_code` alone does not identify a manufacturer: codes 101, 103 and 163 belong to more than one legal company. Routing must use legal company plus code.
- Production holds `reference_data.manufacturer_company_groups` and the view `reference_data.pension_products_by_manufacturer` (applied manually through Supabase on 2026-10-08; not created by this codebase). Unmapped companies have a null manufacturer and must not be routed automatically. The schema verifier reports these objects as production-only.

## Clearinghouse provider (simulated or real)

`EmployerInterface006:ClearinghouseProvider` is the only switch, read once at startup by `ClearinghouseProviderSelection`. The ASP.NET Core environment does not select it: `Production` can run the simulator. Exactly one delivery path is registered and nothing falls back to another; an invalid choice stops startup with `Invalid configuration: ...`.

| Value | Delivery path (provider names stored on each transmission) | Requires |
|---|---|---|
| `Simulated` (default in `appsettings.json`) | Internal TEST simulator: `SimulatedVault`, plus `SimulatedVault-<Key>` for each `SimulatedVault:Manufacturers:<Key>:Enabled` | `EnvironmentCode=1`, `SimulatedVault:RootDirectory` |
| `Real` | Real clearinghouse: **not implemented**, so startup stops | — |
| `Mock` | `MockClearinghouse`, accepts immediately (tests, local UI work) | Development, or `EmployerInterface006:AllowMockTransmission=true` |
| `Disabled` | `UnconfiguredClearinghouse`: every transmission is refused with 503 | — |

Startup also stops when the former switch `SimulatedVault:Enabled` contradicts the selection, or when manufacturer vaults are enabled without `Simulated`. The log line `Clearinghouse provider: <value> (<provider names>); Employer Interface 006 environment code <n>` records the active path. A client cannot pick a provider that is not registered (400), so a simulated process cannot dispatch elsewhere and a real one cannot reach the simulator.

**Simulated data stays identifiable.** Files carry environment code 1 (TEST) and live only under `RootDirectory` (`outbox`, `inbox`, `processing`, `processed`, `failed`, `responded`, `manufacturers/<Key>/...`). Transmissions are stored with provider `SimulatedVault...` and external IDs `SIMVAULT-...`, simulated feedback with message IDs `SIM-...`, and log messages say "Simulated clearing-house". Scenarios are chosen by `SimulatedVault:DefaultScenario` (or a `<payload>.scenario` file next to an outbox payload): `success`, `error[:code]`, `partial[:code]`, `in-transit`, `mixed`, `stress`, `all-errors`, `fedbka:accepted|duplicate|all-errors|<code>`, `fedbkb:accepted` (FEDBKB error codes are refused until their official codebook is in the repository).

**Filesystem.** The database holds all business state: the transmission, its encrypted payload and the ingested feedback. The simulator's files are only its work queue for the few seconds between "sent" and "feedback ingested" (`ResponseDelaySeconds` plus `PollIntervalSeconds`). On Render the container filesystem is lost on every restart and deploy; a transmission sent in that window keeps status `Accepted` but never receives simulated feedback. Each instance processes only its own files, so this holds with several instances too. Smallest safe options:

1. Accept it for demonstrations and find affected transmissions with the read-only query below.
2. Attach a Render persistent disk (paid instance; one instance; deploys stop the old instance first) and set `SimulatedVault:RootDirectory` to a path on it, for example `/var/data/simulated-clearinghouse-vault`. No code change.

```sql
-- Simulated transmissions accepted more than 10 minutes ago without any feedback (lost in-flight files).
SELECT t."Id", t."ReportId", t."Provider", t."CompletedAt"
FROM reporting.report_transmissions t
WHERE t."Provider" LIKE 'SimulatedVault%' AND t."Status" = 'Accepted' AND t."CompletedAt" < now() - interval '10 minutes'
  AND NOT EXISTS (SELECT 1 FROM reporting.employer_interface_feedback f WHERE f."TransmissionId" = t."Id");
```

**Switching to the real clearinghouse later** (code and configuration change, not configuration alone):

1. Implement the real provider behind `IReportTransmissionProvider` and register it for `Real` in `ReportingModule.AddTransmissionProviders`; replace the fail-fast branch in `ClearinghouseProviderSelection` with its own required settings (credentials, endpoint). Decide then whether `Real` may use the clearinghouse TEST environment (`EnvironmentCode=1`).
2. Close the open items in [feedback-routing-audit-2026-10-11.md](feedback-routing-audit-2026-10-11.md#status-review-2026-10-12-a12-release): the executable code-18 routing matrix and confirmation of the code-18 field-number convention on real feedback.
3. Configure the real sender and recipient identity under `EmployerInterface006:*` (the defaults are TEST placeholders) and remove any `Reporting:ManufacturerRouting` entries that name `SimulatedVault...` providers (they would be refused at send time).
4. Decide what happens to reports already "sent" to the simulator: they were never delivered. Count them first with `SELECT "Provider", "Status", count(*) FROM reporting.report_transmissions GROUP BY 1, 2`.
5. Set `EmployerInterface006__ClearinghouseProvider=Real` and confirm the startup log line.

### Manufacturer vaults

A manufacturer vault accepts only transmissions of its own provider and acknowledges only that transmission's routed products. Hybrid dispatch (`Reporting:ManufacturerRouting:EnableHybridDispatch`) is off by default and must not be enabled in production. Code-18 simulator error details use field 74 as a representative synthetic value.

Acceptance scenario for a multi-vault change: two products (one routed to a manufacturer vault), validate routing and both XSD packages, send once, confirm separate outboxes and payloads, confirm feedback correlates only to its own products, inject an error on one vault and repair it through a correction, and resume a rejected hybrid route without resending accepted destinations.

## Not verifiable in this repository

Open feedback-routing items (editor coverage per field and code, code-18 semantics, the executable routing matrix) are tracked in [feedback-routing-audit-2026-10-11.md](feedback-routing-audit-2026-10-11.md).

Real clearinghouse/manufacturer acceptance, provider timeouts after remote acceptance, real feedback timing and order, production sender/recipient identifiers and credentials, production-scale files, and the operational reconciliation of reports left in `Processing`. The system fails closed for all of them.

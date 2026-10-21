# Reporting and Employer Interface 006

Business rules of the Reporting module that are not obvious from the code. Authoritative sources for formats and codes: `docs/specifications/employer-interface/006` (XSDs, `Employer interface V 6.xlsx`, error-code workbook) and `docs/specifications/mislaka`. Never infer 006 rules from UI behavior. File locations are in [CODEMAP.md](CODEMAP.md#reporting).

## Completed reports are not correction sources (October 2026)

- A report with internal status `Completed` is final and cannot initiate a new correction workspace or derived corrective report via the standard report actions/API. The query capability flags must not offer correction for internal `Completed` **or** the user-visible feedback state `completed`; backend correction entry points reject internal `Completed` too. `Sent` reports remain eligible under their existing correction prerequisites.
- This distinction does not affect resumable drafts, which include unsent `Validated` reports, or any existing external feedback-resolution process.

## Unsent reports after final validation (October 2026)

- Final validation (`Validated`) is **not** completion or transmission: if pre-send evidence, payment or agreement checks fail before any transmission, the report remains a resumable, editable draft in the user experience.
- The open manual reports endpoint includes `Draft`, `ReadyForValidation`, `Validated` and `Error`. Editing a validated report calls `MarkDirty` and resets validation so it must pass final validation again before transmission.
- Normal draft deletion still requires no persisted transmissions, no official feedback, no derived versions and permission for the employer. A failed or attempted transmission with stored external evidence must never become deletable merely because the report status is `Error`.
- The frontend wizard loads this set for resume, and refreshes the list when the draft picker opens.

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

- **Type 3 (employer affidavit):** uploaded directly to each relevant negative report above its employee list. Every negative report with refund operation 5 requires its own document 3. Prior annual submissions do not satisfy the current report. It belongs to the report without a product ID, and cannot be newly uploaded into the employer profile or reused from the library.
- **Type 4 (employee refund approval):** uploaded per refunded product when operation 5 is not based on a collective agreement. Separate files are supported for each product; no implicit sharing across products.
- **Type 5 (joining request for a default pension fund):** available per eligible pension-fund product whose external key is in the employer's selected default-fund set and whose employee status is not 14. Mandatory when the matching employee-mix product was first created after the report opened; optional when it existed before the report opened. Stored joining requests may also live in the employee pension-mix library.
- **Type 6 (employer declaration for collective agreement):** uploaded per refunded product when operation 5 is based on a collective agreement.
- The employer-profile document upload API accepts only type 5 and the separate type 7 ALPHA employer agreement. Historical profile documents of other types remain readable, but type 3 cannot be attached again from the library.
- Report-scoped PDF copies remain encrypted and immutable after submission. Difference reports do not carry 006 attachments. The product metadata field CollectiveAgreementRefund selects the required document type (4 or 6). Report validation and XML preflight enforce per-product refund evidence and the current report's affidavit.
- The pension-mix save preserves matched product IDs and refuses deletion while saved documents refer to a product.
- Editing products in an ordinary manual report also reuses matching report-product IDs rather than deleting/recreating them; this preserves report evidence, operation metadata, and payment references. A product with attached 006 evidence must have that evidence removed explicitly before the product can be deleted.
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

## Employer legal agreement gate

Every transmission request, including resumed hybrid dispatch, requires an existing employer-wide document with DocumentTypeCode=7 and matching organization/employer. Missing agreement returns HTTP 409 `employer_legal_agreement_required`; draft editing and validation remain available. Uploaded PDFs are scanned and encrypted through the existing employer document flow. This feature intentionally does not infer that a user's click legally executes a signed contract: uploading a document records the employer's declared acceptance, with contract authenticity and authority subject to review. Removing the document blocks future sends, not previously sent reports.

## Employer default pension fund selection (October 2026)

The national selected-fund catalog flag is recorded on `reference_data.pension_products.is_system_default_fund`. An employer's explicit chosen set is stored separately under `employers.employer_default_funds`; no row means inherit the system defaults. GET/PUT/DELETE at `profile-center/default-funds` return, override, or restore those inherited values. The set is an employer preference and does not by itself establish employee enrollment or 006 attachment eligibility (including the regulator's ID-digit allocation rule).

### Code 5 document validation

The report attachment API exposes eligibleCode5ProductIds for all eligible fund products (optional upload) and requiredCode5ProductIds for eligible fund products newly added to ALPHA during this report (mandatory upload). The latter triggers DEFAULT_FUND_JOIN_DOCUMENT_REQUIRED in deposit/final validation. Both exclude employee status 14. Product creation time is determined from the saved mix relative to the report's CreatedAt; a new product saved during wizard editing remains new for that report. The employer's chosen-fund selection is an ALPHA operational criterion, not proof of actual legal enrollment.

## Flagged simulation reporting routes (2026-10-21)

`EmployerInterface006:SimulationTestMode=true` (ON in `appsettings.json`) enables two explicit tenant-scoped routes whenever `EnvironmentCode=1` and selected clearinghouse provider is `Simulated`, **independently of the ASP.NET Core Development/Production environment name**. When the flag is OFF, environment code is not TEST, or provider selection is not Simulated, the routes return 404.

- `POST /api/organizations/{organizationId}/employers/{employerId}/manual-reports/{reportId}/simulation/payload` returns base64 generated TEST XML and original validation findings for an authorized Draft. It does not transmit or change report state.
- `POST /api/organizations/{organizationId}/employers/{employerId}/manual-reports/{reportId}/simulation/transmissions` skips workbook and XSD validation, claims an authorized unsubmitted Draft atomically, persists encrypted transmission evidence, and dispatches only to `SimulatedVault`. It refuses reports routed to different manufacturer providers, and never blindly retries ambiguous outcomes.
- Normal `POST .../transmissions` still requires Validated reports and all official preflight/export checks. These simulation routes do not bypass XML construction failures, domain invariants, authorization, or tenant isolation.

**Data isolation caveat:** The deployed Production-hosted simulator and normal endpoints share the configured database. The simulation route persists report/transmission/feedback state in that database. Use only dedicated test employers and dummy financial identities. Turning the flag ON is not a substitute for physically separate test data. Simulator feedback defaults to scenario `success`; data-derived validation responses have not yet been implemented. Sidecar scenario files and deployment environment overrides can still force other feedback.

## Manufacturer inquiries from feedback external cases (October 2026)

After opening an external case, its product-level feedback problems may expose a prefilled manufacturer inquiry if an active error-code mail template and verified recipient exist. Product override wins over verified manufacturer default. Tenant-authorized send requires an explicit user action; recipient is always resolved again by the backend, never supplied by the browser. An absent OTP email provider configuration fails closed. Provider acceptance does not imply actual delivery or feedback resolution, and sending a query never changes the official deposit intake status. Individual attempts and failures are persisted in `reporting.feedback_manufacturer_email_dispatches`. File attachments and national identifiers are not automatically included. `EmployerInterface006:SimulationTestMode` remains configured ON in `src/Alpha.Api/appsettings.json`, unchanged by this feature.

- Starter templates for official split codes **2** and **3** are inserted if missing. They request confirmation of allocation and acceptance and do not claim successful intake; each may be edited or disabled in the platform-admin template editor. Other codes remain unavailable for email until a reviewed template is activated.

### Manufacturer mail safe test routing

`ManufacturerMail:TestMode` defaults to **true**, independently of `EmployerInterface006:SimulationTestMode`. When enabled, the server forcibly overrides the SMTP/Resend/Apps Script recipient to `alphapensia@gmail.com` and prepends `[ALPHA - סביבת בדיקות]` to the subject and a prominent test notice plus the intended manufacturer address to the body. The audit log stores the actual destination and test text. A successful test dispatch does not set the external case to waiting for a manufacturer response. To allow delivery to genuine manufacturers, an operator must deliberately set `ManufacturerMail__TestMode=false` on the deployed backend. Apps Script gateways must be redeployed with the `manufacturer-inquiry` type supported in `integrations/google-apps-script/Code.gs`; an older gateway fails closed and does not reroute to the actual manufacturer.

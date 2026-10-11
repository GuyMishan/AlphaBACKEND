# Employer Interface 006 — field and feedback routing audit (2026-10-11)

## Source of truth and scope
- Official field workbook: `docs/specifications/employer-interface/006/Employer interface V 6.xlsx`.
- Field lookup: `EmployerInterface006FieldMapInitializer.Find(interfaceType, fieldNumber)`.
- Error-code behavior: `FeedbackResolutionPlaybookCatalog`.
- Scope resolution: `ReportFeedbackEndpoints`, including `Code18TargetForField`.
- UI resolution: `AlphaFRONTEND/src/components/feedback-resolution-mode.tsx` and dedicated resolvers.

The workbook has **393 field records**: 95 each for outgoing interfaces 12 and 13, 72 each for incoming interfaces 14 and 18, and 59 for interface 16. Only the 95-field outgoing interfaces have editing-surface classifications. A classification is **not** a working field editor.

## Field ranges for each outgoing interface (12 and 13)
| Numbers | Backend edit surface | Actual frontend status |
| --- | --- | --- |
| 1–6 | fileHeader | No supported field-specific editor; reporting/system header |
| 7–24 | integrationIdentity | No supported field-specific editor; sender/recipient/integration |
| 25–29 | employerContact | Conditional employer/contact editor supports selected form fields by XML tag |
| 30–56 | reportTransfer | No complete 27-field editor; unsafe to open deposit-payment editor for all |
| 57–58 | reportEvent | No supported field-specific editor |
| 59–62, 69 | depositProduct | Product fields are **not** edited by DepositPaymentEditor; dedicated routing required |
| 63–68 | employeeProfile | Dedicated employee resolver; conditional-field routing repaired |
| 70–78 | depositEmployeeStatus | DepositPaymentEditor edits employee status/related 006 metadata, scoped to one report product; outgoing current reports only |
| 79–89 | depositContributions | DepositPaymentEditor does not edit contribution lines; dedicated contribution editor required |
| 90–95 | fileClosing | Computed/system closing values; no direct editor |

For interface 13 (negative reports), automated field editing remains intentionally disabled pending a verified correction path.

Incoming interfaces 14, 16 and 18 (203 field entries) currently map to `unknown` for editing; they are not routed as outgoing editable fields.

## Verified code routing architecture
101 entries were located in `FeedbackResolutionPlaybookCatalog`: 2 informational, 38 edit, 41 decision, and 20 external. These are **playbook decisions**, not 101 distinct field-specific editors.

- `employee`: `FeedbackEmployeeResolver` — shared employee form.
- `contribution`, `productPolicy`, `employmentStatus`, `payment`, `documents`, `reportCorrection`, `refund`: `FeedbackInternalCorrectionResolver` — displays contextual fields and prepares correction workspace, but does **not** provide a unique in-modal editor for every affected field.
- `split` and other decision-required codes: `FeedbackDecisionResolver`, actions depend on backend authorization.
- `externalCase` and escalation/reconciliation codes: `FeedbackExternalCaseResolver`, not a form edit.
- Code 18: dynamic field-specific routing; `errorAmount` is interpreted as a numbered field only for this code. Verify this convention using actual provider feedback before considering it production-proven.
- Code 56: employer payment settings only for verified employer-account mismatch; otherwise deposit-scoped payment/reconciliation.

## Concrete bugs found in this audit and corrected
1. Code-18 employee-profile fields were unreachable because `conditionalEmployeeProfile` depended on `depositConditionalEdit` and then required the surface to equal `employeeProfile`; corrected independent condition.
2. Code-18 fields in `depositProduct`, `depositContributions`, and `reportTransfer` incorrectly opened the embedded payment editor, although it does not edit those source fields. Backend preparation is now authorized only for `depositEmployeeStatus`, and frontend field routing matches that restriction.
3. The code-18 focused edit view now renders only the selected product's existing deposit-payment editor, rather than the deposit list (separate frontend change).

## Outstanding gaps / not yet verified
- There is **not** end-to-end proof that every one of the 190 outgoing workbook field entries opens the exact correct editor. Many entries intentionally have no direct UI editor or are derived/integration fields.
- Code-playbook coverage does **not** imply every actionable code has a complete inline field editor. Some code families prepare a workspace or require human/external decision. Every resolver type should receive representative end-to-end tests.
- Backend `Code18TargetForField` uses XML block ownership to distinguish employer/report/deposit; `EditSurface` uses numeric field ranges. Where these disagree, routing should fail closed rather than select an arbitrary edit form.
- Keep external code 18 `PERUT-SHGIHA-SHUM` field-number semantics unverified until a real manufacturer/clearinghouse sample confirms it.
- Before production sign-off, add a table-driven executable matrix for every supported `(interfaceType, fieldNumber)` and playbook `code`, testing ownership, authorization, edit-surface availability, correct entity targeting, validation, save, and correction-workspace immutability.

**Audit conclusion:** The routing catalog is comprehensive as classification, but universal correct-screen coverage is **not** currently true; unsupported cases must remain non-editable instead of opening an incorrect form.


## Source-aware triage: remove non-user-editable fields from the missing-editor backlog

Verified against `src/Alpha.Api/Services/EmployerInterface006XmlBuilder.cs`, in particular `BuildHeader`, `BuildRequester`, `BuildFund`, `BuildEmployee`, and the `ReshumatSgira` calculations. The following classification applies to the common numbered fields of outgoing types 12/13. Conditional fields differ by report kind; eligibility remains subject to official XSD and workbook.

### A. Exclude from the per-error manual editor backlog
These values belong to generation, system/integration configuration, existing master records or aggregation. Fix invalid source/config or exporter logic; do not offer an arbitrary XML-value text box.

| Field numbers | Source / reasoning |
| --- | --- |
| 1–6 | `SUG-MIMSHAK`, XML version, generated timestamp, environment configuration, file number and sequence |
| 7–19 | sender and recipient configuration; sender contact from resolved sender settings; recipient IDs/codes from integration options |
| 20–24 | requester/operator identity resolved from sender/employer configuration; direct-employer sender intentionally emits null requester identity |
| 30–34, 36 | fund identifier, depositor and employer identifier type/number and employer legal name from reference/master/settings; verify master records instead |
| 39 | total reported deposit by transfer group, derived |
| 43 | transfer identifier, generated/persisted report identity; must be corrected through lifecycle logic if wrong |
| 47–48 | `SUG-CHESHBON` emitted as nil; card-type field is not emitted by this builder. Verify XSD conditional requirements rather than adding editor |
| 61–62 | internal employer fund name/number emitted nil, no stored source currently; add data support only if required by a real obligation |
| 69 | employer-at-manufacturer identifier emitted nil in current builder |
| 70 | salary month copied from report product — edit at product/report source rather than adding a dedicated field-70 screen |
| 83 | stable current contribution record identifier, generated/persisted by correction lineage |
| 85–89 | employee/transfer contribution totals and member counts derived from emitted product/contribution rows |
| 90–95 | closing counts and totals computed on emission |

**Important:** 'exclude from direct-field editor' does not mean 'valid to emit null' or 'never user-changeable at the source'. If real feedback identifies a problem with source values (e.g. employer ID, salary month) route to existing source settings/master screen or exporter diagnostics, not a synthetic field editor.

### B. Keep as user-correctable source fields; connect to existing form, not an arbitrary field form
| Field numbers | Recommended source UI |
| --- | --- |
| 25–29 | employer/contact form (verify all five XML-tag bindings) |
| 35 | MASAV sender code inside payment editor, when payment method 7 applies |
| 37–38, 40–42 | operation, payment method, value dates, reference in payment editor |
| 44–46, 49–53 | employer/receiving bank accounts and account types, payment settings + scoped deposit payment editor |
| 57–58 | report-event document attachment and document type; attachment flow |
| 59–60 | pension product type/old fund pension subtype; pension product plus 006 metadata editor |
| 63–68 | employee identity/name/birthdate/internal employee number in employee form |
| 71–78 | deposit status, salary layer, reported salary, employee status, start date, work fraction, work days and policy number; **split routing**: 71/74–77 deposit metadata, 72/73/78 report product editor (do not claim payment form edits all nine) |
| 79–82 | contribution type/rate/amount/exempt payments — pension contribution editor |

### C. Validate conditional/document linkage before removing or adding direct editors
| Numbers | Reason |
| --- | --- |
| 54–56 | previous IDs/clearinghouse IDs currently emitted nil by this builder; conditional correction lineage may require values |
| 84 | previous contribution record identifier; source is correction lineage, not an arbitrary text value |
| 47–48, 61–62, 69 | optional/nil/unemitted only as currently implemented; validate official obligation per operation and provider feedback |

**Audit finding requiring additional fix:** `EditSurface` currently labels 70–78 as `depositEmployeeStatus`; however 70 comes from product salary month, 72 from salary layer, 73 from salary, and 78 from product policy number. The payment editor does not own all these source fields. Its current generic classification is too broad. Correct field-specific routing is needed for 70/72/73/78 before allowing automatic code-18 correction.

This triage removes derived/integration/system fields from the **manual-edit-screen** backlog, not from validation or troubleshooting responsibilities.

### Verification of fields 25–29 (employer contact)
All five official requester-contact fields are already present in `AlphaFRONTEND/src/components/employer-form.tsx` and mapped by `feedback-code18-employer-editor.tsx`: field 25 => `contactFirstName`, 26 => `contactLastName`, 27 => `contactPhone`, 28 => `contactEmail`, 29 => `contactMobile`. `EmployerInterface006XmlBuilder.BuildRequester` maps these to the corresponding XML tags when sender code is not 5. **Remove 25–29 from the missing-editor backlog**; their actual editor already exists.

Remaining validation/integration caveats: frontend contact names currently allow 1 character while the 006 builder requires 2–20; phone/mobile validation differs as well. When `SenderCode == 5`, the requester block deliberately emits these five fields as nil, so missing-field feedback must first check sender configuration and official conditional requirements instead of blindly editing the employer profile.

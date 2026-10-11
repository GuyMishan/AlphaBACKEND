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
101 entries were located in `FeedbackResolutionPlaybookCatalog`: 2 informational, 40 edit, 39 decision, and 20 external. These are **playbook decisions**, not 101 distinct field-specific editors.

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

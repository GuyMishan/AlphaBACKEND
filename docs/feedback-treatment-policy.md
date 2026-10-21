# V006 feedback: four user-facing treatment choices (stage 1)

The authoritative meanings of feedback codes remain the V006 workbook/XSD and clearinghouse specifications in `docs/specifications`. `FeedbackResolutionPlaybookCatalog` retains detailed resolver behavior. `FeedbackTreatmentPolicyCatalog` adds a **complete** user-facing classification; it does not replace the existing resolver or enable e-mail sending.

| Choice | Count | Codes |
| --- | ---: | --- |
| `CorrectionOnly` | 41 | 6, 7, 16, 17, 18, 19, 20, 21, 23, 27, 28, 32, 33, 43, 44, 51, 53, 54, 59, 62, 71, 72, 73, 74, 75, 81, 82, 86, 88, 93, 96, 99, 100, 101, 104, 105, 106, 107, 108, 113, 114 |
| `CorrectionOrManufacturer` | 41 | 2, 3, 4, 5, 11, 15, 24, 25, 26, 42, 45, 47, 49, 50, 55, 56, 57, 58, 61, 63, 64, 67, 68, 69, 70, 78, 79, 80, 83, 84, 85, 97, 98, 102, 103, 109, 110, 111, 112, 115, 116 |
| `ManufacturerOnly` | 17 | 13, 29, 30, 34, 39, 46, 48, 52, 66, 77, 87, 89, 90, 91, 92, 94, 95 |
| `InformationOnly` | 2 | 1, 31 |

## Rules

- **CorrectionOnly:** show a meaningful internal correction route; the target is the playbook resolver (employee/employment, contribution, payment, document, report, refund, etc.), not a screen selected by hard-coded error-code branches.
- **CorrectionOrManufacturer:** both internal correction and external case are allowed *in principle*. This enum is named `CorrectionOrExternalInquiry` in code because a clearinghouse or other institution may be the actual responsible party.
- **ManufacturerOnly:** external inquiry/case only (`ExternalInquiryOnly` in code). Resolve the verified receiving institution/product, not simply the report employer.
- **InformationOnly:** explain, no action or fake confirmation.
- **Decision** in the legacy playbook is a *preflight review requirement*, not a fifth treatment type, and must never close feedback through a no-op confirmation.
- Availability of a treatment **is not permission to send**: enforce tenant authorization, verified recipient, product association, and case-level controls at request time. Retain test-mail override.
- Saving internal corrections or sending a message does not close official feedback. Closure requires relevant confirmation / follow-up feedback.
- The mapping captures the existing playbook's supported correction/external-case capabilities. It is intentionally separate from UI rollout and inbound-mail/AI work.

## Implemented integration

- `FeedbackResolutionProblemDto` now includes `treatmentChoice`, `treatmentCanCorrect` and `treatmentCanInquire`; the reporting resolution context populates them from the catalog.
- The frontend chooses correction, inquiry or information only from the backend policy. Shared existing editor forms are embedded where supported; transmitted reports are edited through correction workspaces, not rewritten.
- Manufacturer inquiry sending derives the subject and protected body from the enabled DB template and verified product/institution recipient. The client submits only `additionalInformation`, appended after the server-rendered template. Test routing to the ALPHA mailbox remains enforced server-side.
- Outbound dispatch records expose a chronological case conversation history; inbound e-mail capture and AI replies are deliberately outside this iteration.
- Coverage tests assert every official playbook code is classified exactly once. Dedicated split tests exercise real edit-product correction preparation.

## Remaining validation and constraints

The current embedded editor supports existing shared employer-contact and deposit/product forms, plus existing employee resolvers. Some report-wide, document, and payment flows still rely on existing specialized workflows rather than an embedded editor. They must not be advertised as fully embedded until those reusable screens are supported. No inbound-mail listener or AI triage has been enabled.

## Verification

`FeedbackTreatmentPolicyCatalogTests` checks full official-code coverage, uniqueness, key cases and external-case action consistency. Run the verification commands in `AGENTS.md` before deploying.

## Database source of truth (October 2026)

`reference_data.feedback_treatment_policies` now stores the four treatment modes per official error code, together with correction target, JSON correction-field identifiers, and external recipient type. Startup seeds every catalog code using `ON CONFLICT DO NOTHING`, preserving DB overrides. Resolution-context API reads this table; unknown or unconfigured codes fail closed. Playbook classifications remain internal defaults for seeding, not user-facing mode authority. Configuration changes must retain authorization and action checks; `correction_target` identifies an existing editor, not executable code.

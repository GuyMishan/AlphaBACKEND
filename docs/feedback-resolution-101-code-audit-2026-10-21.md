# V006 feedback treatment — 101-code source audit (2026-10-21)

## Scope and status

This is an **exhaustive static cross-check** of every code in `FeedbackResolutionPlaybookCatalog`, `FeedbackTreatmentPolicyCatalog`, the persisted database seed `EmployerInterface006ErrorCodeInitializer`, and the frontend correction destinations. It is **not** a 101-scenario browser test or verification of deployment-specific DB overrides. Producer acknowledgement and round-trip feedback have not been simulated for every code.

- Playbook codes: **101**, distinct: **101**.
- Persisted seed rows: **101**, all mapped: **yes**.
- Cross-check violations: **0** (none).
- Modes: informationOnly: 2; correctionOrExternalInquiry: 41; correctionOnly: 41; externalInquiryOnly: 17.
- Resolver coverage: Information: 2; Split: 4; Employee: 5; ProductPolicy: 11; Contribution: 25; ExternalCase: 20; ConditionalField: 1; EmploymentStatus: 6; ReportCorrection: 8; Payment: 5; Refund: 4; Documents: 10.
- Eight known affidavit errors have a specific, unambiguous document focus in the DB seed: document 3 (codes 69,70,111,112), document 4 (109,110), and document 6 (102,103). Codes 55 and 67 mention unspecified supporting documents, so inventing a file type would be unsafe; use the existing report editor and request context.

## Code-by-code audit matrix

| Error code | Seeded DB mode | Existing resolver | DB correction target | Document type | Supported route / caveat |
| ---: | --- | --- | --- | ---: | --- |
| 1 | informationOnly | Information | none | — | Information |
| 2 | correctionOrExternalInquiry | Split | split | — | Existing deposit editor if target supports it |
| 3 | correctionOrExternalInquiry | Split | split | — | Existing deposit editor if target supports it |
| 4 | correctionOrExternalInquiry | Employee | employee | — | Existing employee editor |
| 5 | correctionOrExternalInquiry | ProductPolicy | productPolicy | — | Existing deposit editor if target supports it |
| 6 | correctionOnly | Contribution | contribution | — | Existing deposit editor if target supports it |
| 7 | correctionOnly | Contribution | contribution | — | Existing deposit editor if target supports it |
| 11 | correctionOrExternalInquiry | Employee | employee | — | Existing employee editor |
| 13 | externalInquiryOnly | ExternalCase | none | — | External case |
| 15 | correctionOrExternalInquiry | ProductPolicy | productPolicy | — | Existing deposit editor if target supports it |
| 16 | correctionOnly | Contribution | contribution | — | Existing deposit editor if target supports it |
| 17 | correctionOnly | Contribution | contribution | — | Existing deposit editor if target supports it |
| 18 | correctionOnly | ConditionalField | conditionalField | — | Mapped field editor when supported |
| 19 | correctionOnly | EmploymentStatus | employmentStatus | — | Existing deposit editor if target supports it |
| 20 | correctionOnly | Contribution | contribution | — | Existing deposit editor if target supports it |
| 21 | correctionOnly | Contribution | contribution | — | Existing deposit editor if target supports it |
| 23 | correctionOnly | Contribution | contribution | — | Existing deposit editor if target supports it |
| 24 | correctionOrExternalInquiry | ProductPolicy | productPolicy | — | Existing deposit editor if target supports it |
| 25 | correctionOrExternalInquiry | ProductPolicy | productPolicy | — | Existing deposit editor if target supports it |
| 26 | correctionOrExternalInquiry | ProductPolicy | productPolicy | — | Existing deposit editor if target supports it |
| 27 | correctionOnly | ReportCorrection | reportCorrection | — | Existing report wizard |
| 28 | correctionOnly | ReportCorrection | reportCorrection | — | Existing report wizard |
| 29 | externalInquiryOnly | ExternalCase | none | — | External case |
| 30 | externalInquiryOnly | ExternalCase | none | — | External case |
| 31 | informationOnly | Information | none | — | Information |
| 32 | correctionOnly | ReportCorrection | reportCorrection | — | Existing report wizard |
| 33 | correctionOnly | EmploymentStatus | employmentStatus | — | Existing deposit editor if target supports it |
| 34 | externalInquiryOnly | EmploymentStatus | none | — | Existing deposit editor if target supports it |
| 39 | externalInquiryOnly | ExternalCase | none | — | External case |
| 42 | correctionOrExternalInquiry | ProductPolicy | productPolicy | — | Existing deposit editor if target supports it |
| 43 | correctionOnly | ReportCorrection | reportCorrection | — | Existing report wizard |
| 44 | correctionOnly | EmploymentStatus | employmentStatus | — | Existing deposit editor if target supports it |
| 45 | correctionOrExternalInquiry | Payment | payment | — | Existing deposit editor if target supports it |
| 46 | externalInquiryOnly | ExternalCase | none | — | External case |
| 47 | correctionOrExternalInquiry | EmploymentStatus | employmentStatus | — | Existing deposit editor if target supports it |
| 48 | externalInquiryOnly | EmploymentStatus | none | — | Existing deposit editor if target supports it |
| 49 | correctionOrExternalInquiry | ProductPolicy | productPolicy | — | Existing deposit editor if target supports it |
| 50 | correctionOrExternalInquiry | Payment | payment | — | Existing deposit editor if target supports it |
| 51 | correctionOnly | Refund | refund | — | Existing refund workflow (needs E2E) |
| 52 | externalInquiryOnly | ExternalCase | none | — | External case |
| 53 | correctionOnly | Contribution | contribution | — | Existing deposit editor if target supports it |
| 54 | correctionOnly | ProductPolicy | productPolicy | — | Existing deposit editor if target supports it |
| 55 | correctionOrExternalInquiry | Documents | documents | — | Document type not specified (manual selection) |
| 56 | correctionOrExternalInquiry | Payment | payment | — | Existing deposit editor if target supports it |
| 57 | correctionOrExternalInquiry | Split | split | — | Existing deposit editor if target supports it |
| 58 | correctionOrExternalInquiry | ExternalCase | externalCase | — | External case |
| 59 | correctionOnly | Contribution | contribution | — | Existing deposit editor if target supports it |
| 61 | correctionOrExternalInquiry | ProductPolicy | productPolicy | — | Existing deposit editor if target supports it |
| 62 | correctionOnly | Employee | employee | — | Existing employee editor |
| 63 | correctionOrExternalInquiry | Contribution | contribution | — | Existing deposit editor if target supports it |
| 64 | correctionOrExternalInquiry | Contribution | contribution | — | Existing deposit editor if target supports it |
| 66 | externalInquiryOnly | ExternalCase | none | — | External case |
| 67 | correctionOrExternalInquiry | Documents | documents | — | Document type not specified (manual selection) |
| 68 | correctionOrExternalInquiry | ProductPolicy | productPolicy | — | Existing deposit editor if target supports it |
| 69 | correctionOrExternalInquiry | Documents | documents | 3 | Document 3 (guided upload) |
| 70 | correctionOrExternalInquiry | Documents | documents | 3 | Document 3 (guided upload) |
| 71 | correctionOnly | Contribution | contribution | — | Existing deposit editor if target supports it |
| 72 | correctionOnly | Contribution | contribution | — | Existing deposit editor if target supports it |
| 73 | correctionOnly | Contribution | contribution | — | Existing deposit editor if target supports it |
| 74 | correctionOnly | Contribution | contribution | — | Existing deposit editor if target supports it |
| 75 | correctionOnly | Contribution | contribution | — | Existing deposit editor if target supports it |
| 77 | externalInquiryOnly | ExternalCase | none | — | External case |
| 78 | correctionOrExternalInquiry | Employee | employee | — | Existing employee editor |
| 79 | correctionOrExternalInquiry | Payment | payment | — | Existing deposit editor if target supports it |
| 80 | correctionOrExternalInquiry | ExternalCase | externalCase | — | External case |
| 81 | correctionOnly | Contribution | contribution | — | Existing deposit editor if target supports it |
| 82 | correctionOnly | Contribution | contribution | — | Existing deposit editor if target supports it |
| 83 | correctionOrExternalInquiry | ExternalCase | externalCase | — | External case |
| 84 | correctionOrExternalInquiry | ExternalCase | externalCase | — | External case |
| 85 | correctionOrExternalInquiry | ExternalCase | externalCase | — | External case |
| 86 | correctionOnly | Employee | employee | — | Existing employee editor |
| 87 | externalInquiryOnly | ExternalCase | none | — | External case |
| 88 | correctionOnly | Refund | refund | — | Existing refund workflow (needs E2E) |
| 89 | externalInquiryOnly | ExternalCase | none | — | External case |
| 90 | externalInquiryOnly | ExternalCase | none | — | External case |
| 91 | externalInquiryOnly | ExternalCase | none | — | External case |
| 92 | externalInquiryOnly | ExternalCase | none | — | External case |
| 93 | correctionOnly | Refund | refund | — | Existing refund workflow (needs E2E) |
| 94 | externalInquiryOnly | ExternalCase | none | — | External case |
| 95 | externalInquiryOnly | ExternalCase | none | — | External case |
| 96 | correctionOnly | Refund | refund | — | Existing refund workflow (needs E2E) |
| 97 | correctionOrExternalInquiry | ReportCorrection | reportCorrection | — | Existing report wizard |
| 98 | correctionOrExternalInquiry | ReportCorrection | reportCorrection | — | Existing report wizard |
| 99 | correctionOnly | Payment | payment | — | Existing deposit editor if target supports it |
| 100 | correctionOnly | ReportCorrection | reportCorrection | — | Existing report wizard |
| 101 | correctionOnly | ReportCorrection | reportCorrection | — | Existing report wizard |
| 102 | correctionOrExternalInquiry | Documents | documents | 6 | Document 6 (guided upload) |
| 103 | correctionOrExternalInquiry | Documents | documents | 6 | Document 6 (guided upload) |
| 104 | correctionOnly | Contribution | contribution | — | Existing deposit editor if target supports it |
| 105 | correctionOnly | Contribution | contribution | — | Existing deposit editor if target supports it |
| 106 | correctionOnly | Contribution | contribution | — | Existing deposit editor if target supports it |
| 107 | correctionOnly | Contribution | contribution | — | Existing deposit editor if target supports it |
| 108 | correctionOnly | Contribution | contribution | — | Existing deposit editor if target supports it |
| 109 | correctionOrExternalInquiry | Documents | documents | 4 | Document 4 (guided upload) |
| 110 | correctionOrExternalInquiry | Documents | documents | 4 | Document 4 (guided upload) |
| 111 | correctionOrExternalInquiry | Documents | documents | 3 | Document 3 (guided upload) |
| 112 | correctionOrExternalInquiry | Documents | documents | 3 | Document 3 (guided upload) |
| 113 | correctionOnly | Contribution | contribution | — | Existing deposit editor if target supports it |
| 114 | correctionOnly | Contribution | contribution | — | Existing deposit editor if target supports it |
| 115 | correctionOrExternalInquiry | ProductPolicy | productPolicy | — | Existing deposit editor if target supports it |
| 116 | correctionOrExternalInquiry | Split | split | — | Existing deposit editor if target supports it |

## Material findings and limits

1. **Policy ownership:** All four modes originate in persisted DB rows; static catalog is only seed. Runtime endpoints verify permissions, allowed actions and tenant context; no external send is permissioned solely by UI state.
2. **Document autofocusing:** A document type in `correction_fields` selects an existing upload control; nothing is auto-uploaded. For conditional refund documents, the user must explicitly select the collective-agreement basis before the correct control can appear. If absent or already attached, focus the relevant container/record. Document 3 lives in the negative-report employee stage; documents 4/6 in the product/deposit editor.
3. **No invented file target:** Codes **55, 67** are generic missing-document/policy-change errors, not a single guaranteed document type; they have no forced focus mapping. Other codes without a `documentTypeCode` are not document-focus candidates. The UI should not guess a type.
4. **Workspace safety:** Attached documents must live on the correct report and product, not only employer document library. Corrective copy preserves sent source. Embedded wizard disables transmission, leaving verification and dispatch to existing reporting workflow. Negative-report documents require exact source identifiers.
5. **Unverified behaviors:** Internal correction editor selection is conditional on target scope and source product; not all 101 errors have an applicable editable field. External recipients and manufacturer mail template completeness must be audited against **live DB state**, not merely the seed. Refund, split and report-wide corrections must be E2E tested using real data. Feedback remains open until appropriate follow-up feedback.
6. **Classification is not acceptance:** This matrix confirms 101/101 configured code entries, not successful end-to-end correction for 101/101 or sending 101/101 mail templates.

## Next verification gates

- Run frontend `npm run verify` and backend `dotnet test --solution AlphaBackend.slnx --configuration Release` using CI on the latest HEAD.
- Run browser E2E for every supported resolver and document type (3,4,5,6), including existing file, conditional branch, invalid file, mobile RTL and Dark/Light.
- Against the real database, compare its 101 treatment rows to this seeded reference and audit manufacturer contact/template coverage; do not overwrite reviewed DB overrides during that check.
- Simulate send/feedback cycles including rejected and accepted manufacturer responses before claiming 101 resolved scenarios.

## Repeat audit: exhaustive persisted-policy integration test

The repeat audit extends `SchemaVerifierTests.Persisted_feedback_treatments_cover_all_101_codes_with_document_focus` so it loops over **every** official policy, checking its actual fresh-bootstrap DB `treatment_choice`, `correction_target`, `external_recipient_type` and valid JSON correction fields against the playbook. This is a stronger check than the old count-only test. It also verifies DB overrides remain unchanged after re-running the initializer.

The shared frontend fallback correction resolver no longer uses branches for individual document/report error numbers; document type comes from `correctionFields` and the appropriate domain report editor still owns upload semantics. Mixed-mode feedback groups now withhold any group-wide action unless every problem permits it, avoiding an unauthorized/incorrect cross-code action. A UI for **separately** handling mixed-permission groups is still a usability gap.

**Honest completeness limit:** Static/bootstrapped DB coverage does not guarantee that all 101 producer issues have a successful actionable correction in every concrete report. The following remain outside this audit: full browser automation over 101 real feedbacks; production DB override reconciliation; actual send → manufacturer receipt → follow-up feedback; resolving incomplete product/report references; institution-specific missing document types 55 and 67; and verifying exact uploaded evidence bytes on provider side. This report cannot be used to claim these checks passed.

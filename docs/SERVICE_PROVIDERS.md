# ALPHA — external service providers

This is the operational provider register. **Configured/integrated** describes code or infrastructure already present; it does not by itself certify a working production account, completed onboarding or contractual approval. Review this list when adding or replacing a provider.

| Provider | Purpose | Current ALPHA status | Ownership / next step |
| --- | --- | --- | --- |
| Supabase | Managed PostgreSQL and private Storage for pension-payment confirmations | In use; private `alpha-payment-evidence` bucket created | Keep service-role credentials server-only; verify authorized end-to-end evidence upload/download. |
| Render | Host AlphaBACKEND / .NET API | In use; backend service connected to main | Keep server secrets in Render Environment; verify deployment and logs. |
| Vercel | Host AlphaFRONTEND and AlphaMarketing | In use | Verify deployments and production environment settings per repository. |
| Google Gmail + Apps Script | Deliver email OTP messages | Adapter exists in `integrations/google-apps-script/Code.gs` and backend; activation depends on configured deployment/secrets | Monitor sending quotas; do not expose shared secret. |
| CardCom | ALPHA billing/payment processing | Adapter exists; default provider in committed configuration | Validate actual merchant account, terms and production billing before live charges. |
| PayPlus | Alternative ALPHA billing/payment processing | Adapter exists; commercial onboarding/production selection not established by code alone | Confirm recurring card and bank debit terms and live credentials if selected. |
| Pension clearinghouse (המסלקה הפנסיונית) | Employer Interface 006 submissions and feedback | Official formats/validation implemented; transport remains mocked in the backend | Agree production connection and verify transmission with actual permitted reports. |
| **Cloudmersive** | **Scan uploaded payment evidence for malware before private Storage upload** | **Selected provider; integration pending.** Current scanner accepts raw binary and a literal `clean` response; Cloudmersive's actual API requires a dedicated adapter and error mapping. | Open provider account, confirm commercial/free-plan eligibility, privacy/data processing and file limits; obtain server API key; implement adapter and fail-closed tests before enabling uploads. |

## Sensitive-data supplier checklist

For providers handling financial or identity-related content, verify data location, retention, deletion, subprocessors, access controls, incident notifications, agreement requirements and failure-mode behavior. Never send real pension documents to trial services until the data-processing arrangement and trial conditions are acceptable. Do not route files to consumer malware-sharing services that redistribute uploaded samples.

## Payment evidence specifics

The existing `ConfiguredMalwareScanner` refuses uploads when it is not configured. Setting `Security__MalwareScanner__Endpoint` to a Cloudmersive URL **alone does not complete integration** because the current wire contract expects a plain `clean` result. The secure `Storage__ServiceRoleKey` and Cloudmersive credentials must be kept exclusively in Render. See `README.md` for storage and scanner configuration prerequisites.

# Employer Interface Version 006 schemas

Runtime validation is fail-closed and requires the four official Version 006 XSD files in this directory.

Canonical repository filenames:

- `employer-current-006.xsd` — employer current/regular report (EMPONG)
- `employer-negative-006.xsd` — employer negative report (EMPNEG)
- `employer-summary-feedback-006.xsd` — summary feedback (EMPFED)
- `employer-annual-summary-feedback-006.xsd` — annual summary feedback (EMPYRL)

`Alpha.Api.csproj` copies `*.xsd` from this directory to `Specifications/EmployerInterface/006/` for build and publish output. The runtime registry also recognizes the common official/legacy filenames listed in `EmployerInterfaceSchemaRegistry` so an official file can be stored without renaming.

Do not substitute an older schema and change its version enumeration. Version 006 validation must use the regulator-published Version 006 XSD bytes.

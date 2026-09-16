# Employer Interface 006

This folder contains the official Employer Interface Version 006 XSD schemas used by AlphaBACKEND.

## Official schemas

- `mimshak_maasikim_shotef_xsd_schema_006.xsd.xml` — current / regular employer report
- `mimshak_maasikim_shliliim_xsd_schema_006.xsd.xml` — negative employer report
- `mimshak_maasikim_mesakem_xsd_schema_006.xsd.xml` — summary feedback
- `mimshak_maasikim_mesakem_shnati_xsd_schema_006.xsd.xml` — annual summary feedback

## Usage

These files are the source of truth for Employer Interface 006 validation and XML generation.

The backend must validate incoming and outgoing Employer Interface XML against the matching schema before accepting, importing, or transmitting it.

Do not replace these schemas with files from an older interface version and do not modify the official XSD contents to make generated XML pass validation. The application code must conform to the schemas.

## Document routing

The backend supports four document types:

1. Current employer report
2. Negative employer report
3. Summary feedback
4. Annual summary feedback

Incoming XML is detected by validation against the official schemas and then routed to the appropriate import/feedback flow. Outgoing employer reports are generated according to their report type and must pass the corresponding Version 006 XSD before transmission.

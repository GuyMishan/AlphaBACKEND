# Manufacturer grouping of pension fund catalog

The authoritative current database mapping is in Supabase ALPHADB:
- `reference_data.manufacturer_company_groups`: one row per **legal company name**, with `manufacturer_name` (parent brand), `mapping_status`, `mapping_basis`, and `updated_at`.
- `reference_data.pension_products_by_manufacturer`: read-only view joining the group to every row of `reference_data.pension_products`, preserving `fund_code`, `fund_name`, `company_name`, `company_legal_id`, `product_type`, `domain`, `investment_track_code`, `source`, and `is_active`.

The migration `group_pension_funds_by_manufacturer` was applied via Supabase migrations on 2026-10-08. It creates the grouping table, seeds it from the full existing company-name catalog and creates the view. New company names from future external catalog syncs must be reviewed and added to the mapping table; rows without a mapping appear in the view with null manufacturer and are NOT safe to route automatically.

The snapshot reviewed consisted of 2,172 reference product rows, 1,182 distinct fund codes, 72 companies and 58 top-level manufacturer groups. All 72 current company names received mappings. Three name-based historical associations have `historical_review` status; the independent companies remain independent rather than guessing ultimate ownership.

**Critical constraint:** `fund_code` alone is NOT globally sufficient to identify a manufacturer. The current catalog contains multiple legal companies per code: `101` (Harel and Menora), `103` (Phoenix and Meitav), and `163` (Harel and Meitav). The view flags `code_shared_between_companies`. Never derive a routing override from this grouping without the legal company, code, effective product details, and explicit operational review.

The current transmission routing override `Reporting:ManufacturerRouting:Funds:<code>` is **not automatically derived** from this reference mapping. The mapping is intended to evolve into an authoritative parent manufacturer registry, but operational direct-vault eligibility must be separate from brand grouping and should consider historical funds.

Example queries:

```sql
SELECT manufacturer_name, COUNT(DISTINCT company_name) legal_companies,
       COUNT(DISTINCT fund_code) fund_codes
FROM reference_data.pension_products_by_manufacturer
GROUP BY manufacturer_name
ORDER BY fund_codes DESC;

SELECT manufacturer_name, company_name, fund_code, fund_name, product_type,
       mapping_status, code_shared_between_companies
FROM reference_data.pension_products_by_manufacturer
WHERE manufacturer_name = 'מנורה'
ORDER BY company_name, fund_code, fund_name;
```

using System.Data.Common;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Alpha.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Alpha.Api.Services;

public sealed record ReferenceDataSyncResult(
    Guid RunId,
    string IntegrationKey,
    string IntegrationName,
    string Status,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    int RecordsReceived,
    int RecordsInserted,
    int RecordsUpdated,
    int RecordsDeactivated,
    string? ErrorMessage,
    object Details);

public sealed class ReferenceDataSyncService(HttpClient httpClient, AlphaDbContext db)
{
    private const string BankBranchesResource = "2202bada-4baf-45f5-aa61-8c5bad9646d3";
    private const string TransferFundsResource = "b5223cbc-e1b2-4503-a499-97cdcd7190d2";
    private const string GemelNetResource = "a30dcbea-a1d2-482c-ae29-8f781f5025fb";
    private const string InsuranceNetResource = "c6c62cc7-fe02-4b18-8f3e-813abfbb4647";
    private const string IsraelStreetsResource = "bf185c7f-1a4e-4662-88c5-fa118a244bda";
    private const string AddressSource = "population_authority_streets";

    public static readonly IReadOnlyDictionary<string, string> Integrations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["banks-branches"] = "בנקים וסניפים",
        ["pension-products"] = "קופות ומוצרים פנסיוניים",
        ["address-data"] = "יישובים ורחובות בישראל"
    };

    public async Task<ReferenceDataSyncResult> RunAsync(string key, Guid triggeredBy, CancellationToken ct)
    {
        if (!Integrations.TryGetValue(key, out var name))
            throw new ArgumentException("Unknown integration.", nameof(key));

        var runId = Guid.NewGuid();
        var started = DateTimeOffset.UtcNow;
        await InsertRunAsync(runId, key, name, triggeredBy, started, ct);

        try
        {
            var stats = key switch
            {
                "banks-branches" => await SyncBanksAndBranchesAsync(ct),
                "pension-products" => await SyncPensionProductsAsync(ct),
                "address-data" => await SyncAddressDataAsync(ct),
                _ => throw new ArgumentOutOfRangeException(nameof(key))
            };

            var finished = DateTimeOffset.UtcNow;
            await CompleteRunAsync(runId, "Success", finished, stats, null, ct);
            return new ReferenceDataSyncResult(runId, key, name, "Success", started, finished,
                stats.Received, stats.Inserted, stats.Updated, stats.Deactivated, null, stats.Details);
        }
        catch (Exception ex)
        {
            var finished = DateTimeOffset.UtcNow;
            var empty = new SyncStats(0, 0, 0, 0, new { });
            await CompleteRunAsync(runId, "Failed", finished, empty, ex.Message, ct);
            return new ReferenceDataSyncResult(runId, key, name, "Failed", started, finished, 0, 0, 0, 0, ex.Message, new { });
        }
    }

    private async Task<SyncStats> SyncBanksAndBranchesAsync(CancellationToken ct)
    {
        var records = await FetchAllAsync(BankBranchesResource, ct);
        var now = DateTimeOffset.UtcNow;
        var bankSeen = new HashSet<int>();
        var branchSeen = new HashSet<string>(StringComparer.Ordinal);
        var inserted = 0;
        var updated = 0;

        foreach (var row in records)
        {
            var bankCode = Int(row, "Bank_Code");
            var branchCode = Int(row, "Branch_Code");
            var bankName = Text(row, "Bank_Name");
            var branchName = Text(row, "Branch_Name");
            if (bankCode is null || branchCode is null || string.IsNullOrWhiteSpace(bankName) || string.IsNullOrWhiteSpace(branchName)) continue;

            if (bankSeen.Add(bankCode.Value))
            {
                var existed = await ExistsAsync("SELECT 1 FROM reference_data.banks WHERE bank_code = @p0", new object?[] { bankCode.Value }, ct);
                await ExecuteAsync("""
                    INSERT INTO reference_data.banks (bank_code, bank_name, is_active, source, last_seen_at, updated_at)
                    VALUES (@p0, @p1, true, 'bank_of_israel', @p2, @p2)
                    ON CONFLICT (bank_code) DO UPDATE SET
                        bank_name = EXCLUDED.bank_name,
                        is_active = true,
                        last_seen_at = EXCLUDED.last_seen_at,
                        updated_at = CASE WHEN reference_data.banks.bank_name IS DISTINCT FROM EXCLUDED.bank_name OR NOT reference_data.banks.is_active THEN EXCLUDED.updated_at ELSE reference_data.banks.updated_at END
                    """, new object?[] { bankCode.Value, bankName, now }, ct);
                if (existed) updated++; else inserted++;
            }

            var branchKey = $"{bankCode}:{branchCode}";
            branchSeen.Add(branchKey);
            var branchExisted = await ExistsAsync("SELECT 1 FROM reference_data.bank_branches WHERE bank_code = @p0 AND branch_code = @p1", new object?[] { bankCode.Value, branchCode.Value }, ct);
            await ExecuteAsync("""
                INSERT INTO reference_data.bank_branches
                    (bank_code, branch_code, branch_name, branch_address, city, zip_code, telephone, branch_type, open_date, close_date, is_active, source, last_seen_at, updated_at)
                VALUES (@p0,@p1,@p2,@p3,@p4,@p5,@p6,@p7,@p8,@p9,@p10,'bank_of_israel',@p11,@p11)
                ON CONFLICT (bank_code, branch_code) DO UPDATE SET
                    branch_name = EXCLUDED.branch_name,
                    branch_address = EXCLUDED.branch_address,
                    city = EXCLUDED.city,
                    zip_code = EXCLUDED.zip_code,
                    telephone = EXCLUDED.telephone,
                    branch_type = EXCLUDED.branch_type,
                    open_date = EXCLUDED.open_date,
                    close_date = EXCLUDED.close_date,
                    is_active = EXCLUDED.is_active,
                    last_seen_at = EXCLUDED.last_seen_at,
                    updated_at = EXCLUDED.updated_at
                """,
                new object?[] {
                    bankCode.Value, branchCode.Value, branchName, Text(row,"Branch_Address"), Text(row,"City"), Text(row,"Zip_Code"),
                    Text(row,"Telephone"), Text(row,"Branch_Type"), Text(row,"Open_Date"), Text(row,"Close_Date"),
                    string.IsNullOrWhiteSpace(Text(row,"Close_Date")), now
                }, ct);
            if (branchExisted) updated++; else inserted++;
        }

        var deactivated = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE reference_data.banks SET is_active = false, updated_at = {now}
            WHERE is_active = true AND last_seen_at < {now}
            """, ct);
        deactivated += await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE reference_data.bank_branches SET is_active = false, updated_at = {now}
            WHERE is_active = true AND last_seen_at < {now}
            """, ct);

        return new SyncStats(records.Count, inserted, updated, deactivated, new
        {
            source = "בנק ישראל / data.gov.il",
            banks = bankSeen.Count,
            branches = branchSeen.Count,
            resourceId = BankBranchesResource
        });
    }

    private async Task<SyncStats> SyncAddressDataAsync(CancellationToken ct)
    {
        var records = await FetchAllAsync(IsraelStreetsResource, ct);
        var now = DateTimeOffset.UtcNow;
        var cities = records
            .Select(row => new
            {
                CityCode = Int(row, "city_code"),
                CityName = Text(row, "city_name"),
                RegionCode = Int(row, "region_code"),
                RegionName = Text(row, "region_name")
            })
            .Where(x => x.CityCode.HasValue && !string.IsNullOrWhiteSpace(x.CityName))
            .GroupBy(x => x.CityCode!.Value)
            .Select(g => g.First())
            .ToList();
        var streets = records
            .Select(row => new
            {
                CityCode = Int(row, "city_code"),
                StreetCode = Int(row, "street_code"),
                StreetName = Text(row, "street_name"),
                StreetNameStatus = Text(row, "street_name_status") ?? "official",
                OfficialCode = Int(row, "official_code")
            })
            .Where(x => x.CityCode.HasValue && x.StreetCode.HasValue && x.OfficialCode.HasValue && !string.IsNullOrWhiteSpace(x.StreetName))
            .GroupBy(x => (x.CityCode!.Value, x.StreetCode!.Value))
            .Select(g => g.First())
            .ToList();

        var existingCities = await ScalarIntAsync($"SELECT count(*) FROM reference_data.cities WHERE source = '{AddressSource}'", ct);
        var existingStreets = await ScalarIntAsync($"SELECT count(*) FROM reference_data.streets WHERE source = '{AddressSource}'", ct);

        const int batchSize = 5000;
        for (var offset = 0; offset < cities.Count; offset += batchSize)
        {
            var json = JsonSerializer.Serialize(cities.Skip(offset).Take(batchSize).Select(x => new
            {
                city_code = x.CityCode!.Value,
                city_name = x.CityName,
                region_code = x.RegionCode,
                region_name = x.RegionName
            }));
            await ExecuteAsync("""
                WITH rows AS (
                    SELECT * FROM jsonb_to_recordset(CAST(@p0 AS jsonb)) AS x(
                        city_code integer, city_name text, region_code integer, region_name text)
                )
                INSERT INTO reference_data.cities
                    (city_code, city_name, region_code, region_name, is_active, source, last_seen_at, updated_at)
                SELECT city_code, city_name, region_code, region_name, true, @p1, @p2, @p2 FROM rows
                ON CONFLICT (city_code) DO UPDATE SET
                    city_name = EXCLUDED.city_name,
                    region_code = EXCLUDED.region_code,
                    region_name = EXCLUDED.region_name,
                    is_active = true,
                    source = EXCLUDED.source,
                    last_seen_at = EXCLUDED.last_seen_at,
                    updated_at = EXCLUDED.updated_at
                """, new object?[] { json, AddressSource, now }, ct);
        }

        for (var offset = 0; offset < streets.Count; offset += batchSize)
        {
            var json = JsonSerializer.Serialize(streets.Skip(offset).Take(batchSize).Select(x => new
            {
                city_code = x.CityCode!.Value,
                street_code = x.StreetCode!.Value,
                street_name = x.StreetName,
                official_code = x.OfficialCode!.Value,
                street_name_status = x.StreetNameStatus
            }));
            await ExecuteAsync("""
                WITH rows AS (
                    SELECT * FROM jsonb_to_recordset(CAST(@p0 AS jsonb)) AS x(
                        city_code integer, street_code integer, street_name text, official_code integer, street_name_status text)
                )
                INSERT INTO reference_data.streets
                    (city_code, street_code, street_name, official_code, street_name_status, is_active, source, last_seen_at, updated_at)
                SELECT city_code, street_code, street_name, official_code, street_name_status, true, @p1, @p2, @p2 FROM rows
                ON CONFLICT (city_code, street_code) DO UPDATE SET
                    street_name = EXCLUDED.street_name,
                    official_code = EXCLUDED.official_code,
                    street_name_status = EXCLUDED.street_name_status,
                    is_active = true,
                    source = EXCLUDED.source,
                    last_seen_at = EXCLUDED.last_seen_at,
                    updated_at = EXCLUDED.updated_at
                """, new object?[] { json, AddressSource, now }, ct);
        }

        var deactivated = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE reference_data.cities SET is_active = false, updated_at = {now}
            WHERE source = {AddressSource} AND is_active = true AND last_seen_at < {now}
            """, ct);
        deactivated += await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE reference_data.streets SET is_active = false, updated_at = {now}
            WHERE source = {AddressSource} AND is_active = true AND last_seen_at < {now}
            """, ct);

        var insertedCities = Math.Max(cities.Count - existingCities, 0);
        var insertedStreets = Math.Max(streets.Count - existingStreets, 0);
        var inserted = insertedCities + insertedStreets;
        var updated = Math.Max(cities.Count + streets.Count - inserted, 0);
        return new SyncStats(records.Count, inserted, updated, deactivated, new
        {
            source = "רשות האוכלוסין וההגירה / data.gov.il",
            resourceId = IsraelStreetsResource,
            cities = cities.Count,
            streets = streets.Count,
            officialStreets = streets.Count(x => string.Equals(x.StreetNameStatus, "official", StringComparison.OrdinalIgnoreCase)),
            synonyms = streets.Count(x => !string.Equals(x.StreetNameStatus, "official", StringComparison.OrdinalIgnoreCase))
        });
    }

    private async Task<SyncStats> SyncPensionProductsAsync(CancellationToken ct)
    {
        var transfer = await FetchAllAsync(TransferFundsResource, ct);
        var gemel = await FetchAllAsync(GemelNetResource, ct);
        var insurance = await FetchAllAsync(InsuranceNetResource, ct);
        var now = DateTimeOffset.UtcNow;
        var inserted = 0;
        var updated = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var row in transfer)
        {
            var domain = Text(row, "תחום") ?? "לא ידוע";
            var fundCode = Text(row, "מספר קופה -אישור מס הכנסה");
            var trackCode = Text(row, "מספר מסלול השקעה");
            var fundName = Text(row, "שם קופה / קרן") ?? Text(row, "שם קצר - קופה / קרן");
            if (string.IsNullOrWhiteSpace(fundName)) continue;
            var key = $"transfer:{domain}:{fundCode}:{trackCode}:{fundName}";
            seen.Add(key);
            if (await UpsertProductAsync(key, "cma_transfer", domain, NormalizeProductType(domain, null), fundCode, fundName,
                    Text(row, "שם קצר - קופה / קרן"), Text(row, "ח.פ. של חברה"), Text(row, "שם חברה"), trackCode,
                    Text(row, "שם מסלול ארוך") ?? Text(row, "שם מסלול קצר"), Text(row, "סוג מסלול השקעה"),
                    Int(row, "קוד בנק"), Text(row, "שם בנק"), Int(row, "מספר סניף"), Text(row, "מספר חשבון"),
                    Text(row, "תאריך עדכון אחרון"), now, ct)) inserted++; else updated++;
        }

        foreach (var row in LatestByFund(gemel))
        {
            var code = Text(row, "FUND_ID");
            var name = Text(row, "FUND_NAME");
            if (string.IsNullOrWhiteSpace(name)) continue;
            var classification = Text(row, "FUND_CLASSIFICATION");
            var key = $"gemel:{code}:{name}";
            seen.Add(key);
            if (await UpsertProductAsync(key, "gemelnet", "גמל", NormalizeProductType("גמל", classification), code, name, name,
                    Text(row, "MANAGING_CORPORATION_LEGAL_ID"), Text(row, "MANAGING_CORPORATION"), null, null, classification,
                    null, null, null, null, Text(row, "CURRENT_DATE"), now, ct)) inserted++; else updated++;
        }

        foreach (var row in LatestByFund(insurance))
        {
            var code = Text(row, "FUND_ID");
            var name = Text(row, "FUND_NAME");
            if (string.IsNullOrWhiteSpace(name)) continue;
            var classification = Text(row, "FUND_CLASSIFICATION");
            var key = $"insurance:{code}:{name}";
            seen.Add(key);
            if (await UpsertProductAsync(key, "insurancenet", "ביטוח", NormalizeProductType("ביטוח", classification), code, name, name,
                    Text(row, "PARENT_COMPANY_LEGAL_ID"), Text(row, "PARENT_COMPANY_NAME"), null, null, classification,
                    null, null, null, null, Text(row, "CURRENT_DATE"), now, ct)) inserted++; else updated++;
        }

        var deactivated = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE reference_data.pension_products SET is_active = false, updated_at = {now}
            WHERE is_active = true AND last_seen_at < {now}
            """, ct);

        return new SyncStats(transfer.Count + gemel.Count + insurance.Count, inserted, updated, deactivated, new
        {
            source = "רשות שוק ההון / data.gov.il",
            transferRecords = transfer.Count,
            gemelRecords = gemel.Count,
            insuranceRecords = insurance.Count,
            activeKeys = seen.Count,
            resources = new[] { TransferFundsResource, GemelNetResource, InsuranceNetResource }
        });
    }

    private async Task<bool> UpsertProductAsync(string key, string source, string domain, string productType, string? fundCode,
        string fundName, string? shortName, string? companyLegalId, string? companyName, string? trackCode, string? trackName,
        string? classification, int? bankCode, string? bankName, int? branchCode, string? accountNumber, string? sourceUpdatedAt,
        DateTimeOffset now, CancellationToken ct)
    {
        var existed = await ExistsAsync("SELECT 1 FROM reference_data.pension_products WHERE external_key = @p0", new object?[] { key }, ct);
        await ExecuteAsync("""
            INSERT INTO reference_data.pension_products
                (external_key, source, domain, product_type, fund_code, fund_name, short_name, company_legal_id, company_name,
                 investment_track_code, investment_track_name, classification, bank_code, bank_name, branch_code, account_number,
                 source_updated_at, is_active, last_seen_at, updated_at)
            VALUES (@p0,@p1,@p2,@p3,@p4,@p5,@p6,@p7,@p8,@p9,@p10,@p11,@p12,@p13,@p14,@p15,@p16,true,@p17,@p17)
            ON CONFLICT (external_key) DO UPDATE SET
                source = EXCLUDED.source, domain = EXCLUDED.domain, product_type = EXCLUDED.product_type,
                fund_code = EXCLUDED.fund_code, fund_name = EXCLUDED.fund_name, short_name = EXCLUDED.short_name,
                company_legal_id = EXCLUDED.company_legal_id, company_name = EXCLUDED.company_name,
                investment_track_code = EXCLUDED.investment_track_code, investment_track_name = EXCLUDED.investment_track_name,
                classification = EXCLUDED.classification, bank_code = EXCLUDED.bank_code, bank_name = EXCLUDED.bank_name,
                branch_code = EXCLUDED.branch_code, account_number = EXCLUDED.account_number, source_updated_at = EXCLUDED.source_updated_at,
                is_active = true, last_seen_at = EXCLUDED.last_seen_at, updated_at = EXCLUDED.updated_at
            """,
            new object?[] { key, source, domain, productType, fundCode, fundName, shortName, companyLegalId, companyName, trackCode,
                trackName, classification, bankCode, bankName, branchCode, accountNumber, sourceUpdatedAt, now }, ct);
        return !existed;
    }

    private static string NormalizeProductType(string domain, string? classification)
    {
        var value = $"{domain} {classification}";
        if (value.Contains("השתלמות", StringComparison.OrdinalIgnoreCase)) return "קרן השתלמות";
        if (value.Contains("פנסיה", StringComparison.OrdinalIgnoreCase)) return "קרן פנסיה";
        if (value.Contains("ביטוח", StringComparison.OrdinalIgnoreCase) || value.Contains("פוליס", StringComparison.OrdinalIgnoreCase)) return "ביטוח מנהלים / פוליסה";
        if (value.Contains("גמל", StringComparison.OrdinalIgnoreCase) || value.Contains("תגמולים", StringComparison.OrdinalIgnoreCase)) return "קופת גמל";
        return domain;
    }

    private static IEnumerable<JsonElement> LatestByFund(List<JsonElement> records) => records
        .GroupBy(x => $"{Text(x, "FUND_ID")}|{Text(x, "FUND_NAME")}", StringComparer.Ordinal)
        .Select(g => g.OrderByDescending(x => Text(x, "REPORT_PERIOD"), StringComparer.Ordinal).First());

    private async Task<List<JsonElement>> FetchAllAsync(string resourceId, CancellationToken ct)
    {
        const int limit = 5000;
        var offset = 0;
        var result = new List<JsonElement>();
        while (true)
        {
            var url = $"https://data.gov.il/api/3/action/datastore_search?resource_id={Uri.EscapeDataString(resourceId)}&limit={limit}&offset={offset}";
            using var response = await httpClient.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            if (!json.GetProperty("success").GetBoolean()) throw new InvalidOperationException("data.gov.il returned success=false.");
            var payload = json.GetProperty("result");
            var records = payload.GetProperty("records");
            foreach (var item in records.EnumerateArray()) result.Add(item.Clone());
            var total = payload.TryGetProperty("total", out var totalProp) && totalProp.TryGetInt32(out var totalValue) ? totalValue : result.Count;
            offset += records.GetArrayLength();
            if (records.GetArrayLength() == 0 || offset >= total) break;
        }
        return result;
    }

    private async Task InsertRunAsync(Guid id, string key, string name, Guid userId, DateTimeOffset started, CancellationToken ct) =>
        await ExecuteAsync("""
            INSERT INTO reference_data.sync_runs
                (id, integration_key, integration_name, status, started_at, triggered_by_user_id)
            VALUES (@p0,@p1,@p2,'Running',@p3,@p4)
            """, new object?[] { id, key, name, started, userId }, ct);

    private async Task CompleteRunAsync(Guid id, string status, DateTimeOffset finished, SyncStats stats, string? error, CancellationToken ct) =>
        await ExecuteAsync("""
            UPDATE reference_data.sync_runs SET status=@p1, finished_at=@p2, records_received=@p3,
                records_inserted=@p4, records_updated=@p5, records_deactivated=@p6, error_message=@p7,
                details_json=CAST(@p8 AS jsonb)
            WHERE id=@p0
            """, new object?[] { id, status, finished, stats.Received, stats.Inserted, stats.Updated, stats.Deactivated, error, JsonSerializer.Serialize(stats.Details) }, ct);

    private async Task<int> ScalarIntAsync(string sql, CancellationToken ct)
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        if (command.Connection!.State != System.Data.ConnectionState.Open) await command.Connection.OpenAsync(ct);
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }

    private async Task<bool> ExistsAsync(string sql, object?[] values, CancellationToken ct)
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        AddParameters(command, values);
        if (command.Connection!.State != System.Data.ConnectionState.Open) await command.Connection.OpenAsync(ct);
        return await command.ExecuteScalarAsync(ct) is not null;
    }

    private async Task<int> ExecuteAsync(string sql, object?[] values, CancellationToken ct)
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        AddParameters(command, values);
        if (command.Connection!.State != System.Data.ConnectionState.Open) await command.Connection.OpenAsync(ct);
        return await command.ExecuteNonQueryAsync(ct);
    }

    private static void AddParameters(DbCommand command, object?[] values)
    {
        for (var i = 0; i < values.Length; i++)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = $"p{i}";
            parameter.Value = values[i] ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }
    }

    private static string? Text(JsonElement row, string name)
    {
        if (!row.TryGetProperty(name, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString()?.Trim() : value.ToString().Trim();
    }

    private static int? Int(JsonElement row, string name)
    {
        var text = Text(row, name);
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    private sealed record SyncStats(int Received, int Inserted, int Updated, int Deactivated, object Details);
}

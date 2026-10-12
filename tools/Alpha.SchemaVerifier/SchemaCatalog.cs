using System.Text.Json;
using Npgsql;

namespace Alpha.SchemaVerifier;

/// <summary>
/// Canonical, data-free description of a PostgreSQL schema, read from <c>pg_catalog</c> only.
/// <c>pg_catalog</c> is visible to any role with CONNECT, so capturing it needs no access to table data.
/// </summary>
public sealed record SchemaCatalog(
    IReadOnlyList<string> Schemas,
    IReadOnlyDictionary<string, string> Objects,
    IReadOnlyList<string> Extensions)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>The application schemas compared by default; objects in other schemas (platform-managed) are ignored.</summary>
    public static readonly string[] ApplicationSchemas =
        ["audit", "billing", "employees", "employers", "identity", "organizations", "reference_data", "reporting"];

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static SchemaCatalog FromJson(string json) =>
        JsonSerializer.Deserialize<SchemaCatalog>(json) ?? throw new InvalidDataException("Empty schema catalog.");

    /// <summary>
    /// Opens a session that cannot write: the session default is read-only and every statement runs inside an
    /// explicit READ ONLY transaction whose state is verified before any catalog query.
    /// </summary>
    public static async Task<NpgsqlConnection> OpenReadOnlyAsync(string connectionString, CancellationToken ct)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Options = "-c default_transaction_read_only=on -c statement_timeout=60000",
            ApplicationName = "alpha-schema-verifier"
        };
        var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync(ct);
        return connection;
    }

    public static async Task<SchemaCatalog> CaptureAsync(NpgsqlConnection connection, IReadOnlyCollection<string>? schemas, CancellationToken ct)
    {
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        await Execute(connection, transaction, "SET TRANSACTION READ ONLY", ct);
        if (await Scalar(connection, transaction, "SELECT current_setting('transaction_read_only')", ct) != "on")
            throw new InvalidOperationException("Refusing to read the catalog: the session is not read-only.");

        var scope = (schemas ?? ApplicationSchemas).ToArray();
        var objects = new SortedDictionary<string, string>(StringComparer.Ordinal);
        async Task Collect(string sql)
        {
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            command.Parameters.AddWithValue("schemas", scope);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) objects[reader.GetString(0)] = reader.IsDBNull(1) ? "" : reader.GetString(1);
        }

        const string ns = "n.nspname = ANY(@schemas)";
        await Collect($"""
            SELECT 'table ' || n.nspname || '.' || c.relname, c.relkind::text
            FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE {ns} AND c.relkind IN ('r','p')
            """);
        await Collect($"""
            SELECT 'column ' || n.nspname || '.' || c.relname || '.' || a.attname,
                   format_type(a.atttypid, a.atttypmod) || CASE WHEN a.attnotnull THEN ' NOT NULL' ELSE ' NULL' END ||
                   COALESCE(' DEFAULT ' || pg_get_expr(d.adbin, d.adrelid), '')
            FROM pg_attribute a
            JOIN pg_class c ON c.oid = a.attrelid JOIN pg_namespace n ON n.oid = c.relnamespace
            LEFT JOIN pg_attrdef d ON d.adrelid = a.attrelid AND d.adnum = a.attnum
            WHERE {ns} AND c.relkind IN ('r','p') AND a.attnum > 0 AND NOT a.attisdropped
            """);
        // Constraints and indexes are keyed by table and definition, not by name: EF and the initializers name
        // equivalent objects differently, and a rename is not a compatibility problem.
        await Collect($"""
            SELECT 'constraint ' || n.nspname || '.' || c.relname || ' ' || pg_get_constraintdef(k.oid), k.conname
            FROM pg_constraint k JOIN pg_class c ON c.oid = k.conrelid JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE {ns}
            """);
        await Collect($"""
            SELECT 'index ' || n.nspname || '.' || t.relname || ' ' ||
                   regexp_replace(pg_get_indexdef(i.indexrelid), '^CREATE (UNIQUE )?INDEX \S+ ON ', 'CREATE \1INDEX ON '),
                   ic.relname
            FROM pg_index i
            JOIN pg_class ic ON ic.oid = i.indexrelid JOIN pg_class t ON t.oid = i.indrelid
            JOIN pg_namespace n ON n.oid = t.relnamespace
            WHERE {ns} AND NOT EXISTS (SELECT 1 FROM pg_constraint k WHERE k.conindid = i.indexrelid AND k.contype IN ('p','u','x'))
            """);
        await Collect($"""
            SELECT 'trigger ' || n.nspname || '.' || c.relname || '.' || t.tgname,
                   regexp_replace(pg_get_triggerdef(t.oid), '^CREATE (CONSTRAINT )?TRIGGER \S+ ', 'CREATE \1TRIGGER ')
            FROM pg_trigger t JOIN pg_class c ON c.oid = t.tgrelid JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE {ns} AND NOT t.tgisinternal
            """);
        await Collect($"""
            SELECT 'function ' || n.nspname || '.' || p.proname || '(' || pg_get_function_identity_arguments(p.oid) || ')',
                   md5(pg_get_functiondef(p.oid))
            FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
            WHERE {ns} AND p.prokind = 'f'
            """);
        await Collect($"""
            SELECT 'sequence ' || n.nspname || '.' || c.relname, format_type(s.seqtypid, NULL)
            FROM pg_sequence s JOIN pg_class c ON c.oid = s.seqrelid JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE {ns}
            """);
        await Collect($"""
            SELECT 'view ' || n.nspname || '.' || c.relname, md5(pg_get_viewdef(c.oid))
            FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE {ns} AND c.relkind IN ('v','m')
            """);
        await Collect($"""
            SELECT 'enum ' || n.nspname || '.' || t.typname, string_agg(e.enumlabel, ',' ORDER BY e.enumsortorder)
            FROM pg_type t JOIN pg_enum e ON e.enumtypid = t.oid JOIN pg_namespace n ON n.oid = t.typnamespace
            WHERE {ns} GROUP BY n.nspname, t.typname
            """);

        var extensions = new List<string>();
        await using (var command = new NpgsqlCommand("SELECT extname || ' ' || extversion FROM pg_extension ORDER BY 1", connection, transaction))
        await using (var reader = await command.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct)) extensions.Add(reader.GetString(0));

        var present = new List<string>();
        await using (var command = new NpgsqlCommand("SELECT nspname FROM pg_namespace WHERE nspname = ANY(@schemas) ORDER BY 1", connection, transaction))
        {
            command.Parameters.AddWithValue("schemas", scope);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) present.Add(reader.GetString(0));
        }

        await transaction.RollbackAsync(ct);
        return new SchemaCatalog(present, objects, extensions);
    }

    private static async Task Execute(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<string?> Scalar(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        return (await command.ExecuteScalarAsync(ct))?.ToString();
    }
}

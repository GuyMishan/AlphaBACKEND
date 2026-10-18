using Microsoft.EntityFrameworkCore;

namespace Alpha.Infrastructure.Persistence;

/// <summary>Idempotent additive schema for the single platform legal document and its immutable published versions.</summary>
public static class LegalDocumentSchemaInitializer
{
    public static Task EnsureCreatedAsync(AlphaDbContext db) => db.Database.ExecuteSqlRawAsync("""
        CREATE TABLE IF NOT EXISTS reference_data.legal_document_versions (
            id uuid PRIMARY KEY,
            version integer NOT NULL UNIQUE CHECK (version > 0),
            title varchar(200) NOT NULL,
            markdown text NOT NULL,
            status varchar(16) NOT NULL CHECK (status IN ('draft', 'published', 'archived')),
            requires_reacceptance boolean NOT NULL DEFAULT false,
            created_at timestamptz NOT NULL,
            published_at timestamptz NULL,
            effective_at timestamptz NULL,
            archived_at timestamptz NULL,
            CHECK (status = 'draft' OR (published_at IS NOT NULL AND effective_at IS NOT NULL)),
            CHECK (status <> 'draft' OR (published_at IS NULL AND effective_at IS NULL AND archived_at IS NULL)),
            CHECK (status <> 'archived' OR archived_at IS NOT NULL)
        );
        CREATE UNIQUE INDEX IF NOT EXISTS ix_legal_one_draft
            ON reference_data.legal_document_versions (status) WHERE status = 'draft';
        """);
}

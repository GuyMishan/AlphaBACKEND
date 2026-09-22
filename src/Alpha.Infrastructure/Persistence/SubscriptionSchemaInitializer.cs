using Alpha.Domain.Subscriptions;
using Microsoft.EntityFrameworkCore;

namespace Alpha.Infrastructure.Persistence;

public static class SubscriptionSchemaInitializer
{
    public const string FreePlanCode = "FREE";
    public const string BusinessPlanCode = "BUSINESS";
    public const string MultiEmployerPlanCode = "MULTI_EMPLOYER";
    public const string OrganizationPlanCode = "ORGANIZATION";

    public static async Task EnsureCreatedAsync(AlphaDbContext db, CancellationToken ct = default)
    {
        const string sql = """
            CREATE SCHEMA IF NOT EXISTS subscriptions;

            CREATE TABLE IF NOT EXISTS subscriptions.plans (
                "Id" uuid NOT NULL PRIMARY KEY,
                "Code" character varying(50) NOT NULL,
                "Name" character varying(120) NOT NULL,
                "MaxEmployers" integer NOT NULL,
                "MaxEmployees" integer NOT NULL,
                "MaxUsers" integer NOT NULL,
                "IsActive" boolean NOT NULL,
                "CreatedAt" timestamp with time zone NOT NULL,
                "UpdatedAt" timestamp with time zone NOT NULL
            );

            CREATE UNIQUE INDEX IF NOT EXISTS "IX_plans_Code"
                ON subscriptions.plans ("Code");

            CREATE TABLE IF NOT EXISTS subscriptions.subscriptions (
                "Id" uuid NOT NULL PRIMARY KEY,
                "OrganizationId" uuid NOT NULL,
                "PlanId" uuid NOT NULL,
                "Status" character varying(40) NOT NULL,
                "StartedAt" timestamp with time zone NOT NULL,
                "ExpiresAt" timestamp with time zone NULL,
                "CreatedAt" timestamp with time zone NOT NULL,
                "UpdatedAt" timestamp with time zone NOT NULL,
                CONSTRAINT "FK_subscriptions_organizations_OrganizationId"
                    FOREIGN KEY ("OrganizationId") REFERENCES organizations.organizations ("Id") ON DELETE RESTRICT,
                CONSTRAINT "FK_subscriptions_plans_PlanId"
                    FOREIGN KEY ("PlanId") REFERENCES subscriptions.plans ("Id") ON DELETE RESTRICT
            );

            CREATE UNIQUE INDEX IF NOT EXISTS "IX_subscriptions_OrganizationId"
                ON subscriptions.subscriptions ("OrganizationId");
            CREATE INDEX IF NOT EXISTS "IX_subscriptions_PlanId"
                ON subscriptions.subscriptions ("PlanId");
            """;

        await db.Database.ExecuteSqlRawAsync(sql, ct);

        var freePlan = await db.Plans.SingleOrDefaultAsync(x => x.Code == FreePlanCode, ct);
        if (freePlan is null)
        {
            freePlan = new Plan(FreePlanCode, "Free", maxEmployers: 1, maxEmployees: 3, maxUsers: 1);
            db.Plans.Add(freePlan);
            await db.SaveChangesAsync(ct);
        }

        var organizationIdsWithSubscription = await db.Subscriptions.AsNoTracking()
            .Select(x => x.OrganizationId)
            .ToListAsync(ct);
        var subscribed = organizationIdsWithSubscription.ToHashSet();
        var organizationIds = await db.Organizations.AsNoTracking().Select(x => x.Id).ToListAsync(ct);

        foreach (var organizationId in organizationIds.Where(x => !subscribed.Contains(x)))
            db.Subscriptions.Add(new Subscription(organizationId, freePlan.Id));

        if (db.ChangeTracker.HasChanges())
            await db.SaveChangesAsync(ct);
    }
}

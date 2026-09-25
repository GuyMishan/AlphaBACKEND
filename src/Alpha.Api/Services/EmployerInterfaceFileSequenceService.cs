using System.Data;
using Alpha.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Alpha.Api.Services;

public sealed class EmployerInterfaceFileSequenceService(
    AlphaDbContext db,
    IOptions<EmployerInterface006Options> options)
{
    public sealed record Reservation(int Sequence, DateTimeOffset PreparedAt, string SenderIdentifier);

    public async Task<Reservation> ReserveAsync(Guid employerId, CancellationToken ct)
    {
        var preparedAt = IsraelNow();
        var settings = options.Value;
        var senderIdentifier = settings.SenderIdentifier?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(senderIdentifier) && settings.SenderCode == 5)
        {
            var employer = await db.Employers.AsNoTracking().SingleAsync(x => x.Id == employerId, ct);
            senderIdentifier = new string(employer.RegistrationNumber.Where(char.IsDigit).ToArray());
        }

        if (string.IsNullOrWhiteSpace(senderIdentifier))
            throw new InvalidOperationException("Employer Interface sender identifier is not configured.");

        var businessDate = DateOnly.FromDateTime(preparedAt.Date);
        var connection = db.Database.GetDbConnection();
        var closeAfter = connection.State != ConnectionState.Open;
        if (closeAfter) await connection.OpenAsync(ct);

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO reporting.employer_interface_file_sequences
                    ("SenderIdentifier", "BusinessDate", "LastSequence")
                VALUES (@sender, @businessDate, 1)
                ON CONFLICT ("SenderIdentifier", "BusinessDate")
                DO UPDATE SET "LastSequence" = reporting.employer_interface_file_sequences."LastSequence" + 1
                WHERE reporting.employer_interface_file_sequences."LastSequence" < 9999
                RETURNING "LastSequence";
                """;

            var sender = command.CreateParameter();
            sender.ParameterName = "@sender";
            sender.Value = senderIdentifier;
            command.Parameters.Add(sender);

            var date = command.CreateParameter();
            date.ParameterName = "@businessDate";
            date.Value = businessDate.ToDateTime(TimeOnly.MinValue);
            command.Parameters.Add(date);

            var value = await command.ExecuteScalarAsync(ct);
            if (value is null || value is DBNull)
                throw new InvalidOperationException("Employer Interface daily file sequence exhausted (maximum 9999 files per sender/day).");

            return new Reservation(Convert.ToInt32(value), preparedAt, senderIdentifier);
        }
        finally
        {
            if (closeAfter) await connection.CloseAsync();
        }
    }

    private static DateTimeOffset IsraelNow()
    {
        try
        {
            return TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Asia/Jerusalem"));
        }
        catch (TimeZoneNotFoundException)
        {
            return DateTimeOffset.UtcNow;
        }
    }
}

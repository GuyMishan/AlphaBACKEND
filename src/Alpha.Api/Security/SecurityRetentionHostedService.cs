using Alpha.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Alpha.Api.Security;

public sealed class SecurityRetentionHostedService(IServiceScopeFactory scopeFactory, IConfiguration configuration, ILogger<SecurityRetentionHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(6));
        do
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<AlphaDbContext>();
                var otpHours = Math.Max(1, configuration.GetValue("Security:OtpRetentionHours", 24));
                var cutoff = DateTime.UtcNow.AddHours(-otpHours);
                var login = await db.OtpChallenges.Where(x => x.CreatedAt < cutoff).ExecuteDeleteAsync(stoppingToken);
                var registration = await db.RegistrationOtpChallenges.Where(x => x.CreatedAt < cutoff).ExecuteDeleteAsync(stoppingToken);
                var invitationDays = Math.Max(1, configuration.GetValue("Security:ExpiredInvitationRetentionDays", 30));
                var invitationCutoff = DateTimeOffset.UtcNow.AddDays(-invitationDays);
                var invitations = await db.UserInvitations.Where(x => x.ExpiresAt < invitationCutoff && x.Status != Alpha.Domain.Identity.UserInvitationStatus.Pending).ExecuteDeleteAsync(stoppingToken);
                logger.LogInformation("Security retention removed temporary data only: {LoginOtp} login OTP, {RegistrationOtp} registration OTP, {Invitations} expired/cancelled invitations. Business, pension, employee, report, feedback, document and audit records are never deleted by this job.", login, registration, invitations);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Security retention cycle failed"); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

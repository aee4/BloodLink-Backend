using BloodLink.Infrastructure.Data;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace BloodLink.Api.Security;

public sealed class DatabaseReadyHealthCheck(BloodLinkDbContext dbContext) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        await dbContext.Database.CanConnectAsync(cancellationToken)
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy("Database is unavailable.");
}

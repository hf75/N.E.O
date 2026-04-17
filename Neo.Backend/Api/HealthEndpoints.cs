using Neo.Backend.Services;

namespace Neo.Backend.Api;

public static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapHealth(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/health", () => Results.Ok(new
        {
            status = "ok",
            time = DateTime.UtcNow,
        }));

        app.MapGet("/api/providers", (ProviderRegistry reg) =>
            Results.Ok(reg.Snapshot()));

        return app;
    }
}

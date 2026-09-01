using FileOrganizer.Shared.Models;

namespace FileOrganizer.Shared.Contracts;

public interface IPythonApiClient
{
    void Configure(int port, string bearerToken);
    Task<bool> HealthCheckAsync(CancellationToken ct = default);
    Task<AnalyzeResponse?> AnalyzeAsync(AnalyzeRequest request, CancellationToken ct = default);
}

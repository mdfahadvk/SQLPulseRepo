using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SQLPulseAPI.Net8.Repositories;
using SQLPulseAPI.Net8.Tests.Fakes;

namespace SQLPulseAPI.Net8.Tests;

/// <summary>
/// Boots the API in-memory. No SQL Server or LLM credentials are configured, which
/// is deliberate: nothing in this test project may depend on a live server.
/// </summary>
public class SqlPulseApiFactory : WebApplicationFactory<Program>
{
    /// <summary>Replaces registrations, for example to swap in a fake SQL executor.</summary>
    public Action<IServiceCollection>? ConfigureTestServices { get; init; }

    /// <summary>
    /// Historical storage used by the hosted application, unless a test replaces it.
    /// </summary>
    /// <remarks>
    /// In-memory by default so tests never create a SQLite file on disk and never share
    /// history between each other.
    /// </remarks>
    public InMemoryHistoryRepository HistoryRepository { get; } = new();

    /// <summary>
    /// Blanks every credential the application would otherwise pick up from the machine.
    /// </summary>
    /// <remarks>
    /// The host runs in the Development environment, so the application's own
    /// configuration pipeline loads User Secrets and machine environment variables. A
    /// developer who has configured a real Groq key or real SQL Server credentials for
    /// running the app would otherwise have these tests silently start calling those live
    /// services - reaching the network, consuming quota, and failing on someone else's
    /// machine. Blanking the values here keeps the suite hermetic and self-contained.
    /// </remarks>
    private static readonly Dictionary<string, string?> NeutralizedCredentials = new()
    {
        ["Llm:ApiKey"] = string.Empty,
        ["Llm:BaseUrl"] = string.Empty,
        ["SqlServer:Server"] = string.Empty,
        ["SqlServer:Username"] = string.Empty,
        ["SqlServer:Password"] = string.Empty,
    };

    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        return base.CreateHost(builder);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Added last, so it overrides User Secrets and environment variables alike.
        builder.ConfigureAppConfiguration(configuration =>
            configuration.AddInMemoryCollection(NeutralizedCredentials));

        // Registered first so a test's own hook can still override it.
        builder.ConfigureTestServices(services =>
            services.AddSingleton<IHistoryRepository>(HistoryRepository));

        if (ConfigureTestServices is not null)
        {
            builder.ConfigureTestServices(ConfigureTestServices);
        }
    }
}

using Microsoft.Extensions.Options;

namespace SQLPulseAPI.Net8.Configuration;

/// <summary>
/// Registers and validates every SQLPulse options section.
/// </summary>
public static class SqlPulseConfiguration
{
    /// <summary>
    /// Binds all option sections with data-annotation validation and start-up validation.
    /// </summary>
    public static IServiceCollection AddSqlPulseOptions(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddValidatedOptions<ApplicationOptions>(configuration, ApplicationOptions.SectionName);
        services.AddValidatedOptions<SqlServerOptions>(configuration, SqlServerOptions.SectionName);
        services.AddValidatedOptions<AnalysisOptions>(configuration, AnalysisOptions.SectionName);
        services.AddValidatedOptions<DetectorThresholdOptions>(configuration, DetectorThresholdOptions.SectionName);
        services.AddValidatedOptions<HistoryOptions>(configuration, HistoryOptions.SectionName);
        services.AddValidatedOptions<SqlErrorOptions>(configuration, SqlErrorOptions.SectionName);
        services.AddValidatedOptions<LlmOptions>(configuration, LlmOptions.SectionName);
        services.AddValidatedOptions<AgentOptions>(configuration, AgentOptions.SectionName);
        return services;
    }

    private static void AddValidatedOptions<TOptions>(
        this IServiceCollection services,
        IConfiguration configuration,
        string sectionName)
        where TOptions : class
    {
        services.AddOptions<TOptions>()
            .Bind(configuration.GetSection(sectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
    }

    /// <summary>
    /// Validates an options instance outside the DI container. Used by tests and by
    /// callers that build options manually.
    /// </summary>
    public static IReadOnlyList<string> Validate<TOptions>(TOptions options)
        where TOptions : class
    {
        var context = new System.ComponentModel.DataAnnotations.ValidationContext(options);
        var results = new List<System.ComponentModel.DataAnnotations.ValidationResult>();
        System.ComponentModel.DataAnnotations.Validator.TryValidateObject(
            options, context, results, validateAllProperties: true);
        return results.Select(r => r.ErrorMessage ?? "Validation failed").ToList();
    }
}

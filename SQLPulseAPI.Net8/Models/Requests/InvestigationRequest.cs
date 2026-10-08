using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace SQLPulseAPI.Net8.Models.Requests;

/// <summary>
/// Natural-language investigation request. Python: <c>InvestigationRequest</c>.
/// </summary>
public sealed class InvestigationRequest
{
    /// <summary>Natural-language performance question.</summary>
    [Required]
    [StringLength(2000, MinimumLength = 3)]
    [JsonPropertyName("question")]
    public string Question { get; set; } = string.Empty;
}

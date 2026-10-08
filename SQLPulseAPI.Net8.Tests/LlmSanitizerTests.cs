using System.Text.Json.Nodes;
using SQLPulseAPI.Net8.Services.Agent;
using Xunit;

namespace SQLPulseAPI.Net8.Tests;

/// <summary>
/// Sanitization guarantees for anything crossing the boundary to an AI provider:
/// credentials are removed, oversized text is trimmed, and payloads are shrunk to fit
/// the token budget without misstating how much was found.
/// </summary>
public class LlmSanitizerTests
{
    // --- Credential redaction ------------------------------------------------

    [Theory]
    [InlineData("Login failed: Password=Sup3rSecret!")]
    [InlineData("connect using pwd=hunter2")]
    [InlineData("Authorization: Bearer sk-live-abcdefghijklmnop")]
    [InlineData("api_key=sk-proj-1234567890")]
    [InlineData("secret-key=abcdef123456")]
    [InlineData("connection string=Server=sql01;Uid=sa;Pwd=letmein")]
    [InlineData("UID=sa; PWD=letmein")]
    [InlineData("SERVER=sql01;DATABASE=AppDb;PWD=letmein")]
    public void Sanitize_text_removes_credentials(string text)
    {
        var sanitized = LlmSanitizer.SanitizeText(text);

        Assert.Contains(LlmSanitizer.Redacted, sanitized);
        Assert.DoesNotContain("Sup3rSecret", sanitized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hunter2", sanitized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("letmein", sanitized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sk-live", sanitized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sk-proj", sanitized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("abcdef123456", sanitized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Sanitize_text_leaves_ordinary_sql_alone()
    {
        const string sql = "SELECT OrderId, Total FROM dbo.Orders WHERE CustomerId = 42";

        Assert.Equal(sql, LlmSanitizer.SanitizeText(sql));
    }

    [Theory]
    [InlineData("password")]
    [InlineData("Password")]
    [InlineData("pwd")]
    [InlineData("api_key")]
    [InlineData("api-key")]
    [InlineData("connection_string")]
    [InlineData("sql_password")]
    [InlineData("access_token")]
    [InlineData("refresh_token")]
    public void Sensitive_keys_are_replaced_whatever_the_value(string key)
    {
        var payload = new JsonObject { [key] = "value-that-must-not-escape" };

        var sanitized = LlmSanitizer.Sanitize(payload)!.AsObject();

        Assert.Equal(LlmSanitizer.Redacted, sanitized[key]!.GetValue<string>());
    }

    [Fact]
    public void Sensitive_keys_are_replaced_at_any_depth()
    {
        var payload = new JsonObject
        {
            ["connection"] = new JsonObject
            {
                ["server"] = "sql01",
                ["credentials"] = new JsonObject { ["password"] = "letmein" },
            },
        };

        var sanitized = LlmSanitizer.Sanitize(payload)!.ToJsonString();

        Assert.DoesNotContain("letmein", sanitized, StringComparison.Ordinal);
        Assert.Contains("sql01", sanitized, StringComparison.Ordinal);
    }

    /// <summary>
    /// The Python reference copies list entries through untouched; SQLPulse sanitizes them,
    /// because a credential inside a list of strings would otherwise reach the provider.
    /// </summary>
    [Fact]
    public void Strings_inside_arrays_are_sanitized()
    {
        var payload = new JsonObject
        {
            ["evidence"] = new JsonArray("Connection failed with Password=letmein"),
        };

        var sanitized = LlmSanitizer.Sanitize(payload)!.ToJsonString();

        Assert.DoesNotContain("letmein", sanitized, StringComparison.Ordinal);
    }

    [Fact]
    public void Excessive_nesting_is_truncated_rather_than_walked()
    {
        var deepest = new JsonObject { ["password"] = "letmein" };
        JsonNode current = deepest;
        for (var depth = 0; depth < 20; depth++)
        {
            current = new JsonObject { ["next"] = current };
        }

        var sanitized = LlmSanitizer.Sanitize(current)!.ToJsonString();

        Assert.Contains(LlmSanitizer.TruncatedFlag, sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("letmein", sanitized, StringComparison.Ordinal);
    }

    // --- Verbose field trimming ---------------------------------------------

    [Fact]
    public void Verbose_sql_text_is_trimmed_to_a_preview()
    {
        var payload = new JsonObject { ["sql_text"] = new string('x', 5000) };

        var trimmed = LlmSanitizer.SanitizeForLlm(payload);
        var text = trimmed["sql_text"]!.GetValue<string>();

        Assert.Equal(300, text.Length);
        Assert.EndsWith("[truncated]", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Short_sql_text_is_left_intact()
    {
        const string sql = "SELECT 1";
        var payload = new JsonObject { ["sql_text"] = sql };

        Assert.Equal(sql, LlmSanitizer.SanitizeForLlm(payload)["sql_text"]!.GetValue<string>());
    }

    [Fact]
    public void Verbose_fields_are_trimmed_inside_nested_lists()
    {
        var payload = new JsonObject
        {
            ["queries"] = new JsonArray(
                new JsonObject { ["query_plan"] = new string('p', 4000) }),
        };

        var trimmed = LlmSanitizer.SanitizeForLlm(payload);
        var plan = trimmed["queries"]![0]!["query_plan"]!.GetValue<string>();

        Assert.Equal(300, plan.Length);
    }

    [Fact]
    public void Non_verbose_long_text_is_not_trimmed()
    {
        var payload = new JsonObject { ["summary"] = new string('s', 2000) };

        Assert.Equal(2000, LlmSanitizer.SanitizeForLlm(payload)["summary"]!.GetValue<string>().Length);
    }

    // --- Serialization budget ------------------------------------------------

    [Fact]
    public void Payload_within_budget_is_serialized_unchanged()
    {
        var payload = new JsonObject { ["database"] = "AppDb", ["candidate_count"] = 2 };

        var json = LlmSanitizer.SerializeForLlm(payload, 4000);

        Assert.Equal(payload.ToJsonString(), json);
        Assert.DoesNotContain(LlmSanitizer.TruncatedFlag, json, StringComparison.Ordinal);
    }

    [Fact]
    public void Oversized_payload_is_shrunk_by_halving_its_longest_list()
    {
        var items = new JsonArray();
        for (var index = 0; index < 200; index++)
        {
            items.Add(new JsonObject { ["query_hash"] = $"0x{index:X8}", ["rank"] = index });
        }

        var payload = new JsonObject { ["candidate_count"] = 200, ["candidates"] = items };

        var json = LlmSanitizer.SerializeForLlm(payload, 1000);

        Assert.True(json.Length <= 1000);
        Assert.Contains(LlmSanitizer.TruncatedFlag, json, StringComparison.Ordinal);

        // The count survives the shrink so the model cannot understate what was found.
        var reparsed = JsonNode.Parse(json)!.AsObject();
        Assert.Equal(200, reparsed["candidate_count"]!.GetValue<int>());
        Assert.True(reparsed["candidates"]!.AsArray().Count < 200);
        Assert.Contains("truncated", reparsed["_truncation_note"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_payload_that_cannot_be_shrunk_is_hard_cut_within_budget()
    {
        // A single huge scalar: there is no list to halve, so only a hard cut is left.
        var payload = new JsonObject { ["summary"] = new string('z', 5000) };

        var json = LlmSanitizer.SerializeForLlm(payload, 500);

        Assert.True(json.Length <= 500);
        Assert.EndsWith("\"_truncated\":true}", json, StringComparison.Ordinal);
    }
}

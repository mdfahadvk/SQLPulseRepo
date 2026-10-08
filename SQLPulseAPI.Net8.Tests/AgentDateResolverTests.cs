using SQLPulseAPI.Net8.Services.Agent;
using Xunit;

namespace SQLPulseAPI.Net8.Tests;

/// <summary>
/// Deterministic resolution of the dates an AI model supplies. The year correction is the
/// point of this code: a model asked about "the 5th of January" will happily produce a year
/// from its training data, and a silently wrong year returns an empty result set that reads
/// like a healthy server.
/// </summary>
public class AgentDateResolverTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 15, 14, 30, 0, TimeSpan.Zero);

    private static DateTimeOffset Resolve(string value) => AgentDateResolver.Resolve(value, Now);

    // --- Relative terms ------------------------------------------------------

    [Theory]
    [InlineData("today")]
    [InlineData("Today")]
    [InlineData("  TODAY  ")]
    public void Today_resolves_to_midnight_today(string value)
    {
        Assert.Equal(new DateTimeOffset(2026, 6, 15, 0, 0, 0, TimeSpan.Zero), Resolve(value));
    }

    [Theory]
    [InlineData("yesterday")]
    [InlineData("Yesterday")]
    public void Yesterday_resolves_to_midnight_yesterday(string value)
    {
        Assert.Equal(new DateTimeOffset(2026, 6, 14, 0, 0, 0, TimeSpan.Zero), Resolve(value));
    }

    // --- ISO parsing ---------------------------------------------------------

    [Fact]
    public void Date_only_resolves_to_midnight()
    {
        Assert.Equal(new DateTimeOffset(2026, 6, 10, 0, 0, 0, TimeSpan.Zero), Resolve("2026-06-10"));
    }

    [Theory]
    [InlineData("2026-06-10T09:15:00")]
    [InlineData("2026-06-10 09:15:00")]
    [InlineData("2026-06-10T09:15")]
    [InlineData("2026-06-10T09:15:00.250")]
    public void Iso_datetimes_are_accepted(string value)
    {
        var resolved = Resolve(value);

        Assert.Equal(new DateTime(2026, 6, 10), resolved.Date);
        Assert.Equal(9, resolved.Hour);
        Assert.Equal(15, resolved.Minute);
    }

    /// <summary>
    /// The offset is discarded rather than applied, matching the reference agent and the
    /// REST endpoints: an explicit offset from a model is not evidence about the server's
    /// reporting timezone, and shifting the clock would silently move the window.
    /// </summary>
    [Theory]
    [InlineData("2026-06-10T09:15:00Z")]
    [InlineData("2026-06-10T09:15:00+05:00")]
    [InlineData("2026-06-10T09:15:00-08:00")]
    public void Offsets_preserve_the_wall_clock_reading(string value)
    {
        var resolved = Resolve(value);

        Assert.Equal(9, resolved.Hour);
        Assert.Equal(TimeSpan.Zero, resolved.Offset);
    }

    // --- End-of-day normalization -------------------------------------------

    [Theory]
    [InlineData("2026-06-10T23:59:59")]
    [InlineData("2026-06-10T23:59:59.999")]
    public void Inclusive_end_of_day_becomes_the_following_midnight(string value)
    {
        Assert.Equal(new DateTimeOffset(2026, 6, 11, 0, 0, 0, TimeSpan.Zero), Resolve(value));
    }

    [Fact]
    public void Ordinary_times_are_not_shifted()
    {
        Assert.Equal(new DateTimeOffset(2026, 6, 10, 23, 58, 0, TimeSpan.Zero), Resolve("2026-06-10T23:58:00"));
    }

    // --- Year correction -----------------------------------------------------

    [Fact]
    public void Current_year_is_trusted()
    {
        Assert.Equal(2026, Resolve("2026-01-05").Year);
    }

    [Fact]
    public void Previous_year_is_trusted_because_it_is_a_plausible_request()
    {
        Assert.Equal(2025, Resolve("2025-12-20").Year);
    }

    [Fact]
    public void An_implausibly_old_year_is_corrected_to_this_year_when_that_date_has_passed()
    {
        // A model asked in 2026 about "5 January" should not send SQLPulse to 2024.
        Assert.Equal(new DateTimeOffset(2026, 1, 5, 0, 0, 0, TimeSpan.Zero), Resolve("2024-01-05"));
    }

    [Fact]
    public void An_implausibly_old_year_falls_back_to_last_year_when_this_year_is_still_future()
    {
        // 20 December has not happened yet in June 2026, so the most recent occurrence is 2025.
        Assert.Equal(new DateTimeOffset(2025, 12, 20, 0, 0, 0, TimeSpan.Zero), Resolve("2023-12-20"));
    }

    [Fact]
    public void Year_correction_keeps_the_time_of_day()
    {
        var resolved = Resolve("2020-03-02T08:45:30");

        Assert.Equal(new DateTimeOffset(2026, 3, 2, 8, 45, 30, TimeSpan.Zero), resolved);
    }

    [Fact]
    public void Leap_day_is_clamped_when_the_target_year_is_not_a_leap_year()
    {
        // 2026 is not a leap year: 29 February must become 28 February, not overflow.
        Assert.Equal(new DateTimeOffset(2026, 2, 28, 0, 0, 0, TimeSpan.Zero), Resolve("2020-02-29"));
    }

    [Fact]
    public void A_future_year_is_left_alone()
    {
        Assert.Equal(2027, Resolve("2027-01-05").Year);
    }

    // --- Rejection -----------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("last Tuesday")]
    [InlineData("not a date")]
    [InlineData("2026-13-45")]
    public void Unusable_values_are_rejected(string value)
    {
        Assert.False(AgentDateResolver.TryResolve(value, Now, out _));
        Assert.Throws<FormatException>(() => AgentDateResolver.Resolve(value, Now));
    }

    [Fact]
    public void Null_is_rejected()
    {
        Assert.False(AgentDateResolver.TryResolve(null, Now, out _));
    }

    /// <summary>
    /// Ambiguous formats are refused rather than guessed: reading 05/01/2026 as either the
    /// 5th of January or the 1st of May would silently analyse the wrong day.
    /// </summary>
    [Theory]
    [InlineData("05/01/2026")]
    [InlineData("01-05-2026")]
    public void Ambiguous_formats_are_refused(string value)
    {
        Assert.False(AgentDateResolver.TryResolve(value, Now, out _));
    }
}

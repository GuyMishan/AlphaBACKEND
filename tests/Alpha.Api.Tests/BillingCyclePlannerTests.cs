using Alpha.Api.Services;
using Xunit;

namespace Alpha.Api.Tests;

public sealed class BillingCyclePlannerTests
{
    [Fact]
    public void Previous_monthly_windows_returns_completed_months_oldest_first()
    {
        var now = new DateTimeOffset(2026, 9, 22, 11, 30, 0, TimeSpan.Zero);

        var windows = BillingCyclePlanner.PreviousMonthlyWindows(now, 3);

        Assert.Equal(3, windows.Count);
        Assert.Equal(new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero), windows[0].Start);
        Assert.Equal(new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero), windows[0].End);
        Assert.Equal(new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero), windows[2].Start);
        Assert.Equal(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), windows[2].End);
    }

    [Fact]
    public void Previous_monthly_windows_never_includes_current_partial_month()
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 1, TimeSpan.Zero);

        var window = Assert.Single(BillingCyclePlanner.PreviousMonthlyWindows(now, 1));

        Assert.Equal(new DateTimeOffset(2025, 12, 1, 0, 0, 0, TimeSpan.Zero), window.Start);
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), window.End);
    }

    [Fact]
    public void Previous_monthly_windows_bounds_catch_up_horizon()
    {
        var now = new DateTimeOffset(2026, 9, 22, 0, 0, 0, TimeSpan.Zero);

        Assert.Single(BillingCyclePlanner.PreviousMonthlyWindows(now, 0));
        Assert.Equal(24, BillingCyclePlanner.PreviousMonthlyWindows(now, 100).Count);
    }
}

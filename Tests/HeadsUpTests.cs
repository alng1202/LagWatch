using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LagWatch;
using Xunit;

public class HeadsUpTests
{
    private static HealthProbe P(bool pub, long ms = 20, bool gw = true, long gwMs = 2, bool adapter = true) =>
        new HealthProbe { Time = DateTime.Now, AdapterUp = adapter, GatewayKnown = true, GatewayOk = gw, GatewayMs = gwMs, PublicOk = pub, PublicMs = pub ? ms : 0 };

    private static List<HealthProbe> Many(int n, Func<int, HealthProbe> f) => Enumerable.Range(0, n).Select(f).ToList();

    // MARK: - Evaluator

    [Fact]
    public void HealthyWhenEverythingAnswers() =>
        Assert.Equal(LinkState.Healthy, LinkEvaluator.Evaluate(Many(10, _ => P(true)), 20).State);

    [Fact]
    public void RouterThatNeverAnswersPingsIsNotAProblem() =>
        Assert.Equal(LinkState.Healthy, LinkEvaluator.Evaluate(Many(10, _ => P(true, gw: false)), 20).State);

    [Fact]
    public void ProviderOutageWhenRouterAnswersButInternetDoesNot()
    {
        var s = LinkEvaluator.Evaluate(Many(8, _ => P(true)).Concat(Many(2, _ => P(false))).ToList(), 20);
        Assert.Equal(LinkState.ProviderOutage, s.State);
        Assert.False(s.LocalCause);
    }

    [Fact]
    public void LocalOutageWhenRouterStopsAnswering()
    {
        var s = LinkEvaluator.Evaluate(Many(8, _ => P(true)).Concat(Many(2, _ => P(false, gw: false))).ToList(), 20);
        Assert.Equal(LinkState.LocalOutage, s.State);
        Assert.True(s.LocalCause);
    }

    [Fact]
    public void AdapterDownIsLocalOutage() =>
        Assert.Equal(LinkState.LocalOutage, LinkEvaluator.Evaluate(Many(2, _ => P(false, gw: false, adapter: false)), 20).State);

    [Fact]
    public void LossBeyondTheRouterIsSpottyOnTheProvidersSide()
    {
        var s = LinkEvaluator.Evaluate(Many(10, i => P(i % 4 != 1)), 20); // 3 of 10 lost, never two in a row at the end
        Assert.Equal(LinkState.Spotty, s.State);
        Assert.False(s.LocalCause);
    }

    [Fact]
    public void SlowOrLossyRouterIsSpottyOnThisSide()
    {
        var s = LinkEvaluator.Evaluate(Many(10, i => P(true, gwMs: 90)), 20);
        Assert.Equal(LinkState.Spotty, s.State);
        Assert.True(s.LocalCause);
    }

    [Fact]
    public void LatencyFarAboveUsualIsSpotty() =>
        Assert.Equal(LinkState.Spotty, LinkEvaluator.Evaluate(Many(10, _ => P(true, ms: 260)), 25).State);

    // MARK: - Forecaster

    private static readonly DateTime Now = new DateTime(2026, 11, 17, 18, 40, 0); // a Tuesday

    /// <summary>Full days of 5-minute buckets; <paramref name="bad"/> marks spotty buckets.</summary>
    private static List<HealthBucket> History(int days, Func<DateTime, bool> bad)
    {
        var list = new List<HealthBucket>();
        for (int d = 1; d <= days; d++)
            for (int m = 0; m < 1440; m += 5)
            {
                DateTime t = Now.Date.AddDays(-d).AddMinutes(m);
                list.Add(new HealthBucket { Start = t, Probes = 10, State = bad(t) ? LinkState.Spotty : LinkState.Healthy });
            }
        return list;
    }

    [Fact]
    public void NotReadyBeforeThreeDays()
    {
        var outlook = RiskForecaster.Forecast(History(2, _ => false), Now, 12);
        Assert.False(outlook.Ready);
        Assert.Empty(outlook.Windows);
    }

    [Fact]
    public void FlagsTheTimeThatIsSpottyOnMostSameWeekdays()
    {
        // Spotty 7–8 PM on Tuesdays only, for four weeks.
        var history = History(28, t => t.DayOfWeek == DayOfWeek.Tuesday && t.Hour == 19);
        var outlook = RiskForecaster.Forecast(history, Now, 12);
        Assert.True(outlook.Ready);
        var w = Assert.Single(outlook.Windows);
        Assert.Equal(Now.Date.AddHours(19), w.Start);
        Assert.Equal(Now.Date.AddHours(20), w.End);
        Assert.Equal("Tuesdays", w.Basis);
        Assert.Equal(4, w.SpottyDays);
        Assert.True(w.High);
    }

    [Fact]
    public void OneBadEveningIsNotAPattern()
    {
        var history = History(28, t => t.Date == Now.Date.AddDays(-7) && t.Hour == 19);
        Assert.Empty(RiskForecaster.Forecast(history, Now, 12).Windows);
    }

    [Fact]
    public void TodayDoesNotPredictToday()
    {
        var history = History(5, _ => false);
        for (int m = 19 * 60; m < 20 * 60; m += 5)
            history.Add(new HealthBucket { Start = Now.Date.AddMinutes(m), Probes = 10, State = LinkState.ProviderOutage });
        Assert.Empty(RiskForecaster.Forecast(history, Now, 12).Windows);
    }

    [Fact]
    public void FallsBackToWeekdaysWhenFewSameWeekdays()
    {
        // Only 6 days of history: one earlier Tuesday, so weekdays are used. Spotty 7–8 PM on weekdays.
        var history = History(6, t => t.DayOfWeek != DayOfWeek.Saturday && t.DayOfWeek != DayOfWeek.Sunday && t.Hour == 19);
        var w = Assert.Single(RiskForecaster.Forecast(history, Now, 12).Windows);
        Assert.Equal("weekdays", w.Basis);
    }

    [Fact]
    public void HistoryRoundTripsThroughTheFile()
    {
        string path = Path.Combine(Path.GetTempPath(), "lagwatch-health-" + Guid.NewGuid().ToString("N") + ".csv");
        try
        {
            var store = new HealthHistory(path);
            store.Append(new HealthBucket { Start = Now, Provider = "Telefonica Germany GmbH & Co.OHG", Asn = "6805", Probes = 10,
                LossPercent = 20, MedianMs = 31, GatewayLossPercent = 0, State = LinkState.Spotty });
            var b = Assert.Single(store.Load(Now.AddDays(-1)));
            Assert.Equal(LinkState.Spotty, b.State);
            Assert.Equal("6805", b.Asn);
            Assert.Equal("Telefonica Germany GmbH & Co.OHG", b.Provider);
        }
        finally { File.Delete(path); }
    }

    // MARK: - Provider outage feed

    [Fact]
    public void IodaCountsOnlyProblemAlertsNotRecoveries()
    {
        const string json = "{\"data\":[{\"time\":1,\"level\":\"critical\",\"condition\":\"< 0.8\"}," +
                            "{\"time\":2,\"level\":\"warning\"},{\"time\":3,\"level\":\"normal\",\"condition\":\"normal\"}]}";
        Assert.Equal(2, ProviderOutageFeed.CountProblems(json));
        Assert.Equal(0, ProviderOutageFeed.CountProblems("{\"data\":[]}"));
        Assert.Contains("/v2/outages/alerts?entityType=asn&entityCode=6805&", ProviderOutageFeed.AlertsUrl("6805", 1, 2));
    }

    // MARK: - Words

    [Fact]
    public void HeadsUpsSayWhatToDoAndNeverMentionAHelpDesk()
    {
        var w = new RiskWindow { Start = Now.Date.AddHours(19), End = Now.Date.AddHours(20), SpottyDays = 4, ObservedDays = 5, Basis = "Tuesdays" };
        var all = new[]
        {
            HeadsUpText.Before(w),
            HeadsUpText.During(new LinkStatus { State = LinkState.Spotty, LossPercent = 12 }, "Telefonica"),
            HeadsUpText.During(new LinkStatus { State = LinkState.Spotty, LocalCause = true }, ""),
            HeadsUpText.During(new LinkStatus { State = LinkState.ProviderOutage }, ""),
            HeadsUpText.During(new LinkStatus { State = LinkState.LocalOutage, LocalCause = true }, ""),
            HeadsUpText.After(Now, Now.AddMinutes(14), LinkState.Spotty),
            HeadsUpText.ProviderAlert("Telefonica"),
        };
        Assert.Contains("4 of the last 5 Tuesdays", all[0].Body);
        Assert.Contains("Save your work", all[0].Body);
        Assert.Contains("Telefonica", all[1].Body);
        Assert.Contains("your internet provider", all[3].Body);
        Assert.True(all[2].OffersFixes && all[4].OffersFixes);
        Assert.Contains("14 min", all[5].Body);
        foreach (var h in all)
        {
            Assert.DoesNotContain(" IT ", h.Title + " " + h.Body); // case-sensitive: "it" is fine
            foreach (var word in new[] { "help desk", "office", "organization" })
                Assert.DoesNotContain(word, h.Title + " " + h.Body, StringComparison.OrdinalIgnoreCase);
        }
    }
}

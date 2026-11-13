using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using LagWatch;
using Xunit;

public class ForecastTests
{
    private static string WriteHistory(Func<DateTime, int, (bool Ok, double Ms)> sample, DateTime now, int days, string target = "1.1.1.1")
    {
        string path = Path.Combine(Path.GetTempPath(), "lagwatch-forecast-" + Guid.NewGuid().ToString("N") + ".csv");
        var sb = new StringBuilder("timestamp,target,success,latency_ms\n");
        for (int d = 0; d <= days; d++)          // d = 0 is today: it must not count
            for (int m = -14; m <= 14; m += 2)   // 15 samples inside the ±15 min window around "now"
            {
                DateTime t = now.Date.AddDays(-d).Add(now.TimeOfDay).AddMinutes(m);
                var s = sample(t, d);
                sb.Append(t.ToString("o", CultureInfo.InvariantCulture)).Append(",\"").Append(target).Append("\",")
                  .Append(s.Ok ? "true" : "false").Append(',').Append(s.Ok ? s.Ms.ToString(CultureInfo.InvariantCulture) : "").Append('\n');
            }
        File.WriteAllText(path, sb.ToString());
        return path;
    }

    private static readonly DateTime Now = new DateTime(2026, 11, 12, 22, 0, 0);

    [Fact]
    public void ReadyAfterThreeEarlierDays_TodayIgnored()
    {
        // Earlier days are 20 ms; today is 500 ms and must not move the usual value.
        string path = WriteHistory((t, d) => (true, d == 0 ? 500 : 20), Now, 3);
        try
        {
            var snapshot = new LatencyForecaster(path).Build(Now, "1.1.1.1", 6, 30);
            Assert.True(snapshot.Now.Ready);
            Assert.Equal(20, snapshot.Now.MedianMs);
            Assert.Equal(3, snapshot.Now.Days);
            Assert.Equal(12, snapshot.Upcoming.Count); // 6 hours of half-hour slots
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void LearningWithTwoDays()
    {
        string path = WriteHistory((t, d) => (true, 20), Now, 2);
        try
        {
            var now = new LatencyForecaster(path).Build(Now, "1.1.1.1", 6, 30).Now;
            Assert.False(now.Ready);
            Assert.Equal(2, now.Days);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void TypicalRangeAndFailures()
    {
        // Each day alternates 10 / 30 ms with one timeout.
        int i = 0;
        string path = WriteHistory((t, d) => { i++; return i % 15 == 0 ? (false, 0) : (true, i % 2 == 0 ? 10 : 30); }, Now, 4);
        try
        {
            var now = new LatencyForecaster(path).Build(Now, "1.1.1.1", 6, 30).Now;
            Assert.True(now.Ready);
            Assert.InRange(now.LowMs, 10, now.MedianMs);
            Assert.InRange(now.HighMs, now.MedianMs, 30);
            Assert.True(now.FailurePercent > 0 && now.FailurePercent < 10);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void OtherTargetsDoNotMix()
    {
        string path = WriteHistory((t, d) => (true, 20), Now, 3, "8.8.8.8");
        try { Assert.False(new LatencyForecaster(path).Build(Now, "1.1.1.1", 6, 30).Now.Ready); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void MissingFileIsLearningNotAnError()
    {
        var snapshot = new LatencyForecaster(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".csv"))
            .Build(Now, "1.1.1.1", 6, 30);
        Assert.False(snapshot.Now.Ready);
        Assert.All(snapshot.Upcoming, s => Assert.False(s.Ready));
    }
}

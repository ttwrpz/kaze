using System.Text.Json;
using Kaze.Core.Models;
using Kaze.Core.Services;
using Kaze.Core.Weather;
using Microsoft.Extensions.Time.Testing;

namespace Kaze.Core.Tests;

public class TemperatureHistoryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kaze-tests-" + Guid.NewGuid().ToString("N"));
    private static readonly AcState Unit = new() { Id = "1", FamilyId = "f", Name = "Bed", PowerOn = true, TargetTemperature = 25, RoomTemperature = 28 };

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void Throttles_until_interval_or_change()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 22, 10, 0, 0, TimeSpan.Zero));
        var h = new TemperatureHistory(_dir, time);
        Assert.True(h.Record(Unit));
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.False(h.Record(Unit with { RoomTemperature = 27.5 }));
        Assert.True(h.Record(Unit with { TargetTemperature = 26 }));
        time.Advance(TimeSpan.FromMinutes(6));
        Assert.True(h.Record(Unit with { TargetTemperature = 26, RoomTemperature = 27 }));
        Assert.False(h.Record(Unit with { Online = false }));

        var again = new TemperatureHistory(_dir, time);
        var samples = again.Query(Unit.Key, time.GetUtcNow().AddHours(-1), time.GetUtcNow());
        Assert.Equal(3, samples.Count);
        Assert.Equal(27, samples[^1].Room);
        Assert.Equal(26, samples[^1].Target);
        Assert.Empty(again.Query("other/unit", time.GetUtcNow().AddHours(-1), time.GetUtcNow()));
    }

    [Fact]
    public void Query_spans_months_and_prune_removes_old_files()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 7, 31, 23, 58, 0, TimeSpan.Zero));
        var h = new TemperatureHistory(_dir, time) { RetentionDays = 30 };
        h.Record(Unit);
        time.Advance(TimeSpan.FromMinutes(10));
        h.Record(Unit);
        Assert.Equal(2, h.Query(Unit.Key, time.GetUtcNow().AddHours(-1), time.GetUtcNow()).Count);

        time.Advance(TimeSpan.FromDays(70));
        h.Prune();
        Assert.Empty(Directory.GetFiles(_dir, "history-*.csv"));
    }

    [Fact]
    public void Parses_missing_room_temperature()
    {
        var s = TemperatureHistory.Parse("2026-11-22T10:00:00.0000000Z,f/1,,25,1,COOLING");
        Assert.NotNull(s);
        Assert.Null(s!.Room);
        Assert.Equal(25, s.Target);
        Assert.Null(TemperatureHistory.Parse("garbage"));
    }
}

public class SleepCurveTests
{
    private static readonly AcState Cooling = new() { Id = "1", FamilyId = "f", PowerOn = true, Mode = AcModes.Cooling, TargetTemperature = 25 };

    [Fact]
    public void Cooling_relaxes_upwards_then_turns_off()
    {
        var plan = SleepCurvePlanner.Plan(Cooling, new SleepCurveSettings(), AcCapabilities.Default);
        Assert.Equal(4, plan.Count);
        Assert.Equal(FanSpeeds.Lv1, plan[0].Command.FanSpeed);
        Assert.Equal(TimeSpan.FromMinutes(60), plan[1].After);
        Assert.Equal(26, plan[1].Command.TargetTemperature);
        Assert.Equal(27, plan[2].Command.TargetTemperature);
        Assert.False(plan[3].Command.PowerOn);
        Assert.Equal(TimeSpan.FromHours(8), plan[3].After);
    }

    [Fact]
    public void Heating_relaxes_downwards_and_clamps()
    {
        var heat = Cooling with { Mode = AcModes.Heating, TargetTemperature = 17 };
        var plan = SleepCurvePlanner.Plan(heat, new SleepCurveSettings { OffAfterMinutes = null }, AcCapabilities.Default);
        Assert.Equal(16, plan[1].Command.TargetTemperature);
        Assert.Equal(16, plan[2].Command.TargetTemperature);
        Assert.DoesNotContain(plan, p => p.Command.PowerOn == false);
    }

    [Fact]
    public void Steps_after_turn_off_are_dropped_and_fan_mode_skips_temperature()
    {
        var plan = SleepCurvePlanner.Plan(Cooling, new SleepCurveSettings { OffAfterMinutes = 120 }, AcCapabilities.Default);
        Assert.Equal(3, plan.Count);
        Assert.Equal(TimeSpan.FromMinutes(120), plan[^1].After);

        var dry = SleepCurvePlanner.Plan(Cooling with { Mode = AcModes.Dry }, new SleepCurveSettings(), AcCapabilities.Default);
        Assert.Null(dry[0].Command.FanSpeed); // dry only supports AUTO here
        var fan = SleepCurvePlanner.Plan(Cooling with { Mode = AcModes.Fan }, new SleepCurveSettings(), AcCapabilities.Default);
        Assert.Equal(2, fan.Count);
    }
}

public class WeatherTests
{
    [Fact]
    public void Parses_forecast_and_locations()
    {
        using var f = JsonDocument.Parse("""
            {"current":{"time":"2026-11-22T15:00","temperature_2m":31.4,"relative_humidity_2m":62},
             "daily":{"time":["2026-11-22"],"temperature_2m_max":[34.1],"temperature_2m_min":[25.0]}}
            """);
        var w = OpenMeteoClient.ParseForecast(f.RootElement, DateTimeOffset.Now)!;
        Assert.Equal(31.4, w.Temperature);
        Assert.Equal(62, w.Humidity);
        Assert.Equal(34.1, w.TodayMax);

        using var g = JsonDocument.Parse("""
            {"results":[{"name":"Bangkok","latitude":13.75,"longitude":100.5,"country":"Thailand","admin1":"Bangkok"}]}
            """);
        var loc = Assert.Single(OpenMeteoClient.ParseLocations(g.RootElement));
        Assert.Equal("Bangkok, Thailand", loc.Display);

        using var many = JsonDocument.Parse("""
            {"results":[{"name":"Bangkok","latitude":-7.1,"longitude":111.7,"country":"Indonesia","admin1":"East Java","population":0},
                        {"name":"Bangkok","latitude":-7.2,"longitude":111.8,"country":"Indonesia","admin1":"East Java"},
                        {"name":"Bangkok","latitude":13.75,"longitude":100.5,"country":"Thailand","admin1":"Bangkok","population":5104476}]}
            """);
        var ranked = OpenMeteoClient.ParseLocations(many.RootElement);
        Assert.Equal(2, ranked.Count);
        Assert.Equal("Thailand", ranked[0].Country);
        using var empty = JsonDocument.Parse("{}");
        Assert.Empty(OpenMeteoClient.ParseLocations(empty.RootElement));
        Assert.Null(OpenMeteoClient.ParseForecast(empty.RootElement, DateTimeOffset.Now));
    }

    [Theory]
    [InlineData(null, null, 20.0, true)]
    [InlineData(30.0, null, 31.0, true)]
    [InlineData(30.0, null, 29.0, false)]
    [InlineData(null, 18.0, 20.0, false)]
    [InlineData(30.0, null, null, true)]
    public void Conditions(double? above, double? below, double? outdoor, bool expected) =>
        Assert.Equal(expected, WeatherConditions.Allows(above, below, outdoor));
}

public class LocalApiTests
{
    [Fact]
    public void Parses_control_bodies()
    {
        var r = LocalApi.ParseControl("""{"power":"on","mode":"cool","temperature":24.5,"fan":"3","swing":"both"}""");
        Assert.Empty(r.Errors);
        Assert.True(r.Command.PowerOn);
        Assert.Equal(AcModes.Cooling, r.Command.Mode);
        Assert.Equal(24.5, r.Command.TargetTemperature);
        Assert.Equal(FanSpeeds.Lv3, r.Command.FanSpeed);
        Assert.Equal(FanSwings.Both, r.Command.FanSwing);

        Assert.Equal(-1, LocalApi.ParseControl("""{"temperature":"-1"}""").TemperatureDelta);
        Assert.True(LocalApi.ParseControl("""{"power":"toggle"}""").TogglePower);
        Assert.False(LocalApi.ParseControl("""{"power":false}""").Command.PowerOn);
        Assert.NotEmpty(LocalApi.ParseControl("""{"colour":"red"}""").Errors);
        Assert.NotEmpty(LocalApi.ParseControl("not json").Errors);
        Assert.NotEmpty(LocalApi.ParseControl("{}").Errors);
    }

    [Theory]
    [InlineData("GET", "/api", ApiRoute.Info, null)]
    [InlineData("GET", "/api/units", ApiRoute.Units, null)]
    [InlineData("GET", "/api/units/Study%20Room", ApiRoute.Unit, "Study Room")]
    [InlineData("POST", "/api/units/selected/control", ApiRoute.Control, "selected")]
    [InlineData("POST", "/api/units/1/frost-wash", ApiRoute.FrostWash, "1")]
    [InlineData("POST", "/api/units/1/sleep", ApiRoute.Sleep, "1")]
    [InlineData("GET", "/api/units/1/history", ApiRoute.History, "1")]
    [InlineData("POST", "/api/presets/Sleep", ApiRoute.RunPreset, "Sleep")]
    [InlineData("GET", "/api/units/1/control", ApiRoute.NotFound, null)]
    [InlineData("GET", "/", ApiRoute.NotFound, null)]
    public void Routes(string method, string path, ApiRoute expected, string? arg)
    {
        Assert.Equal(expected, LocalApi.Route(method, path, out var a));
        Assert.Equal(arg, a);
    }

    [Fact]
    public void Requires_local_host_and_token()
    {
        Assert.True(LocalApi.IsAuthorized("POST", "localhost:8765", "Bearer abc", null, "abc"));
        Assert.True(LocalApi.IsAuthorized("GET", "127.0.0.1:8765", null, "abc", "abc"));
        Assert.False(LocalApi.IsAuthorized("POST", "127.0.0.1:8765", null, "abc", "abc")); // query token only reads
        Assert.False(LocalApi.IsAuthorized("GET", "evil.example:8765", "Bearer abc", null, "abc"));
        Assert.False(LocalApi.IsAuthorized("GET", "localhost", "Bearer nope", null, "abc"));
        Assert.False(LocalApi.IsAuthorized("GET", "localhost", null, null, "abc"));
        Assert.False(LocalApi.IsAuthorized("GET", "localhost", "Bearer abc", null, ""));

        foreach (var ok in new[] { "127.0.0.1", "::1", "10.1.2.3", "172.20.0.5", "192.168.1.50", "169.254.9.9", "100.100.1.1", "fd12::1", "fe80::1", "::ffff:192.168.1.5" })
            Assert.True(LocalApi.IsHomeNetworkAddress(System.Net.IPAddress.Parse(ok)), ok);
        foreach (var bad in new[] { "8.8.8.8", "172.32.0.1", "100.128.0.1", "2001:db8::1", "::ffff:1.1.1.1" })
            Assert.False(LocalApi.IsHomeNetworkAddress(System.Net.IPAddress.Parse(bad)), bad);
    }

    [Fact]
    public void Finds_units_by_name_key_or_selected()
    {
        var a = new AcState { Id = "1", FamilyId = "f", Name = "Study Room" };
        var b = new AcState { Id = "2", FamilyId = "f", Name = "Bedroom" };
        AcState[] units = [a, b];
        Assert.Same(a, LocalApi.FindUnit(units, "study room", null));
        Assert.Same(b, LocalApi.FindUnit(units, "f/2", null));
        Assert.Same(b, LocalApi.FindUnit(units, "selected", "f/2"));
        Assert.Null(LocalApi.FindUnit(units, "kitchen", null));
    }
}

public class ReminderAndUpdateTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Frost_wash_reminder()
    {
        Assert.True(Reminders.FrostWashDue(null, Now, 28, null));
        Assert.False(Reminders.FrostWashDue(Now.AddDays(-10), Now, 28, null));
        Assert.True(Reminders.FrostWashDue(Now.AddDays(-30), Now, 28, null));
        Assert.False(Reminders.FrostWashDue(Now.AddDays(-30), Now, 28, Now.AddDays(1)));
        Assert.False(Reminders.FrostWashDue(null, Now, 0, null));
    }

    [Fact]
    public void Budget_thresholds_fire_once()
    {
        Assert.Equal([80], Reminders.NewBudgetThresholds(85, 100, [], 80, 100));
        Assert.Equal([100], Reminders.NewBudgetThresholds(120, 100, [80], 80, 100));
        Assert.Empty(Reminders.NewBudgetThresholds(120, 100, [80, 100], 80, 100));
        Assert.Empty(Reminders.NewBudgetThresholds(50, 0, [], 80, 100));
    }

    [Fact]
    public void Parses_github_release()
    {
        const string D = "https://github.com/o/r/releases/download/v1.3.0/";
        var r = UpdateCheck.ParseLatest($$"""
            {"tag_name":"v1.3.0","html_url":"https://github.com/o/r/releases/tag/v1.3.0","draft":false,"prerelease":false,
             "body":"Fixes","assets":[{"name":"Kaze-1.3.0-win-x64.zip","browser_download_url":"{{D}}Kaze-1.3.0-win-x64.zip"},
                                      {"name":"Kaze-Setup-1.3.0.exe","browser_download_url":"{{D}}Kaze-Setup-1.3.0.exe"},
                                      {"name":"Kaze-Setup-1.3.0.exe.sig","browser_download_url":"{{D}}Kaze-Setup-1.3.0.exe.sig"}]}
            """, "o/r")!;
        Assert.Equal(new Version(1, 3, 0), r.Version);
        Assert.Equal(D + "Kaze-Setup-1.3.0.exe", r.InstallerUrl);
        Assert.Equal(D + "Kaze-Setup-1.3.0.exe.sig", r.SignatureUrl);
        Assert.Null(UpdateCheck.ParseLatest("""{"tag_name":"v2.0","prerelease":true}""", "o/r"));
        Assert.True(UpdateCheck.IsNewer(new Version(1, 3, 0), new Version(1, 2, 9, 0)));
        Assert.False(UpdateCheck.IsNewer(new Version(1, 3, 0), new Version(1, 3, 0, 0)));
        Assert.Equal(new Version(1, 2, 0), UpdateCheck.ParseVersion("release-1.2"));
    }

    [Theory]
    [InlineData("https://evil.example/Kaze-Setup-1.3.0.exe")]                                  // another host
    [InlineData("https://github.com/someone/else/releases/download/v1.3.0/Kaze-Setup-1.3.0.exe")] // another repository
    [InlineData("https://github.com/o/r/releases/download/v1.3.0/Other.exe")]                    // name and URL differ
    public void Ignores_setup_assets_from_elsewhere(string url)
    {
        var r = UpdateCheck.ParseLatest($$"""
            {"tag_name":"v1.3.0","html_url":"https://evil.example/page",
             "assets":[{"name":"Kaze-Setup-1.3.0.exe","browser_download_url":"{{url}}"},
                       {"name":"setup.exe","browser_download_url":"https://github.com/o/r/releases/download/v1.3.0/setup.exe"}]}
            """, "o/r")!;
        Assert.Null(r.InstallerUrl);
        Assert.Equal("https://github.com/o/r/releases/", r.PageUrl);
    }

    [Fact]
    public void Release_signature_accepts_only_the_signed_file_version_and_key()
    {
        using var key = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        var publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        var v = new Version(1, 3, 0);
        var data = System.Text.Encoding.UTF8.GetBytes("setup program bytes");
        var sig = ReleaseSignature.Sign(new MemoryStream(data), v, key);

        Assert.True(ReleaseSignature.Verify(new MemoryStream(data), v, sig, publicKey));
        // An old signed setup presented as a newer version (rollback) fails.
        Assert.False(ReleaseSignature.Verify(new MemoryStream(data), new Version(1, 4, 0), sig, publicKey));
        Assert.False(ReleaseSignature.Verify(new MemoryStream(data), v, "not base64!", publicKey));
        // The official key did not make this signature.
        Assert.False(ReleaseSignature.Verify(new MemoryStream(data), v, sig));
        data[0] ^= 1;
        Assert.False(ReleaseSignature.Verify(new MemoryStream(data), v, sig, publicKey));
    }

    [Fact]
    public void Parses_sleep_switch()
    {
        var r = CommandLineRequest.Parse(["--sleep", "--unit", "Bedroom"]);
        Assert.True(r.Sleep);
        Assert.True(r.HasUnitAction);
    }
}

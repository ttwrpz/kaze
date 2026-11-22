using System.Text.Json;
using Kaze.Core.Cloud;
using Kaze.Core.Models;

namespace Kaze.Core.Tests;

public class AirCloudJsonTests
{
    private const string Units = """
    {"data":[{"id":12345,"name":"Bedroom","cloudId":"RAK-X","vendorThingId":"V1","online":true,
      "power":"ON","mode":"COOLING","fanSpeed":"LV3","fanSwing":"VERTICAL","iduTemperature":25.5,
      "roomTemperature":"28","humidity":2147483647,"relativeTemperature":0}]}
    """;

    [Fact]
    public void Parses_units_leniently()
    {
        using var doc = JsonDocument.Parse(Units);
        var u = Assert.Single(AirCloudJson.ParseUnits(doc.RootElement, "9"));
        Assert.Equal("12345", u.Id);
        Assert.Equal("9/12345", u.Key);
        Assert.True(u.PowerOn);
        Assert.Equal(AcModes.Cooling, u.Mode);
        Assert.Equal("LV3", u.FanSpeed);
        Assert.Equal(25.5, u.TargetTemperature);
        Assert.Equal(28, u.RoomTemperature);
        Assert.Equal(AcState.NoHumidityValue, u.HumiditySetting);
    }

    [Fact]
    public void Missing_isOnline_defaults_true_and_isOnline_alias_works()
    {
        using var doc = JsonDocument.Parse("""[{"id":"1","isOnline":false,"power":"OFF"}]""");
        var u = Assert.Single(AirCloudJson.ParseUnits(doc.RootElement, "f"));
        Assert.False(u.Online);
        Assert.False(u.PowerOn);
    }

    [Fact]
    public void Command_payload_is_full_state_merge()
    {
        using var doc = JsonDocument.Parse(Units);
        var u = AirCloudJson.ParseUnits(doc.RootElement, "9")[0];
        var p = AirCloudJson.BuildCommandPayload(u, AcCommand.SetTemperature(24));
        Assert.Equal("ON", (string)p["power"]!);
        Assert.Equal("COOLING", (string)p["mode"]!);
        Assert.Equal("LV3", (string)p["fanSpeed"]!);
        Assert.Equal("VERTICAL", (string)p["fanSwing"]!);
        Assert.Equal("0", (string)p["humidity"]!);
        Assert.Equal(24.0, (double)p["iduTemperature"]!);
        Assert.Equal(0.0, (double)p["relativeTemperature"]!);
    }

    [Fact]
    public void Relative_temperature_only_kept_in_auto()
    {
        var u = new AcState { Id = "1", FamilyId = "f", Mode = AcModes.Auto, RelativeTemperature = 2, HumiditySetting = 50 };
        var p = AirCloudJson.BuildCommandPayload(u, AcCommand.Power(true));
        Assert.Equal(2.0, (double)p["relativeTemperature"]!);
        Assert.Equal("50", (string)p["humidity"]!);
        var p2 = AirCloudJson.BuildCommandPayload(u, AcCommand.SetMode(AcModes.Heating));
        Assert.Equal(0.0, (double)p2["relativeTemperature"]!);
    }

    [Fact]
    public void Parses_family_ids()
    {
        using var doc = JsonDocument.Parse("""[{"familyId":111},{"familyId":"222"},{"familyId":111}]""");
        Assert.Equal(["111", "222"], AirCloudJson.ParseFamilyIds(doc.RootElement));
    }

    [Fact]
    public void Parses_capabilities()
    {
        using var doc = JsonDocument.Parse("""
        [{"cloudId":"A"},{"cloudId":"RAK-X","swing":{"VERTICAL":true,"HORIZONTAL":false},
          "racOperationModes":[
            {"mode":"cooling","minTemperature":18,"maxTemperature":30,"defaultTemperature":25,
             "enableSettings":{"temperature":true,"fan":true,"swing":true},
             "enableFanSpeed":{"LV2":true,"AUTO":true,"LV1":false,"LV5":true},"defaultFanSpeed":{"AUTO":true}},
            {"mode":"FAN","temperatureSetting":"RELATIVE","enableSettings":{"temperature":false}}]}]
        """);
        var e = AirCloudJson.FindConfiguration(doc.RootElement, "RAK-X");
        Assert.NotNull(e);
        var caps = AirCloudJson.ParseCapabilities(e.Value);
        Assert.Equal(["OFF", "VERTICAL"], caps.Swings);
        Assert.Equal(["COOLING", "FAN"], caps.SupportedModes);
        var cool = caps.For("COOLING");
        Assert.Equal(18, cool.MinTemperature);
        Assert.Equal(["AUTO", "LV2", "LV5"], cool.FanSpeeds);
        Assert.Equal("AUTO", cool.DefaultFanSpeed);
        Assert.False(caps.For("FAN").TemperatureEnabled);
        Assert.True(caps.For("FAN").RelativeTemperature);
        Assert.Equal(30, caps.Clamp("COOLING", 35));
    }

    [Fact]
    public void Parses_energy_summary()
    {
        using var doc = JsonDocument.Parse("""
        {"individualRacsData":[{"vendorThingId":"V1","racName":"Bedroom","energyConsumed":12.5,"cost":50}],
         "allRacsData":{"currency":"THB"}}
        """);
        var r = AirCloudJson.ParseEnergySummary(doc.RootElement, new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 22));
        Assert.Equal("THB", r.Currency);
        Assert.Equal(12.5, r.TotalKWh);
    }

    [Fact]
    public void Sign_in_errors_are_mapped()
    {
        Assert.Equal(AirCloudErrorKind.InvalidCredentials,
            AirCloudAuth.MapSignInError(System.Net.HttpStatusCode.BadRequest, """{"errorState":"UNKNOWN_USER"}""").Kind);
        Assert.Equal(AirCloudErrorKind.InvalidCredentials,
            AirCloudAuth.MapSignInError(System.Net.HttpStatusCode.Unauthorized, """{"errorState":"UNKNOWN_USER_OR_PASSWORD"}""").Kind);
        Assert.Equal(AirCloudErrorKind.AccountLocked,
            AirCloudAuth.MapSignInError(System.Net.HttpStatusCode.BadRequest, """{"errorState":"ACCOUNT_LOCKED"}""").Kind);
        Assert.Equal(AirCloudErrorKind.UserNotVerified,
            AirCloudAuth.MapSignInError(System.Net.HttpStatusCode.BadRequest, """{"errorState":"USER_NOT_VERIFIED"}""").Kind);
    }

    [Fact]
    public void Response_bodies_are_redacted()
    {
        var ex = new AirCloudException(AirCloudErrorKind.Http, "x")
        {
            ResponseBody = """{"token":"T","refreshToken":"R","user":{"email":"a@b.c","mobileNumber":"+66"},"errorState":"E"}""",
        };
        Assert.DoesNotContain("\"T\"", ex.ResponseBody);
        Assert.DoesNotContain("a@b.c", ex.ResponseBody);
        Assert.DoesNotContain("+66", ex.ResponseBody);
        Assert.Contains("\"E\"", ex.ResponseBody);
        Assert.Equal("[non-JSON body redacted]", SensitiveData.Redact("token=abc"));
    }

    [Fact]
    public void Sign_in_error_exposes_error_state()
    {
        var ex = AirCloudAuth.MapSignInError(System.Net.HttpStatusCode.Unauthorized, """{"errorState":"UNKNOWN_USER_OR_PASSWORD"}""");
        Assert.Equal("UNKNOWN_USER_OR_PASSWORD", ex.ErrorState);
        Assert.Contains("UNKNOWN_USER_OR_PASSWORD", ex.Message);
    }

    [Fact]
    public void Parses_frost_wash_and_error_status_from_real_shape()
    {
        using var doc = JsonDocument.Parse("""
        {"data":[{"id":158306,"name":"Study Room","power":"ON","mode":"COOLING","iduTemperature":25.0,
          "iduFrostWashStatus":{"active":true,"priority":8,"subCategory":null,"errorCode":null},
          "errorStatus":{"active":true,"priority":4,"subCategory":"E1","errorCode":"07"}}]}
        """);
        var u = Assert.Single(AirCloudJson.ParseUnits(doc.RootElement, "164073"));
        Assert.True(u.FrostWashSupported);
        Assert.True(u.FrostWashActive);
        Assert.True(u.ErrorActive);
        Assert.Equal("07", u.ErrorCode);
        var body = AirCloudJson.BuildFrostWashPayload(u);
        Assert.Equal(158306, (int)body["id"]!);
    }

    [Fact]
    public void No_error_status_means_no_fault()
    {
        using var doc = JsonDocument.Parse("""[{"id":1,"errorStatus":{"active":false,"subCategory":"NO_ERROR"}}]""");
        var u = Assert.Single(AirCloudJson.ParseUnits(doc.RootElement, "f"));
        Assert.False(u.ErrorActive);
        Assert.Null(u.ErrorCode);
        Assert.False(u.FrostWashSupported);
    }

    [Fact]
    public void Parses_energy_racs()
    {
        using var doc = JsonDocument.Parse("""{"allRacList":[{"racId":158306,"racName":"Study Room","isSelected":false,"isEnergyConsumptionSupported":true}]}""");
        var r = Assert.Single(AirCloudJson.ParseEnergyRacs(doc.RootElement));
        Assert.Equal("158306", r.RacId);
        Assert.True(r.Supported);
        Assert.False(r.Selected);
    }
}

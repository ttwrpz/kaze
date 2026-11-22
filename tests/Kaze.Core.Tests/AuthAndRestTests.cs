using System.Net;
using System.Text;
using Kaze.Core.Cloud;
using Kaze.Core.Models;
using Microsoft.Extensions.Time.Testing;

namespace Kaze.Core.Tests;

public class AuthAndRestTests
{
    private sealed class FakeHandler(Func<HttpRequestMessage, int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Req, string? Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            Requests.Add((request, body));
            return respond(request, Requests.Count);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string json) =>
        new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static string Path(HttpRequestMessage r) => r.RequestUri!.AbsolutePath;

    [Fact]
    public async Task Sign_in_uses_email_field_and_caches_token()
    {
        var handler = new FakeHandler((r, _) => Path(r) == "/iam/auth/sign-in"
            ? Json(HttpStatusCode.OK, """{"token":"T1","refreshToken":"R1"}""")
            : Json(HttpStatusCode.OK, "[]"));
        var time = new FakeTimeProvider();
        var auth = new AirCloudAuth(new HttpClient(handler), new AirCloudOptions(), () => new("me@x.com", "pw"), time);

        Assert.Equal("T1", await auth.GetAccessTokenAsync());
        Assert.Equal("T1", await auth.GetAccessTokenAsync());
        Assert.Single(handler.Requests);
        Assert.Contains("\"email\":\"me@x.com\"", handler.Requests[0].Body);
        Assert.Equal("okhttp/4.9.1", handler.Requests[0].Req.Headers.UserAgent.ToString());
    }

    [Fact]
    public async Task Phone_login_strips_formatting()
    {
        var handler = new FakeHandler((_, _) => Json(HttpStatusCode.OK, """{"token":"T","refreshToken":"R"}"""));
        var auth = new AirCloudAuth(new HttpClient(handler), new AirCloudOptions(), () => new("+66 81-234 5678", "pw"));
        await auth.GetAccessTokenAsync();
        Assert.Contains("\"mobileNumber\":\"+66812345678\"", handler.Requests[0].Body);
    }

    [Fact]
    public async Task Stale_token_is_refreshed_with_refresh_header()
    {
        var handler = new FakeHandler((r, _) => Path(r) switch
        {
            "/iam/auth/sign-in" => Json(HttpStatusCode.OK, """{"token":"T1","refreshToken":"R1"}"""),
            "/iam/auth/refresh-token" => Json(HttpStatusCode.OK, """{"token":"T2","refreshToken":"R2"}"""),
            _ => Json(HttpStatusCode.NotFound, ""),
        });
        var time = new FakeTimeProvider();
        var auth = new AirCloudAuth(new HttpClient(handler), new AirCloudOptions(), () => new("me@x.com", "pw"), time);
        await auth.GetAccessTokenAsync();
        time.Advance(TimeSpan.FromMinutes(6));
        Assert.Equal("T2", await auth.GetAccessTokenAsync());
        var refresh = handler.Requests[1].Req;
        Assert.Equal("Bearer R1", refresh.Headers.Authorization!.ToString());
        Assert.Equal("true", refresh.Headers.GetValues("isRefreshToken").Single());
    }

    [Fact]
    public async Task Failed_refresh_falls_back_to_sign_in()
    {
        var signIns = 0;
        var handler = new FakeHandler((r, _) => Path(r) switch
        {
            "/iam/auth/sign-in" => Json(HttpStatusCode.OK, $$"""{"token":"T{{++signIns}}","refreshToken":"R"}"""),
            "/iam/auth/refresh-token" => Json(HttpStatusCode.Unauthorized, "{}"),
            _ => Json(HttpStatusCode.NotFound, ""),
        });
        var time = new FakeTimeProvider();
        var auth = new AirCloudAuth(new HttpClient(handler), new AirCloudOptions(), () => new("me@x.com", "pw"), time);
        await auth.GetAccessTokenAsync();
        time.Advance(TimeSpan.FromMinutes(6));
        Assert.Equal("T2", await auth.GetAccessTokenAsync());
    }

    [Fact]
    public async Task Malformed_token_response_is_rejected()
    {
        var handler = new FakeHandler((_, _) => Json(HttpStatusCode.OK, """{"token":"only"}"""));
        var auth = new AirCloudAuth(new HttpClient(handler), new AirCloudOptions(), () => new("me@x.com", "pw"));
        var ex = await Assert.ThrowsAsync<AirCloudException>(() => auth.GetAccessTokenAsync());
        Assert.Equal(AirCloudErrorKind.Protocol, ex.Kind);
        Assert.False(auth.HasToken);
    }

    [Fact]
    public async Task Rest_retries_once_after_401_with_new_token()
    {
        var putCount = 0;
        var handler = new FakeHandler((r, _) =>
        {
            if (Path(r) == "/iam/auth/sign-in") return Json(HttpStatusCode.OK, """{"token":"T1","refreshToken":"R1"}""");
            if (Path(r) == "/iam/auth/refresh-token") return Json(HttpStatusCode.OK, """{"token":"T2","refreshToken":"R2"}""");
            putCount++;
            return r.Headers.Authorization!.Parameter == "T1" ? Json(HttpStatusCode.Unauthorized, "{}") : Json(HttpStatusCode.OK, "{}");
        });
        var http = new HttpClient(handler);
        var opts = new AirCloudOptions();
        var auth = new AirCloudAuth(http, opts, () => new("me@x.com", "pw"));
        var rest = new AirCloudRestClient(http, opts, auth);

        var unit = new AcState { Id = "55", FamilyId = "9", PowerOn = true };
        await rest.SendCommandAsync(unit, AcCommand.SetTemperature(24));

        Assert.Equal(2, putCount);
        var put = handler.Requests.Last().Req;
        Assert.Equal(HttpMethod.Put, put.Method);
        Assert.Equal("/rac/basic-idu-control/general-control-command-status/55", put.RequestUri!.AbsolutePath);
        Assert.Equal("?familyId=9", put.RequestUri.Query);
    }

    [Fact]
    public async Task Rate_limit_reports_retry_after()
    {
        var handler = new FakeHandler((r, _) =>
        {
            if (Path(r) == "/iam/auth/sign-in") return Json(HttpStatusCode.OK, """{"token":"T1","refreshToken":"R1"}""");
            var resp = Json(HttpStatusCode.TooManyRequests, "{}");
            resp.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(1800));
            return resp;
        });
        var http = new HttpClient(handler);
        var opts = new AirCloudOptions();
        var rest = new AirCloudRestClient(http, opts, new AirCloudAuth(http, opts, () => new("a@b.c", "p")));
        var ex = await Assert.ThrowsAsync<AirCloudException>(() => rest.GetEnergyAllRacsRawAsync("1"));
        Assert.Equal(AirCloudErrorKind.RateLimited, ex.Kind);
        Assert.Equal(TimeSpan.FromSeconds(1800), ex.RetryAfter);
    }
}

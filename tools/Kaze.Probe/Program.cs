// Kaze.Probe talks to the airCloud Go cloud to verify the API against a real account.
//
//   dotnet run --project tools/Kaze.Probe                 dump families, units, capabilities, energy
//   dotnet run --project tools/Kaze.Probe -- watch 120    stream state frames for 120 s
//   dotnet run --project tools/Kaze.Probe -- set --temp 25 [--power on|off] [--mode COOLING] [--fan AUTO] [--swing OFF]
//
// Credentials: env KAZE_LOGIN / KAZE_PASSWORD, else the login saved by the Kaze app
// (Windows Credential Manager), else prompted (password hidden).
// Output files go to ./probe-output (tokens are never written).

using System.Globalization;
using System.Text.Json;
using Kaze.Core.Cloud;
using Kaze.Core.Models;
using Microsoft.Extensions.Logging;

var args2 = args.ToList();
var verb = args2.FirstOrDefault()?.ToLowerInvariant() ?? "dump";

using var loggerFactory = LoggerFactory.Create(b => b.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Information));

var login = Environment.GetEnvironmentVariable("KAZE_LOGIN");
var password = Environment.GetEnvironmentVariable("KAZE_PASSWORD");
if (string.IsNullOrWhiteSpace(login) && OperatingSystem.IsWindows())
{
    try
    {
        var saved = Meziantou.Framework.Win32.CredentialManager.ReadCredential("Kaze/airCloudGo");
        if (saved is { UserName: { Length: > 0 }, Password: { Length: > 0 } })
        {
            login = saved.UserName;
            password = saved.Password;
            Console.WriteLine("Using the login saved by the Kaze app.");
        }
    }
    catch { /* no saved login */ }
}
if (string.IsNullOrWhiteSpace(login))
{
    Console.Write("airCloud Go email or phone: ");
    login = Console.ReadLine()?.Trim();
}
if (string.IsNullOrEmpty(password))
{
    Console.Write("Password: ");
    password = ReadPassword();
}
if (string.IsNullOrWhiteSpace(login) || string.IsNullOrEmpty(password))
{
    Console.Error.WriteLine("Login and password are required.");
    return 2;
}

var options = new AirCloudOptions();
using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
var creds = new AirCloudCredentials(login, password);
var auth = new AirCloudAuth(http, options, () => creds, logger: loggerFactory.CreateLogger<AirCloudAuth>());
var rest = new AirCloudRestClient(http, options, auth, loggerFactory.CreateLogger<AirCloudRestClient>());
await using var backend = new AirCloudBackend(options, auth, rest, loggerFactory.CreateLogger<AirCloudBackend>());

var outDir = Path.Combine(Environment.CurrentDirectory, "probe-output");
Directory.CreateDirectory(outDir);
var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
var pretty = new JsonSerializerOptions { WriteIndented = true };

string Pretty(string json)
{
    try { using var d = JsonDocument.Parse(json); return JsonSerializer.Serialize(d.RootElement, pretty); }
    catch { return json; }
}

void Save(string name, string content)
{
    var path = Path.Combine(outDir, $"{stamp}-{name}");
    File.WriteAllText(path, content);
    Console.WriteLine($"  saved {path}");
}

try
{
    Console.WriteLine("\n== Sign in");
    await auth.SignInAsync();
    Console.WriteLine("  OK");

    Console.WriteLine("\n== Families (rac/location-controls/status)");
    var familiesRaw = await rest.SendRawAsync(HttpMethod.Get, AirCloudRestClient.FamiliesPath, null);
    Save("families.json", Pretty(familiesRaw));
    using (var fd = JsonDocument.Parse(familiesRaw))
        Console.WriteLine("  familyIds: " + string.Join(", ", AirCloudJson.ParseFamilyIds(fd.RootElement)));

    switch (verb)
    {
        case "watch":
            await Watch(args2.Count > 1 && int.TryParse(args2[1], out var secs) ? secs : 120);
            break;
        case "set":
            await Set();
            break;
        default:
            await Dump();
            break;
    }
    return 0;
}
catch (AirCloudException ex)
{
    Console.Error.WriteLine($"\nairCloud error [{ex.Kind}] {ex.Message}");
    if (ex.HttpStatus is not null) Console.Error.WriteLine($"  HTTP {ex.HttpStatus}");
    if (!string.IsNullOrEmpty(ex.ResponseBody)) Console.Error.WriteLine($"  body: {ex.ResponseBody}");
    return 1;
}

async Task<IReadOnlyList<AcState>> Snapshot()
{
    var frames = new List<string>();
    void OnFrame(StompFrame f) => frames.Add($"--- {f.Command} {string.Join(" ", f.Headers.Where(h => !h.Key.Contains("auth", StringComparison.OrdinalIgnoreCase)).Select(h => $"{h.Key}={h.Value}"))}\n{Pretty(f.Body)}");
    backend.FrameReceived += OnFrame;
    try
    {
        var units = await backend.FetchSnapshotAsync();
        Save("ws-frames.txt", string.Join("\n\n", frames));
        return units;
    }
    finally { backend.FrameReceived -= OnFrame; }
}

async Task Dump()
{
    Console.WriteLine("\n== Units (STOMP notification snapshot)");
    var units = await Snapshot();
    if (units.Count == 0) Console.WriteLine("  (no units reported)");
    foreach (var u in units)
        Console.WriteLine($"  [{u.Key}] {u.Summary}\n     cloudId={u.CloudId} vendorThingId={u.VendorThingId} online={u.Online} power={(u.PowerOn ? "ON" : "OFF")} mode={u.Mode} fan={u.FanSpeed} swing={u.FanSwing} set={u.TargetTemperature} room={u.RoomTemperature} humidity={u.HumiditySetting} relative={u.RelativeTemperature}");

    var cloudIds = units.Select(u => u.CloudId).OfType<string>().Distinct().ToList();
    if (cloudIds.Count > 0)
    {
        Console.WriteLine("\n== Capabilities (rac/model-wise/rac-configuration)");
        var cfgRaw = await rest.GetConfigurationRawAsync(cloudIds);
        Save("rac-configuration.json", Pretty(cfgRaw));
        using var cd = JsonDocument.Parse(cfgRaw);
        foreach (var u in units)
        {
            if (AirCloudJson.FindConfiguration(cd.RootElement, u.CloudId) is not { } e) continue;
            var caps = AirCloudJson.ParseCapabilities(e);
            Console.WriteLine($"  {u.Name}: swings [{string.Join(",", caps.Swings)}]");
            foreach (var m in caps.SupportedModes.Select(caps.For))
                Console.WriteLine($"    {m.Mode,-8} {m.MinTemperature}-{m.MaxTemperature}° (def {m.DefaultTemperature}{(m.RelativeTemperature ? ", relative" : "")}) temp={m.TemperatureEnabled} fan={m.FanEnabled} swing={m.SwingEnabled} fans=[{string.Join(",", m.FanSpeeds)}] defFan={m.DefaultFanSpeed}");
        }
    }

    foreach (var fid in backend.FamilyIds)
    {
        Console.WriteLine($"\n== Energy (family {fid})");
        try
        {
            var today = DateOnly.FromDateTime(DateTime.Today);
            var from = new DateOnly(today.Year, today.Month, 1);
            var sumRaw = await rest.GetEnergySummaryRawAsync(fid, from, today);
            Save($"energy-summary-{fid}.json", Pretty(sumRaw));
            using var sd = JsonDocument.Parse(sumRaw);
            var rep = AirCloudJson.ParseEnergySummary(sd.RootElement, from, today);
            foreach (var u in rep.Units) Console.WriteLine($"  {u.Name}: {u.KWh?.ToString() ?? "?"} kWh, cost {u.Cost?.ToString() ?? "?"} {rep.Currency}");
            var allRaw = await rest.GetEnergyAllRacsRawAsync(fid);
            Save($"energy-all-racs-{fid}.json", Pretty(allRaw));
        }
        catch (AirCloudException ex)
        {
            Console.WriteLine($"  energy not available: {ex.Message}");
        }
    }
}

async Task Watch(int seconds)
{
    Console.WriteLine($"\n== Watching state pushes for {seconds}s (change something in the phone app)");
    var log = new List<string>();
    backend.FrameReceived += f =>
    {
        var line = $"{DateTime.Now:HH:mm:ss} {f.Command} {f.Header("destination")}";
        Console.WriteLine(line);
        log.Add(line + "\n" + Pretty(f.Body));
    };
    backend.UnitsReported += us => { foreach (var u in us) Console.WriteLine("   " + u.Summary); };
    backend.StatusChanged += (s, d) => Console.WriteLine($"   [status] {s} {d}");
    await backend.StartAsync();
    await Task.Delay(TimeSpan.FromSeconds(seconds));
    await backend.StopAsync();
    Save("watch.txt", string.Join("\n\n", log));
}

async Task Set()
{
    string? Opt(string name)
    {
        var i = args2.IndexOf(name);
        return i >= 0 && i + 1 < args2.Count ? args2[i + 1] : null;
    }
    var cmd = new AcCommand
    {
        PowerOn = Opt("--power")?.ToLowerInvariant() switch { "on" => true, "off" => false, _ => null },
        Mode = Opt("--mode")?.ToUpperInvariant(),
        FanSpeed = Opt("--fan")?.ToUpperInvariant(),
        FanSwing = Opt("--swing")?.ToUpperInvariant(),
        TargetTemperature = Opt("--temp") is { } t ? double.Parse(t, CultureInfo.InvariantCulture) : null,
    };
    var units = await Snapshot();
    var target = Opt("--unit") is { } id ? units.FirstOrDefault(u => u.Id == id) : units.FirstOrDefault();
    if (target is null) { Console.WriteLine("No unit found."); return; }
    Console.WriteLine($"\n== Sending to {target.Name}: {cmd}");
    Console.WriteLine("  payload: " + AirCloudJson.BuildCommandPayload(target, cmd).ToJsonString());
    await backend.SendAsync(target, cmd);
    Console.WriteLine("  sent. Waiting 8s and re-reading state...");
    await Task.Delay(8000);
    foreach (var u in await Snapshot()) Console.WriteLine("  " + u.Summary);
}

static string ReadPassword()
{
    if (Console.IsInputRedirected) return Console.ReadLine() ?? "";
    var chars = new List<char>();
    while (true)
    {
        var k = Console.ReadKey(intercept: true);
        if (k.Key == ConsoleKey.Enter) break;
        if (k.Key == ConsoleKey.Backspace) { if (chars.Count > 0) chars.RemoveAt(chars.Count - 1); continue; }
        if (!char.IsControl(k.KeyChar)) chars.Add(k.KeyChar);
    }
    Console.WriteLine();
    return new string([.. chars]);
}

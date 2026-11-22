using System.Diagnostics;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.RegularExpressions;
using Kaze.App.Helpers;
using Kaze.Core.Services;
using Microsoft.Extensions.Logging;

namespace Kaze.App.Services;

/// <summary>
/// Checks a GitHub repository's latest release and installs it with the Kaze setup program.
/// Nothing is sent except the request for the public release list.
/// </summary>
public sealed partial class UpdateService(HttpClient http, SettingsStore settings, NotificationService toasts, ILogger<UpdateService> log) : IDisposable
{
    private Timer? _timer;

    public static Version CurrentVersion { get; } = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);

    public ReleaseInfo? Available { get; private set; }
    public string? Status { get; private set; }
    public bool Busy { get; private set; }

    /// <summary>Raised (possibly on a background thread) when the state changes.</summary>
    public event Action? Changed;

    [GeneratedRegex(@"^[A-Za-z0-9-]{1,39}/[A-Za-z0-9._-]{1,100}$")]
    private static partial Regex RepoPattern();

    public static bool IsValidRepository(string? repo) => repo is not null && RepoPattern().IsMatch(repo.Trim());

    public void Start() =>
        _timer = new Timer(_ =>
        {
            var u = settings.Current.Updates;
            if (u.CheckAutomatically && IsValidRepository(u.Repository)
                && (u.LastCheck is not { } last || DateTimeOffset.Now - last > TimeSpan.FromHours(20)))
                _ = CheckAsync(userInitiated: false);
        }, null, TimeSpan.FromMinutes(1), TimeSpan.FromHours(6));

    public async Task CheckAsync(bool userInitiated)
    {
        var repo = settings.Current.Updates.Repository?.Trim();
        if (!IsValidRepository(repo))
        {
            Status = L.T("Set the GitHub repository that publishes Kaze releases first.");
            Changed?.Invoke();
            return;
        }
        Busy = true;
        Status = L.T("Checking for updates...");
        Changed?.Invoke();
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{repo}/releases/latest");
            req.Headers.UserAgent.Add(new ProductInfoHeaderValue("Kaze-Updater", CurrentVersion.ToString(3)));
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            using var resp = await http.SendAsync(req);
            settings.Update(s => s.Updates.LastCheck = DateTimeOffset.Now);
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                Available = null;
                Status = L.T("No releases published yet.");
                return;
            }
            resp.EnsureSuccessStatusCode();
            var release = UpdateCheck.ParseLatest(await resp.Content.ReadAsStringAsync(), repo!);
            if (release is null || !UpdateCheck.IsNewer(release.Version, CurrentVersion))
            {
                Available = null;
                Status = L.F("Kaze {0} is the latest version.", CurrentVersion.ToString(3));
                return;
            }
            Available = release;
            Status = L.F("Version {0} is available.", release.Version.ToString(3));
            if (userInitiated || settings.Current.Updates.SkippedVersion != release.Tag)
                toasts.Show(L.F("Kaze {0} is available", release.Version.ToString(3)),
                    L.F("You have {0}.", CurrentVersion.ToString(3)), "update",
                    new ToastButton(L.T("Install"), ToastActions.InstallUpdate),
                    new ToastButton(L.T("Skip this version"), ToastActions.SkipUpdate));
        }
        catch (Exception ex)
        {
            log.LogInformation("Update check failed: {Error}", ex.Message);
            Status = L.F("Could not check for updates: {0}", ex.Message);
        }
        finally
        {
            Busy = false;
            Changed?.Invoke();
        }
    }

    public void SkipAvailable()
    {
        if (Available is { } a) settings.Update(s => s.Updates.SkippedVersion = a.Tag);
    }

    /// <summary>
    /// Downloads the setup program and its signature, and runs the setup only when the signature
    /// matches the official release key. Returns true when the app should exit.
    /// </summary>
    public async Task<bool> InstallAsync()
    {
        if (Available is not { } release) return false;
        if (release.InstallerUrl is not { } url || release.SignatureUrl is not { } sigUrl)
        {
            // Nothing that can be verified: let the user download it from the release page.
            Status = L.T("This release can't be installed automatically. Download it from the release page.");
            Changed?.Invoke();
            OpenReleasePage(release);
            return false;
        }
        Busy = true;
        Status = L.F("Downloading {0}...", release.Version.ToString(3));
        Changed?.Invoke();
        try
        {
            var signature = await DownloadTextAsync(sigUrl, maxBytes: 1024);
            // A fresh folder of our own, so nothing else can swap the file between checking and running it.
            var dir = Directory.CreateTempSubdirectory("Kaze-Update-").FullName;
            var path = Path.Combine(dir, $"Kaze-Setup-{release.Version.ToString(3)}.exe");
            using (var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
            {
                resp.EnsureSuccessStatusCode();
                if (!IsTrustedHost(resp.RequestMessage?.RequestUri)) throw new InvalidDataException("The download was redirected to an unexpected host.");
                await using var file = File.Create(path);
                await resp.Content.CopyToAsync(file);
            }

            // Keep the file open (read only, no writers) while checking it and starting it.
            await using var check = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            // The signature covers the version too, and only newer versions get this far, so an older
            // signed setup can't be passed off as an update.
            if (!UpdateCheck.IsNewer(release.Version, CurrentVersion) || !ReleaseSignature.Verify(check, release.Version, signature))
            {
                log.LogWarning("Update {Version} failed the signature check", release.Version);
                Status = L.T("The downloaded update failed the signature check, so it was not installed.");
                return false;
            }
            log.LogInformation("Starting setup for {Version}", release.Version);
            Process.Start(new ProcessStartInfo(path, "/silent /relaunch") { UseShellExecute = false });
            return true;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Update download failed");
            Status = L.F("Could not download the update: {0}", ex.Message);
            return false;
        }
        finally
        {
            Busy = false;
            Changed?.Invoke();
        }
    }

    public static void OpenReleasePage(ReleaseInfo release)
    {
        // ParseLatest only keeps github.com release pages of the configured repository.
        if (!release.PageUrl.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase)) return;
        try { Process.Start(new ProcessStartInfo(release.PageUrl) { UseShellExecute = true }); }
        catch (Exception) { }
    }

    private async Task<string> DownloadTextAsync(string url, int maxBytes)
    {
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();
        if (!IsTrustedHost(resp.RequestMessage?.RequestUri)) throw new InvalidDataException("The download was redirected to an unexpected host.");
        await using var stream = await resp.Content.ReadAsStreamAsync();
        var buffer = new byte[maxBytes + 1];
        var read = 0;
        int n;
        while (read < buffer.Length && (n = await stream.ReadAsync(buffer.AsMemory(read))) > 0) read += n;
        if (read > maxBytes) throw new InvalidDataException("The signature file is too large.");
        return System.Text.Encoding.ASCII.GetString(buffer, 0, read);
    }

    /// <summary>GitHub serves release files from github.com and redirects to its download hosts.</summary>
    private static bool IsTrustedHost(Uri? u) =>
        u is not null && u.Scheme == Uri.UriSchemeHttps
        && (u.Host == "github.com" || u.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase));

    public void Dispose() => _timer?.Dispose();
}

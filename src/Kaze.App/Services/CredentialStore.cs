using Kaze.Core.Cloud;
using Meziantou.Framework.Win32;

namespace Kaze.App.Services;

/// <summary>
/// Saved airCloud Go accounts. Each password lives in Windows Credential Manager (encrypted per user)
/// under its own target. The account list and the active account are kept in settings.
/// </summary>
public sealed class CredentialStore(SettingsStore settings)
{
    private const string Prefix = "Kaze/airCloudGo/";
    private readonly Dictionary<string, AirCloudCredentials?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync = new();

    public static bool SameLogin(string? a, string? b) => string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>Saved logins, in the order they were added.</summary>
    public IReadOnlyList<string> Accounts => settings.Current.Accounts.ToList();

    /// <summary>The login the app is connected with, or null when signed out.</summary>
    public string? ActiveLogin => settings.Current.Accounts.FirstOrDefault(a => SameLogin(a, settings.Current.Login));

    /// <summary>Credentials of the active account (used by AirCloudAuth).</summary>
    public AirCloudCredentials? Get() => ActiveLogin is { } login ? Read(login) : null;

    public AirCloudCredentials? Read(string login)
    {
        lock (_sync)
        {
            if (_cache.TryGetValue(login, out var hit)) return hit;
            AirCloudCredentials? creds = null;
            try
            {
                var c = CredentialManager.ReadCredential(Target(login));
                if (c is { UserName: { Length: > 0 } u, Password: { Length: > 0 } p }) creds = new AirCloudCredentials(u, p);
            }
            catch { }
            _cache[login] = creds;
            return creds;
        }
    }

    public void Save(AirCloudCredentials creds, bool activate)
    {
        CredentialManager.WriteCredential(Target(creds.Login), creds.Login, creds.Password,
            "Kaze airCloud Go login", CredentialPersistence.LocalMachine);
        lock (_sync) _cache[creds.Login] = creds;
        settings.Update(s =>
        {
            var i = s.Accounts.FindIndex(a => SameLogin(a, creds.Login));
            if (i >= 0) s.Accounts[i] = creds.Login;
            else s.Accounts.Add(creds.Login);
            if (activate) s.Login = creds.Login;
        });
    }

    public void SetActive(string? login) => settings.Update(s => s.Login = login);

    public void Remove(string login)
    {
        try { CredentialManager.DeleteCredential(Target(login)); } catch { }
        lock (_sync) _cache.Remove(login);
        settings.Update(s =>
        {
            s.Accounts.RemoveAll(a => SameLogin(a, login));
            s.AccountUnits.Remove(login.Trim().ToLowerInvariant());
            if (SameLogin(s.Login, login)) s.Login = null;
        });
    }

    private static string Target(string login) => Prefix + login.Trim().ToLowerInvariant();
}

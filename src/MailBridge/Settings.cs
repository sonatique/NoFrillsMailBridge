using System.Text.Json;
using MailKit.Security;
using ModelContextProtocol;

namespace MailBridge;

internal sealed class AccountConfig
{
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 993;
    public string Security { get; set; } = "ssl";
    public string User { get; set; } = "";
    public string? Password { get; set; }
    public string? PasswordEnv { get; set; }

    public SecureSocketOptions SocketOptions => Security.ToLowerInvariant() switch
    {
        "starttls" => SecureSocketOptions.StartTls,
        "none" => SecureSocketOptions.None,
        _ => SecureSocketOptions.SslOnConnect,
    };

    public string ResolvePassword() =>
        !string.IsNullOrEmpty(Password) ? Password
        : !string.IsNullOrEmpty(PasswordEnv) && Environment.GetEnvironmentVariable(PasswordEnv) is { Length: > 0 } v ? v
        : throw new McpException($"No password for account '{Name}'.");
}

internal sealed class ConfigFile
{
    public string? SaveDir { get; set; }
    public List<AccountConfig> Accounts { get; set; } = new();
}

internal static class Config
{
    private static readonly Lazy<ConfigFile> Cached = new(Read, LazyThreadSafetyMode.PublicationOnly);

    public static ConfigFile Current => Cached.Value;

    // Accepts "all", an empty value, or a comma separated list of account names.
    public static List<AccountConfig> Resolve(string? names)
    {
        var all = Current.Accounts;
        if (string.IsNullOrWhiteSpace(names) || names.Trim().Equals("all", StringComparison.OrdinalIgnoreCase))
            return all;

        var result = new List<AccountConfig>();
        foreach (var name in names.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var found = all.FirstOrDefault(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                ?? throw new McpException(
                    $"Unknown account '{name}'. Available: {string.Join(", ", all.Select(a => a.Name))}.");
            result.Add(found);
        }

        return result;
    }

    public static AccountConfig ResolveOne(string? name)
    {
        var accounts = Resolve(name);
        return accounts.Count == 1
            ? accounts[0]
            : throw new McpException(
                $"Specify exactly one account. Available: {string.Join(", ", Current.Accounts.Select(a => a.Name))}.");
    }

    private static ConfigFile Read()
    {
        var path = Environment.GetEnvironmentVariable("MAILBRIDGE_CONFIG")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MailBridge", "accounts.json");

        if (!File.Exists(path))
            throw new McpException($"Configuration file not found: {path}");

        ConfigFile? config;
        try
        {
            config = JsonSerializer.Deserialize<ConfigFile>(
                File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip });
        }
        catch (JsonException ex)
        {
            throw new McpException($"Invalid configuration file {path}: {ex.Message}");
        }

        if (config is null)
            throw new McpException("Empty configuration file.");

        if (config.Accounts.Count == 0)
            throw new McpException("No account defined.");

        return config;
    }
}

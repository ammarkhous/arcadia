using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Arcadia.Core.Security;
using Microsoft.Extensions.Logging;

namespace Arcadia.Infrastructure.Security;

/// <summary>
/// Secrets encrypted with Windows DPAPI (CurrentUser scope): only this Windows account on this PC can read them.
/// Stored as a small JSON map of name → base64 ciphertext, separate from settings.json.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiSecretStore(string filePath, ILogger<DpapiSecretStore> logger) : ISecretStore
{
    private static readonly byte[] Entropy = "Arcadia.Secrets.v1"u8.ToArray();
    private readonly Lock _gate = new();
    private Dictionary<string, string>? _cache;

    public event EventHandler? Changed;

    public string? Get(string name)
    {
        lock (_gate)
        {
            var map = Load();
            if (!map.TryGetValue(name, out var cipher)) return null;
            try
            {
                return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(cipher), Entropy, DataProtectionScope.CurrentUser));
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException)
            {
                logger.LogWarning("Secret {Name} could not be decrypted (created by another Windows user?)", name);
                return null;
            }
        }
    }

    public void Set(string name, string? value)
    {
        lock (_gate)
        {
            var map = Load();
            if (string.IsNullOrWhiteSpace(value)) map.Remove(name);
            else map[name] = Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value.Trim()), Entropy, DataProtectionScope.CurrentUser));
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            var temp = filePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(map));
            File.Move(temp, filePath, overwrite: true);
        }
        logger.LogInformation("Secret {Name} {Action}", name, string.IsNullOrWhiteSpace(value) ? "cleared" : "updated");
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private Dictionary<string, string> Load()
    {
        if (_cache != null) return _cache;
        try
        {
            _cache = File.Exists(filePath)
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(filePath)) ?? []
                : [];
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            logger.LogWarning(ex, "Secret store unreadable; starting empty");
            _cache = [];
        }
        return _cache;
    }
}

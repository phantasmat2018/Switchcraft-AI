using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Switchcraft;

/// <summary>The key is protected for the current Windows user and never exported with rules.</summary>
public sealed class ApiKeyStore
{
    private readonly string path;
    private string key = "";
    public string Key => key;
    public bool HasKey => key.Length > 0;
    public ApiKeyStore(string directory)
    {
        path = Path.Combine(directory, "typesafe-key.dat");
        try
        {
            if (File.Exists(path))
            {
                var bytes = ProtectedData.Unprotect(Convert.FromBase64String(File.ReadAllText(path)), null, DataProtectionScope.CurrentUser);
                try { key = Encoding.UTF8.GetString(bytes); }
                finally { CryptographicOperations.ZeroMemory(bytes); }
            }
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or IOException or UnauthorizedAccessException) { }
    }
    public void Save(string value)
    {
        value = value.Trim();
        if (value.Length is < 8 or > 4096 || value.Contains('\r') || value.Contains('\n'))
            throw new ArgumentException("Перевірте формат ключа API.");
        var bytes = Encoding.UTF8.GetBytes(value);
        try { SettingsStore.AtomicWrite(path, Convert.ToBase64String(ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser))); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
        key = value;
    }
    public void Clear() { if (File.Exists(path)) File.Delete(path); key = ""; }
}

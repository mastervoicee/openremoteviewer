using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace OpenRemoteViewer;

public class AppSettings
{
    public string Role { get; set; } = "Client";
    public string IP { get; set; } = "192.168.1.";
    public int Port { get; set; } = 9876;
    public string EncryptedPassword { get; set; } = "";
    public bool AutoStart { get; set; }
    public bool RememberSettings { get; set; }
    public string Language { get; set; } = "en";

    private static string SettingsDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OpenRemoteViewer");

    private static string SettingsPath => Path.Combine(SettingsDir, "settings.json");

    public string GetPassword()
    {
        if (string.IsNullOrEmpty(EncryptedPassword)) return "";
        try
        {
            var encrypted = Convert.FromBase64String(EncryptedPassword);
            var decrypted = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(decrypted);
        }
        catch { return ""; }
    }

    public void SetPassword(string password)
    {
        if (string.IsNullOrEmpty(password))
        {
            EncryptedPassword = "";
            return;
        }
        var encrypted = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(password), null, DataProtectionScope.CurrentUser);
        EncryptedPassword = Convert.ToBase64String(encrypted);
    }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                return JsonSerializer.Deserialize<AppSettings>(json) ?? new();
            }
        }
        catch { }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(SettingsDir);
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
        }
        catch { }
    }

    public void ApplyAutoStart()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", true);
            if (key == null) return;

            if (AutoStart)
            {
                var exePath = Environment.ProcessPath ?? "";
                if (!string.IsNullOrEmpty(exePath))
                    key.SetValue("OpenRemoteViewer", $"\"{exePath}\" --autoconnect");
            }
            else
            {
                key.DeleteValue("OpenRemoteViewer", false);
            }
        }
        catch { }
    }
}

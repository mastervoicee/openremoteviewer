using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace OpenRemoteViewer;

public sealed record UpdateInfo(Version Version, string Url, string Sha256)
{
    public string VersionText => Version.ToString(3);
}

/// <summary>
/// Self-update from GitHub Releases: the latest release must carry an asset named
/// OpenRemoteViewer.exe. The download is accepted only when its SHA-256 matches the
/// digest GitHub reports for that asset.
/// </summary>
public static class Updater
{
    public const string Repo = "mastervoicee/openremoteviewer";
    private const string AssetName = "OpenRemoteViewer.exe";

    // Параметры, с которыми новая копия продолжает работу после обновления.
    // Передаются через окружение дочернего процесса, а не через командную строку,
    // чтобы пароль не был виден в списке процессов.
    public const string ResumeArg = "--resume";
    public const string EnvRole = "ORV_RESUME_ROLE";
    public const string EnvHost = "ORV_RESUME_HOST";
    public const string EnvPort = "ORV_RESUME_PORT";
    public const string EnvPassword = "ORV_RESUME_PASSWORD";

    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(1);

    private static readonly HttpClient Http = CreateHttp();

    public static Version Current { get; } =
        Normalize(Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0));

    public static string CurrentText => Current.ToString(3);

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("OpenRemoteViewer-Updater");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    private static Version Normalize(Version v) =>
        new(v.Major, Math.Max(v.Minor, 0), Math.Max(v.Build, 0));

    /// <summary>Remove the previous exe left behind by the last update.</summary>
    public static void CleanupOldVersion()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return;

        _ = Task.Run(async () =>
        {
            // старый процесс может ещё завершаться — пробуем несколько раз
            for (int i = 0; i < 10; i++)
            {
                try
                {
                    File.Delete(exe + ".old");
                    File.Delete(exe + ".new");
                    return;
                }
                catch { await Task.Delay(1000); }
            }
        });
    }

    /// <summary>Returns the newer release, or null when up to date / offline / release is malformed.</summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await Http.GetAsync(
                $"https://api.github.com/repos/{Repo}/releases/latest", ct);
            if (!resp.IsSuccessStatusCode) return null;

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;

            var tag = root.GetProperty("tag_name").GetString() ?? "";
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var parsed)) return null;
            var version = Normalize(parsed);
            if (version <= Current) return null;

            foreach (var asset in root.GetProperty("assets").EnumerateArray())
            {
                if (asset.GetProperty("name").GetString() != AssetName) continue;

                var url = asset.GetProperty("browser_download_url").GetString();
                var digest = asset.TryGetProperty("digest", out var d) ? d.GetString() : null;

                // Без контрольной суммы обновление не ставим.
                if (string.IsNullOrEmpty(url) || digest == null ||
                    !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                    return null;

                return new UpdateInfo(version, url, digest["sha256:".Length..]);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { }

        return null;
    }

    /// <summary>
    /// Download the release, verify it, swap the running exe and start the new copy.
    /// The caller must exit the application right after this returns.
    /// </summary>
    public static async Task ApplyAndRestartAsync(
        UpdateInfo info, IReadOnlyDictionary<string, string> resume, CancellationToken ct)
    {
        var exe = Environment.ProcessPath
            ?? throw new InvalidOperationException("Executable path is unknown");
        var tmp = exe + ".new";
        var old = exe + ".old";

        using (var dlCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            dlCts.CancelAfter(TimeSpan.FromMinutes(15));
            using var resp = await Http.GetAsync(
                info.Url, HttpCompletionOption.ResponseHeadersRead, dlCts.Token);
            resp.EnsureSuccessStatusCode();
            await using var file = File.Create(tmp);
            await resp.Content.CopyToAsync(file, dlCts.Token);
        }

        string hash;
        await using (var file = File.OpenRead(tmp))
            hash = Convert.ToHexString(await SHA256.HashDataAsync(file, ct));

        if (!hash.Equals(info.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(tmp);
            throw new InvalidDataException("Checksum mismatch");
        }

        // Запущенный exe нельзя перезаписать, но можно переименовать.
        File.Delete(old);
        File.Move(exe, old);
        try
        {
            File.Move(tmp, exe);
        }
        catch
        {
            File.Move(old, exe);
            throw;
        }

        var psi = new ProcessStartInfo(exe, ResumeArg)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(exe) ?? ""
        };
        foreach (var (key, value) in resume)
            psi.Environment[key] = value;

        Process.Start(psi);
    }
}

using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ExplorerDock.Services;

/// <summary>GitHub Release 里的一个可下载文件（这里只用安装包那一个）。</summary>
public sealed record UpdateAsset(string Name, string DownloadUrl, long Size);

/// <summary>一次检查更新的结果：tag 名、版本号、可下载的安装包（没发 Release 时为 null）、该版本的说明正文。</summary>
public sealed record UpdateInfo(string Tag, Version Version, UpdateAsset? Setup, string Notes);

/// <summary>
/// 检查更新：查 GitHub 上的 tag，取最大的 vX.Y.Z 和当前版本比。
///
/// 只在用户点「检查更新」时才调用 —— 没有后台自动检查，不点就不联网。
/// </summary>
public static class UpdateChecker
{
    private const string RepoApi = "https://api.github.com/repos/v4806/ExplorerDock";

    private static readonly HttpClient Client = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };

        // GitHub API 要求带 User-Agent；Accept 用官方推荐值
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ExplorerDock-UpdateCheck");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

        return client;
    }

    /// <summary>当前程序版本（来自 csproj 的 &lt;Version&gt;）。</summary>
    public static Version CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);

    /// <summary>形如 v1.0.6 的当前版本文本。</summary>
    public static string CurrentVersionText =>
        $"v{CurrentVersion.Major}.{CurrentVersion.Minor}.{Math.Max(0, CurrentVersion.Build)}";

    /// <summary>
    /// 查最新版本。返回 null = 已经是最新（或仓库里没有比当前更新的 tag）。
    /// </summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken token)
    {
        var json = await Client.GetStringAsync($"{RepoApi}/tags?per_page=100", token).ConfigureAwait(false);

        Version? latest = null;
        string? latestTag = null;

        using (var document = JsonDocument.Parse(json))
        {
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (!item.TryGetProperty("name", out var nameProperty)) continue;

                var name = nameProperty.GetString();
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (!TryParseTag(name, out var version)) continue;

                if (latest is null || version > latest)
                {
                    latest = version;
                    latestTag = name;
                }
            }
        }

        if (latest is null || latestTag is null) return null;

        // 只比前三段：程序集版本是 1.0.6.0，tag 是 1.0.6
        if (latest <= Normalize(CurrentVersion)) return null;

        UpdateAsset? setup = null;
        var notes = string.Empty;

        try
        {
            var releaseJson = await Client
                .GetStringAsync($"{RepoApi}/releases/tags/{latestTag}", token)
                .ConfigureAwait(false);

            setup = FindSetupAsset(releaseJson);
            notes = ReadBody(releaseJson);
        }
        catch (HttpRequestException)
        {
            // 新版本可能只打了 tag 还没发 Release：那就只告诉用户有新版，不给下载
        }

        return new UpdateInfo(latestTag, latest, setup, notes);
    }

    /// <summary>取 Release 的说明正文（发版时写的那段介绍，就是确认框里的"新版本简介"）。</summary>
    private static string ReadBody(string releaseJson)
    {
        try
        {
            using var document = JsonDocument.Parse(releaseJson);

            return document.RootElement.TryGetProperty("body", out var body)
                ? body.GetString() ?? string.Empty
                : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>下载安装包到临时目录，返回本地完整路径。</summary>
    public static async Task<string> DownloadAsync(UpdateAsset asset, IProgress<double>? progress, CancellationToken token)
    {
        var directory = Path.Combine(Path.GetTempPath(), "ExplorerDock");
        Directory.CreateDirectory(directory);

        var path = Path.Combine(directory, asset.Name);

        using var response = await Client
            .GetAsync(asset.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, token)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? asset.Size;

        await using var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        await using var target = File.Create(path);

        var buffer = new byte[81920];
        long done = 0;

        while (true)
        {
            var read = await source.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
            if (read <= 0) break;

            await target.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            done += read;

            if (total > 0) progress?.Report((double)done / total);
        }

        return path;
    }

    /// <summary>把 v1.0.6 这样的 tag 解析成版本号；不是版本 tag 就返回 false。</summary>
    private static bool TryParseTag(string tag, out Version version)
    {
        var text = tag.TrimStart('v', 'V');

        if (Version.TryParse(text, out var parsed))
        {
            version = parsed;
            return true;
        }

        version = new Version(0, 0);
        return false;
    }

    private static Version Normalize(Version version)
        => new(version.Major, version.Minor, Math.Max(0, version.Build));

    /// <summary>在 Release 的资产列表里找安装包（ExplorerDock-Setup-*.exe）。</summary>
    private static UpdateAsset? FindSetupAsset(string releaseJson)
    {
        using var document = JsonDocument.Parse(releaseJson);

        if (!document.RootElement.TryGetProperty("assets", out var assets)) return null;

        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.TryGetProperty("name", out var nameProperty) ? nameProperty.GetString() : null;
            var url = asset.TryGetProperty("browser_download_url", out var urlProperty) ? urlProperty.GetString() : null;

            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(url)) continue;
            if (!name.StartsWith("ExplorerDock-Setup-", StringComparison.OrdinalIgnoreCase)) continue;
            if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;

            var size = asset.TryGetProperty("size", out var sizeProperty) && sizeProperty.TryGetInt64(out var value)
                ? value
                : 0;

            return new UpdateAsset(name, url, size);
        }

        return null;
    }
}

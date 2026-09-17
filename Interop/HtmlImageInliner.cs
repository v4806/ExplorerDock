using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace ExplorerDock.Interop;

/// <summary>
/// 把 HTML 里的远程图片下载下来嵌成 data URL。
///
/// 起因：网页复制的内容里图片基本都是 https 地址，粘贴时目标程序会挨个去下载 ——
/// 一页 45 张图能让 QQ 卡上几分钟。嵌进来之后粘贴就不用联网取图了，
/// 顺带还解决"原链接失效后粘出来没图"的问题。
///
/// 张数、单张大小、总量、总耗时都有上限，下不到的那张保持原样（不破坏内容）。
/// 这个过程在后台跑，不挡用户看记录。
/// </summary>
internal static class HtmlImageInliner
{
    private const int MaxImages = 40;
    private const long MaxImageBytes = 2 * 1024 * 1024;
    private const long MaxTotalBytes = 8 * 1024 * 1024;
    private const int MaxMilliseconds = 12_000;

    private static readonly Regex SrcPattern = new(
        "(src|href)\\s*=\\s*\"(https?://[^\"]+)\"",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly System.Net.Http.HttpClient Client = CreateClient();

    private static System.Net.Http.HttpClient CreateClient()
    {
        var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(6) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 ExplorerDock");
        return client;
    }

    /// <summary>返回内联后的 HTML 字节；没有可内联的图片时返回 null。</summary>
    public static byte[]? Inline(byte[] html)
    {
        try
        {
            var text = Encoding.UTF8.GetString(html);
            var matches = SrcPattern.Matches(text);
            if (matches.Count == 0) return null;

            // 同一个地址只下一次
            var urls = new List<string>();

            foreach (Match match in matches)
            {
                var url = match.Groups[2].Value;
                if (!urls.Contains(url, StringComparer.OrdinalIgnoreCase)) urls.Add(url);
            }

            if (urls.Count == 0) return null;

            long deadline = Environment.TickCount64 + MaxMilliseconds;
            var dataUrls = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            long total = 0;

            foreach (var url in urls)
            {
                if (dataUrls.Count >= MaxImages) break;
                if (total >= MaxTotalBytes) break;
                if (Environment.TickCount64 > deadline) break;

                var bytes = Download(url);
                if (bytes is null) continue;

                total += bytes.Length;
                dataUrls[url] = $"data:{GuessMime(bytes)};base64,{Convert.ToBase64String(bytes)}";
            }

            if (dataUrls.Count == 0) return null;

            var result = SrcPattern.Replace(text, match =>
            {
                var url = match.Groups[2].Value;

                return dataUrls.TryGetValue(url, out var dataUrl)
                    ? $"{match.Groups[1].Value}=\"{dataUrl}\""
                    : match.Value;
            });

            var output = Encoding.UTF8.GetBytes(result);
            return output.Length <= MaxInlinedHtmlBytes ? output : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>内联完的 HTML 最多这么大；超了宁可不内联（存下来太占地方）。</summary>
    private const int MaxInlinedHtmlBytes = 8 * 1024 * 1024;

    private static byte[]? Download(string url)
    {
        try
        {
            using var response = Client
                .GetAsync(url, System.Net.Http.HttpCompletionOption.ResponseHeadersRead)
                .GetAwaiter()
                .GetResult();

            if (!response.IsSuccessStatusCode) return null;

            var declared = response.Content.Headers.ContentLength;
            if (declared is > MaxImageBytes) return null;

            var bytes = response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
            if (bytes.Length == 0 || bytes.LongLength > MaxImageBytes) return null;

            return ClipboardInterop.LooksLikeImage(bytes) ? bytes : null;
        }
        catch
        {
            return null;
        }
    }

    private static string GuessMime(byte[] bytes)
    {
        if (bytes.Length < 12) return "image/png";

        if (bytes[0] == 0x89 && bytes[1] == 0x50) return "image/png";
        if (bytes[0] == 0xFF && bytes[1] == 0xD8) return "image/jpeg";
        if (bytes[0] == 0x47 && bytes[1] == 0x49) return "image/gif";
        if (bytes[0] == 0x42 && bytes[1] == 0x4D) return "image/bmp";

        // RIFF....WEBP
        if (bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[8] == 0x57 && bytes[9] == 0x45) return "image/webp";

        return "image/png";
    }
}

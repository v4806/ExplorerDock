using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using ExplorerDock.Interop;

namespace ExplorerDock.Services;

public enum ClipKind
{
    Text = 0,
    Image = 1,
    Files = 2,
}

/// <summary>一条剪贴记录。</summary>
public sealed class ClipItem
{
    public string Id { get; set; } = string.Empty;

    public ClipKind Kind { get; set; }

    /// <summary>文本内容（图片/文件为 null）。</summary>
    public string? Text { get; set; }

    /// <summary>图片原始字节在 blobs 目录里的文件名；null 表示没有图片数据。</summary>
    public string? BlobId { get; set; }

    /// <summary>从 HTML 里抠出来的预览图（只给列表当缩略图用，不写回剪贴板）。</summary>
    public string? PreviewBlobId { get; set; }

    /// <summary>PNG / DIB / DIBV5 —— 原样字节原本属于哪个剪贴板格式。</summary>
    public string? ImageFormat { get; set; }

    /// <summary>文件列表的路径。</summary>
    public string[]? Files { get; set; }

    /// <summary>复制时剪贴板上并存的其他格式（HTML / RTF 原始字节），粘贴时一并放回。</summary>
    public List<ClipFormat>? Formats { get; set; }

    /// <summary>落盘实际占用（无损压缩 + 加密之后）。</summary>
    public long SizeBytes { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public bool Favorited { get; set; }

    /// <summary>收藏所在分组 id；null = 未分组。</summary>
    public string? GroupId { get; set; }

    /// <summary>内容指纹，用于去重。</summary>
    public string Hash { get; set; } = string.Empty;

    /// <summary>图片的原始宽高文字（面板懒加载时填上，不落盘）。</summary>
    [JsonIgnore]
    public string? MeasuredSize { get; set; }

    /// <summary>是否已经为这条记录尝试过用隐藏渲染器出图（只记在内存里）。</summary>
    [JsonIgnore]
    public bool PreviewRenderTried { get; set; }

    [JsonIgnore]
    public string Preview => Kind switch
    {
        ClipKind.Text => PreviewText(Text),

        // 图文混排（Word 里复制的带图文字）：优先显示文字内容，
        // 光写"图片"两个字用户根本认不出是哪一条
        ClipKind.Image => Text is { Length: > 0 } caption
            ? PreviewText(caption)
            : (ImageFormat ?? "IMG") + " 图片",

        ClipKind.Files => Files is { Length: > 0 }
            ? (Files.Length > 1
                ? $"{Path.GetFileName(Files[0])} 等 {Files.Length} 个文件"
                : Path.GetFileName(Files[0]))
            : string.Empty,
        _ => string.Empty,
    };

    /// <summary>
    /// 文本预览：跳过开头的空行，最多取前 3 个有内容的行。
    /// 很多程序复制的文本前面带着换行，只取第一行的话列表里就是一片空白。
    /// </summary>
    private static string PreviewText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        var lines = new List<string>(3);
        int total = 0;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Trim().Length == 0) continue;

            lines.Add(line);
            total += line.Length;

            if (lines.Count >= 3 || total >= 200) break;
        }

        var joined = string.Join('\n', lines);
        return joined.Length > 300 ? joined[..300] : joined;
    }

    [JsonIgnore]
    public string SizeText => FormatSize(SizeBytes);

    public static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0.#} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):0.#} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):0.##} GB";
    }

    private static string FirstLine(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        int end = text.IndexOfAny(new[] { '\r', '\n' });
        var line = end < 0 ? text : text[..end];
        return line.Length > 400 ? line[..400] : line;
    }
}

/// <summary>收藏分组。</summary>
public sealed class ClipGroup
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public int Order { get; set; }
}

/// <summary>
/// 剪贴板库：内存里是明文对象，落盘一律加密。
///
/// 存储布局（%APPDATA%\ExplorerDock）：
///   clipboard.index     加密的 JSON 索引（元数据 + 文本全文，不含图片数据）
///   clipboard\blobs\*.bin  每条图片单独一个文件：zlib 无损压缩 → AES-GCM
///
/// 为什么拆开存：加一条文本不必重写所有图片；只有条目结构变化时才重写索引。
/// 图片字节本身始终是原样（压缩是无损的，解压后逐字节一致）。
/// </summary>
public sealed class ClipboardStore : IDisposable
{
    /// <summary>索引文件版本，将来结构变了靠它判断。</summary>
    private const int CurrentVersion = 1;

    private readonly object _gate = new();
    private readonly List<ClipItem> _items = new();     // 新 → 旧
    private readonly List<ClipGroup> _groups = new();
    private readonly Dictionary<string, ClipItem> _byHash = new();
    private readonly Dictionary<string, ClipItem> _byId = new();
    private readonly Dictionary<string, byte[]> _blobCache = new();
    private readonly List<string> _blobCacheOrder = new();
    private readonly List<PendingBlob> _pendingBlobs = new();

    /// <summary>串行化"写盘"这件事：索引只能有一个写入者。</summary>
    private readonly object _saveGate = new();

    private readonly ClipboardCrypto? _crypto;
    private readonly string _blobDirectory;
    private Timer? _saveTimer;
    private bool _saving;
    private bool _saveAgain;
    private volatile bool _disposed;
    private string? _lastError;

    private const int MaxBlobCache = 20;

    public ClipboardStore()
    {
        _crypto = ClipboardCrypto.Open();
        _blobDirectory = Path.Combine(ClipboardCrypto.DataDirectory, "clipboard", "blobs");
    }

    /// <summary>库内容变了（收藏/分组/新增/删除），UI 用它刷新。在 UI 线程触发。</summary>
    public event Action? Changed;

    /// <summary>加密可用 = 可以记录。不可用时整个剪贴板记录功能关闭，绝不明文落盘。</summary>
    public bool IsAvailable => _crypto is { IsUnlocked: true };

    /// <summary>最近一次出错信息（面板用来提示，正常人看不到）。</summary>
    public string? LastError
    {
        get { lock (_gate) return _lastError; }
    }

    public IReadOnlyList<ClipItem> Items
    {
        get { lock (_gate) return _items.ToArray(); }
    }

    public IReadOnlyList<ClipGroup> Groups
    {
        get { lock (_gate) return _groups.OrderBy(g => g.Order).ToArray(); }
    }

    public long TotalBytes
    {
        get { lock (_gate) return _items.Sum(i => i.SizeBytes); }
    }

    public int Count
    {
        get { lock (_gate) return _items.Count; }
    }

    private static string IndexPath => Path.Combine(ClipboardCrypto.DataDirectory, "clipboard.index");

    // ---------- 载入 / 保存 ----------

    /// <summary>载入索引。索引坏了、密钥不对都只归档旧文件并重建空库，绝不抛。</summary>
    public void Load()
    {
        if (!IsAvailable) return;

        try
        {
            Directory.CreateDirectory(_blobDirectory);

            if (!File.Exists(IndexPath)) return;

            var blob = File.ReadAllBytes(IndexPath);
            var plain = _crypto!.Decrypt(blob);
            if (plain is null)
            {
                Archive(IndexPath, "bad");
                SetError("剪贴板历史无法解密，已归档并重建");
                return;
            }

            var dto = JsonSerializer.Deserialize<VaultDto>(plain);
            if (dto is null)
            {
                Archive(IndexPath, "bad");
                SetError("剪贴板历史格式不正确，已归档并重建");
                return;
            }

            lock (_gate)
            {
                _items.Clear();
                _groups.Clear();
                _byHash.Clear();
                _byId.Clear();

                foreach (var group in dto.Groups ?? new List<ClipGroup>())
                {
                    _groups.Add(group);
                }

                foreach (var item in (dto.Items ?? new List<ClipItem>()).OrderByDescending(i => i.CreatedUtc))
                {
                    if (string.IsNullOrEmpty(item.Id)) continue;

                    _items.Add(item);
                    _byId[item.Id] = item;
                    if (!string.IsNullOrEmpty(item.Hash)) _byHash[item.Hash] = item;
                }
            }
        }
        catch (Exception ex)
        {
            SetError($"载入剪贴板历史失败：{ex.GetType().Name}");
        }
    }

    /// <summary>把一条新采集的内容放进库（去重 → 裁剪 → 排队保存）。在 UI 线程调用。</summary>
    internal ClipItem? Add(ClipboardPayload payload)
    {
        if (!IsAvailable || _disposed) return null;

        ClipItem result;

        lock (_gate)
        {
            if (_byHash.TryGetValue(payload.Hash, out var existing))
            {
                // 同一内容重复复制：不新增，只把它挪回最前（和系统一样）
                _items.Remove(existing);
                existing.CreatedUtc = DateTime.UtcNow;
                _items.Insert(0, existing);
                result = existing;
            }
            else
            {
                var item = new ClipItem
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Kind = payload.Kind switch
                    {
                        ClipboardPayloadKind.Image => ClipKind.Image,
                        ClipboardPayloadKind.Files => ClipKind.Files,
                        _ => ClipKind.Text,
                    },
                    Text = payload.Text,
                    ImageFormat = payload.ImageFormat,
                    Files = payload.Files,
                    Formats = payload.Formats,
                    Hash = payload.Hash,
                    CreatedUtc = DateTime.UtcNow,
                };

                if (payload.ImageBytes is { Length: > 0 })
                {
                    item.BlobId = item.Id + ".bin";
                    item.SizeBytes = payload.ImageBytes.LongLength;   // 先按原始大小估算，写完盘回填真实值
                    _pendingBlobs.Add(new PendingBlob(item.Id, item.BlobId, payload.ImageBytes, payload.ImageFormat));

                    // 立刻进内存缓存：blob 是异步写盘的，不先缓存的话，
                    // "刚复制完马上按 Win+V"这段时间里缩略图是空的（要等落盘后重开面板才有）
                    CacheBlob(item.BlobId, payload.ImageBytes);
                }
                else
                {
                    item.SizeBytes = EstimateTextSize(payload);

                    // 剪贴板上没有位图格式、但 HTML 里嵌了图片（QQ / 微信复制图文就是这种）：
                    // 抠出来的这张图只存给列表做缩略图，粘贴时不放回剪贴板
                    if (payload.PreviewImageBytes is { Length: > 0 })
                    {
                        item.PreviewBlobId = item.Id + ".preview.bin";
                        item.SizeBytes += payload.PreviewImageBytes.LongLength;
                        _pendingBlobs.Add(new PendingBlob(item.Id, item.PreviewBlobId, payload.PreviewImageBytes, "PREVIEW"));
                        CacheBlob(item.PreviewBlobId, payload.PreviewImageBytes);
                    }
                }

                _items.Insert(0, item);
                _byId[item.Id] = item;
                _byHash[item.Hash] = item;
                result = item;
            }

            Trim();
        }

        ScheduleSave();
        Changed?.Invoke();
        return result;
    }

    /// <summary>
    /// 把某条记录的某个"其他格式"换掉（后台把 HTML 里的远程图内联完时用）。
    /// 加锁替换，免得和保存线程抢同一个列表。
    /// </summary>
    public void ReplaceFormat(ClipItem item, int index, ClipFormat format)
    {
        lock (_gate)
        {
            if (item.Formats is null || index < 0 || index >= item.Formats.Count) return;
            item.Formats[index] = format;
        }

        ScheduleSave();
    }

    private static long EstimateTextSize(ClipboardPayload payload)
        => payload.Text is null ? 64 : 64 + System.Text.Encoding.UTF8.GetByteCount(payload.Text);

    /// <summary>取图片原始字节（解密 + 解压），带一层小缓存。可能返回 null（数据被删/损坏）。</summary>
    public byte[]? GetImageBytes(ClipItem item) => ReadBlob(item.BlobId);

    /// <summary>取"从 HTML 里抠出来的预览图"字节（只用于列表缩略图）。</summary>
    public byte[]? GetPreviewBytes(ClipItem item) => ReadBlob(item.PreviewBlobId);

    /// <summary>
    /// 把渲染出来的预览图存到这条记录上（隐藏渲染器出图之后调用）。
    /// 已经落盘的会直接覆盖，下次打开面板不用再渲染一遍。
    /// </summary>
    public void SetPreviewBlob(ClipItem item, byte[] png)
    {
        if (!IsAvailable || png.Length == 0) return;

        string blobId;

        lock (_gate)
        {
            item.PreviewBlobId ??= item.Id + ".preview.bin";
            blobId = item.PreviewBlobId;
            _pendingBlobs.Add(new PendingBlob(item.Id, blobId, png, "PREVIEW"));
        }

        CacheBlob(blobId, png);
        ScheduleSave();
    }

    /// <summary>把字节放进 blob 内存缓存（按访问顺序淘汰）。</summary>
    private void CacheBlob(string blobId, byte[] raw)
    {
        lock (_gate)
        {
            _blobCache[blobId] = raw;
            _blobCacheOrder.Remove(blobId);
            _blobCacheOrder.Add(blobId);

            while (_blobCacheOrder.Count > MaxBlobCache)
            {
                _blobCache.Remove(_blobCacheOrder[0]);
                _blobCacheOrder.RemoveAt(0);
            }
        }
    }

    private byte[]? ReadBlob(string? blobId)
    {
        if (blobId is null || !IsAvailable) return null;

        lock (_gate)
        {
            if (_blobCache.TryGetValue(blobId, out var cached)) return cached;
        }
        try
        {
            var path = Path.Combine(_blobDirectory, blobId);
            if (!File.Exists(path)) return null;

            var blob = File.ReadAllBytes(path);
            var packed = _crypto!.Decrypt(blob);
            if (packed is null) return null;

            var raw = Unpack(packed);
            CacheBlob(blobId, raw);
            return raw;
        }
        catch
        {
            return null;
        }
    }

    // ---------- 收藏 / 分组 / 删除 ----------

    public void SetFavorited(ClipItem item, bool favorited, string? groupId)
    {
        lock (_gate)
        {
            item.Favorited = favorited;
            item.GroupId = favorited ? groupId : null;
        }

        ScheduleSave();
        Changed?.Invoke();
    }

    public void SetGroup(ClipItem item, string? groupId)
    {
        lock (_gate)
        {
            item.GroupId = groupId;
        }

        ScheduleSave();
        Changed?.Invoke();
    }

    public ClipGroup CreateGroup(string name)
    {
        var group = new ClipGroup
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = name,
            Order = _groups.Count,
        };

        lock (_gate)
        {
            _groups.Add(group);
        }

        ScheduleSave();
        Changed?.Invoke();
        return group;
    }

    public void RenameGroup(ClipGroup group, string name)
    {
        lock (_gate)
        {
            group.Name = name;
        }

        ScheduleSave();
        Changed?.Invoke();
    }

    /// <summary>删分组：里面的收藏退回"未分组"，不删记录。</summary>
    public void RemoveGroup(ClipGroup group)
    {
        lock (_gate)
        {
            _groups.Remove(group);

            foreach (var item in _items)
            {
                if (item.GroupId == group.Id) item.GroupId = null;
            }
        }

        ScheduleSave();
        Changed?.Invoke();
    }

    public void Remove(ClipItem item)
    {
        lock (_gate)
        {
            _items.Remove(item);
            _byId.Remove(item.Id);
            if (!string.IsNullOrEmpty(item.Hash)) _byHash.Remove(item.Hash);

            if (item.BlobId is not null)
            {
                _blobCache.Remove(item.BlobId);
                _blobCacheOrder.Remove(item.BlobId);
                DeleteBlob(item.BlobId);
            }
        }

        ScheduleSave();
        Changed?.Invoke();
    }

    /// <summary>清空历史（保留收藏）。</summary>
    public void ClearUnfavorited()
    {
        lock (_gate)
        {
            for (int i = _items.Count - 1; i >= 0; i--)
            {
                var item = _items[i];
                if (item.Favorited) continue;

                _items.RemoveAt(i);
                _byId.Remove(item.Id);
                if (!string.IsNullOrEmpty(item.Hash)) _byHash.Remove(item.Hash);
                if (item.BlobId is not null) DeleteBlob(item.BlobId);
            }
        }

        ScheduleSave();
        Changed?.Invoke();
    }

    /// <summary>彻底清空（含收藏与分组）。</summary>
    public void ClearAll()
    {
        lock (_gate)
        {
            _items.Clear();
            _groups.Clear();
            _byId.Clear();
            _byHash.Clear();
            _blobCache.Clear();
            _blobCacheOrder.Clear();

            try
            {
                if (Directory.Exists(_blobDirectory)) Directory.Delete(_blobDirectory, recursive: true);
                Directory.CreateDirectory(_blobDirectory);
            }
            catch
            {
                // 删不掉就留着，下次照样能覆盖写
            }
        }

        ScheduleSave();
        Changed?.Invoke();
    }

    // ---------- 上限裁剪 ----------

    /// <summary>
    /// 按设置裁剪：数量上限作用于非收藏条目；容量上限先淘汰最旧的非收藏，
    /// 仍然超了就继续淘汰最旧的收藏（否则磁盘占用会无上限增长）。
    /// 调用方必须已持有 _gate。
    /// </summary>
    private void Trim()
    {
        // 设置里 0 表示不设限，这里换算成实际上限
        int itemSetting = App.Settings.ClipboardMaxItems;
        int maxItems = itemSetting <= 0 ? int.MaxValue : itemSetting;

        long byteSetting = App.Settings.ClipboardMaxBytes;
        long maxBytes = byteSetting <= 0 ? long.MaxValue : byteSetting;

        while (_items.Count(i => !i.Favorited) > maxItems)
        {
            var oldest = FindOldest(favorited: false);
            if (oldest is null) break;
            RemoveInternal(oldest);
        }

        while (TotalBytesLocked() > maxBytes && _items.Count > 0)
        {
            var victim = FindOldest(favorited: false) ?? FindOldest(favorited: true);
            if (victim is null) break;
            RemoveInternal(victim);
        }
    }

    private long TotalBytesLocked() => _items.Sum(i => i.SizeBytes);

    private ClipItem? FindOldest(bool favorited)
    {
        ClipItem? oldest = null;

        foreach (var item in _items)
        {
            if (item.Favorited != favorited) continue;
            if (oldest is null || item.CreatedUtc < oldest.CreatedUtc) oldest = item;
        }

        return oldest;
    }

    /// <summary>从库里摘掉一条（含它的图片文件）。调用方必须已持有 _gate。</summary>
    private void RemoveInternal(ClipItem item)
    {
        _items.Remove(item);
        _byId.Remove(item.Id);
        if (!string.IsNullOrEmpty(item.Hash)) _byHash.Remove(item.Hash);

        DeleteBlobAndCache(item.BlobId);
        DeleteBlobAndCache(item.PreviewBlobId);
    }

    private void DeleteBlobAndCache(string? blobId)
    {
        if (blobId is null) return;

        _blobCache.Remove(blobId);
        _blobCacheOrder.Remove(blobId);
        DeleteBlob(blobId);
    }

    // ---------- 保存 ----------

    /// <summary>排队保存（600ms 合并一次）。</summary>
    public void ScheduleSave()
    {
        if (_disposed || !IsAvailable) return;

        lock (_gate)
        {
            _saveTimer ??= new Timer(_ => QueueSave(), null, Timeout.Infinite, Timeout.Infinite);
            _saveTimer.Change(600, Timeout.Infinite);
        }
    }

    private void QueueSave()
    {
        if (_disposed || !IsAvailable) return;

        lock (_saveGate)
        {
            if (_saving)
            {
                _saveAgain = true;   // 这次改动没赶上正在写的那一轮，写完再来一次
                return;
            }

            _saving = true;
        }

        StartSaveThread();
    }

    private void StartSaveThread()
    {
        var thread = new Thread(() =>
        {
            try
            {
                SaveCore();
            }
            catch (Exception ex)
            {
                SetError($"保存剪贴板历史失败：{ex.GetType().Name}");
            }
            finally
            {
                bool again;

                lock (_saveGate)
                {
                    _saving = false;
                    again = _saveAgain;
                    _saveAgain = false;
                    Monitor.PulseAll(_saveGate);
                }

                if (again && !_disposed) QueueSave();
            }
        })
        {
            IsBackground = true,
            Name = "ExplorerDock.ClipboardSave",
            Priority = ThreadPriority.BelowNormal,
        };

        thread.Start();
    }

    /// <summary>退出前同步保存一次，保证最后一条不丢。</summary>
    public void SaveNow()
    {
        if (!IsAvailable) return;

        try
        {
            try
            {
                _saveTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            }
            catch
            {
                // 忽略
            }

            lock (_saveGate)
            {
                // 后台可能正好在写：等它让位（几十毫秒级），避免两个线程同时写索引
                int waited = 0;
                while (_saving && waited < 3000)
                {
                    Monitor.Wait(_saveGate, 100);
                    waited += 100;
                }

                SaveCore();
            }
        }
        catch (Exception ex)
        {
            SetError($"保存剪贴板历史失败：{ex.GetType().Name}");
        }
    }

    private void SaveCore()
    {
        if (!IsAvailable) return;

        // 1) 先把待写图片落盘（索引里引用了它们，必须先写）
        List<PendingBlob> pending;
        lock (_gate)
        {
            pending = _pendingBlobs.ToList();
            _pendingBlobs.Clear();
        }

        foreach (var blob in pending)
        {
            try
            {
                var packed = Pack(blob.Raw, blob.Format);
                var encrypted = _crypto!.Encrypt(packed);

                Directory.CreateDirectory(_blobDirectory);
                var path = Path.Combine(_blobDirectory, blob.BlobId);
                var temp = path + ".tmp";

                File.WriteAllBytes(temp, encrypted);
                File.Move(temp, path, overwrite: true);

                lock (_gate)
                {
                    if (_byId.TryGetValue(blob.ItemId, out var item))
                    {
                        // 主图片：估算值换成实际打包后的大小；预览图：在原有基础上累加
                        if (string.Equals(item.BlobId, blob.BlobId, StringComparison.Ordinal))
                        {
                            item.SizeBytes = encrypted.LongLength;
                        }
                        else
                        {
                            item.SizeBytes += encrypted.LongLength;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                SetError($"写入剪贴板图片失败：{ex.GetType().Name}");
            }
        }

        // 2) 序列化索引 → 加密 → 原子替换
        VaultDto dto;
        lock (_gate)
        {
            dto = new VaultDto
            {
                Version = CurrentVersion,
                Groups = _groups.ToList(),
                Items = _items.ToList(),
            };
        }

        var json = JsonSerializer.SerializeToUtf8Bytes(dto, JsonOptions);
        var payload = _crypto!.Encrypt(json);

        Directory.CreateDirectory(ClipboardCrypto.DataDirectory);
        var indexTemp = IndexPath + ".tmp";
        File.WriteAllBytes(indexTemp, payload);
        File.Move(indexTemp, IndexPath, overwrite: true);

        ClearError();
    }

    // ---------- 压缩（无损）与工具 ----------

    private sealed record PendingBlob(string ItemId, string BlobId, byte[] Raw, string? Format);

    private const byte PackRaw = 0;
    private const byte PackZlib = 1;

    /// <summary>
    /// 打包图片字节：zlib 无损压缩，首字节标记用了哪种方式。
    /// 像素/字节完全不变，解压后与原数据逐字节一致 —— 纯粹为了省磁盘，不是转码。
    /// 已经是压缩格式（PNG）的直接原样存，别再压一遍。
    /// </summary>
    private static byte[] Pack(byte[] raw, string? format)
    {
        if (string.Equals(format, "PNG", StringComparison.OrdinalIgnoreCase)) return Prepend(PackRaw, raw);

        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(raw, 0, raw.Length);
        }

        return Prepend(PackZlib, output.ToArray());
    }

    /// <summary>
    /// 还原图片原始字节。方式标记写在数据里（自描述），
    /// 所以将来压缩策略变了，旧数据也照样能读出来。
    /// </summary>
    private static byte[] Unpack(byte[] packed)
    {
        if (packed.Length == 0) return packed;

        byte method = packed[0];
        var body = packed.AsSpan(1).ToArray();

        if (method != PackZlib) return body;

        using var input = new MemoryStream(body);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();

        zlib.CopyTo(output);
        return output.ToArray();
    }

    private static byte[] Prepend(byte marker, byte[] data)
    {
        var result = new byte[data.Length + 1];
        result[0] = marker;
        data.CopyTo(result, 1);
        return result;
    }

    private void DeleteBlob(string blobId)
    {
        try
        {
            var path = Path.Combine(_blobDirectory, blobId);
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // 删不掉留着，不影响功能
        }
    }

    private static void Archive(string path, string tag)
    {
        try
        {
            var target = $"{path}.{tag}-{DateTime.Now:yyyyMMddHHmmss}";
            File.Move(path, target, overwrite: true);
        }
        catch
        {
            // 归档失败也不能影响启动
        }
    }

    private void SetError(string message)
    {
        lock (_gate)
        {
            _lastError = message;
        }
    }

    private void ClearError()
    {
        lock (_gate)
        {
            _lastError = null;
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed class VaultDto
    {
        public int Version { get; set; }

        public List<ClipGroup>? Groups { get; set; }

        public List<ClipItem>? Items { get; set; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            _saveTimer?.Dispose();
        }
        catch
        {
            // 忽略
        }

        try
        {
            _crypto?.Dispose();
        }
        catch
        {
            // 忽略
        }
    }
}

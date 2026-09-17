using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace ExplorerDock.Services;

/// <summary>
/// 剪贴板数据的落盘密钥与加解密。
///
/// 密钥链：DEK（32 字节 CSPRNG 随机，每个用户的库唯一）→ 依次套上已启用的保护层。
/// 目前实现 L1（DPAPI，默认开启且不可关闭）；L2 主密码、L3 TPM 绑定在后续轮次接入。
///
/// 设计要点：
/// - DEK 是随机的，所以同一份程序装在十台机器上、十份密文互不通用；
/// - DPAPI 之外再叠一层本程序专属熵值，别的程序即使在同一账户下盲目调用
///   CryptUnprotectData 也解不开；
/// - AES-GCM 只用于整块加解密，内存里始终是明文对象，显示/粘贴路径完全不碰加密。
/// </summary>
internal sealed class ClipboardCrypto : IDisposable
{
    private const string Magic = "EDC1";
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;

    /// <summary>DPAPI 的附加熵（应用专属），编译进程序里。</summary>
    private static readonly byte[] Entropy =
        SHA256.HashData(Encoding.UTF8.GetBytes("ExplorerDock.Clipboard.Entropy.v1"));

    private readonly object _gate = new();
    private readonly AesGcm _aes;
    private byte[]? _dek;

    private ClipboardCrypto(byte[] dek)
    {
        _dek = dek;
        _aes = new AesGcm(dek, TagSize);
    }

    /// <summary>数据目录（%APPDATA%\ExplorerDock）。</summary>
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ExplorerDock");

    public static string KeyPath { get; } = Path.Combine(DataDirectory, "clipboard.key");

    /// <summary>当前是否已经解锁（能加解密）。</summary>
    public bool IsUnlocked => _dek is not null;

    /// <summary>
    /// 打开密钥库：有密钥文件就解出来，没有就生成一个（随机 DEK → DPAPI 保护后落盘）。
    /// 返回 null 表示加密不可用 —— 此时调用方必须**关闭剪贴板记录**，绝不能退化成明文落盘。
    /// </summary>
    public static ClipboardCrypto? Open()
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);

            byte[] dek;

            if (File.Exists(KeyPath))
            {
                var blob = File.ReadAllBytes(KeyPath);
                var plain = Dpapi.Unprotect(blob, Entropy);

                if (plain is null || plain.Length != KeySize) return null;
                dek = plain;
            }
            else
            {
                dek = RandomNumberGenerator.GetBytes(KeySize);
                File.WriteAllBytes(KeyPath, Dpapi.Protect(dek, Entropy));
            }

            return new ClipboardCrypto(dek);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>加密一段明文，输出 "EDC1" + nonce + tag + 密文。</summary>
    public byte[] Encrypt(byte[] plain)
    {
        var dek = _dek ?? throw new InvalidOperationException("密钥库未解锁");

        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var tag = new byte[TagSize];
        var cipher = new byte[plain.Length];

        lock (_gate)
        {
            _aes.Encrypt(nonce, plain, cipher, tag);
        }

        var result = new byte[4 + NonceSize + TagSize + cipher.Length];
        Encoding.ASCII.GetBytes(Magic).CopyTo(result, 0);
        nonce.CopyTo(result, 4);
        tag.CopyTo(result, 4 + NonceSize);
        cipher.CopyTo(result, 4 + NonceSize + TagSize);

        return result;
    }

    /// <summary>解密；数据损坏、被换过、或者密钥不对都返回 null（调用方按"读不出来"处理）。</summary>
    public byte[]? Decrypt(byte[] blob)
    {
        try
        {
            if (_dek is null) return null;
            if (blob.Length < 4 + NonceSize + TagSize) return null;
            if (Encoding.ASCII.GetString(blob, 0, 4) != Magic) return null;

            var nonce = blob.AsSpan(4, NonceSize).ToArray();
            var tag = blob.AsSpan(4 + NonceSize, TagSize).ToArray();
            int cipherLength = blob.Length - 4 - NonceSize - TagSize;
            var cipher = blob.AsSpan(4 + NonceSize + TagSize, cipherLength).ToArray();
            var plain = new byte[cipherLength];

            lock (_gate)
            {
                _aes.Decrypt(nonce, cipher, tag, plain);
            }

            return plain;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            try
            {
                _aes.Dispose();
            }
            catch
            {
                // 忽略
            }

            if (_dek is not null)
            {
                CryptographicOperations.ZeroMemory(_dek);
                _dek = null;
            }
        }
    }
}

/// <summary>DPAPI（CryptProtectData / CryptUnprotectData）的 P/Invoke 封装。</summary>
internal static class Dpapi
{
    private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(
        ref DATA_BLOB pDataIn,
        string? szDataDescr,
        ref DATA_BLOB pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        int dwFlags,
        out DATA_BLOB pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(
        ref DATA_BLOB pDataIn,
        IntPtr ppszDataDescr,
        ref DATA_BLOB pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        int dwFlags,
        out DATA_BLOB pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    public static byte[] Protect(byte[] data, byte[] entropy)
    {
        var input = ToBlob(data);
        var extra = ToBlob(entropy);

        try
        {
            if (!CryptProtectData(ref input, "ExplorerDock clipboard key", ref extra, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out var output))
            {
                throw new CryptographicException($"CryptProtectData failed: {Marshal.GetLastWin32Error()}");
            }

            try
            {
                return FromBlob(output);
            }
            finally
            {
                LocalFree(output.pbData);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(input.pbData);
            Marshal.FreeHGlobal(extra.pbData);
        }
    }

    public static byte[]? Unprotect(byte[] blob, byte[] entropy)
    {
        var input = ToBlob(blob);
        var extra = ToBlob(entropy);

        try
        {
            if (!CryptUnprotectData(ref input, IntPtr.Zero, ref extra, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out var output))
            {
                return null;
            }

            try
            {
                return FromBlob(output);
            }
            finally
            {
                LocalFree(output.pbData);
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(input.pbData);
            Marshal.FreeHGlobal(extra.pbData);
        }
    }

    private static DATA_BLOB ToBlob(byte[] data)
    {
        var pointer = Marshal.AllocHGlobal(data.Length);
        Marshal.Copy(data, 0, pointer, data.Length);
        return new DATA_BLOB { cbData = data.Length, pbData = pointer };
    }

    private static byte[] FromBlob(DATA_BLOB blob)
    {
        var result = new byte[blob.cbData];
        Marshal.Copy(blob.pbData, result, 0, blob.cbData);
        return result;
    }
}

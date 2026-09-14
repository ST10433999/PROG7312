using System.Security.Cryptography;
using SmartX.Shared.Models;

namespace SmartX.Api.Services;

/// <summary>
/// Stores configuration files, deployment photos and hardware logs against a sensor profile.
/// <para>
/// Uploads are streamed straight from the multipart body to disk through an AES-256-CBC
/// <see cref="CryptoStream"/> – the file is never buffered in memory as a whole, and what lands on
/// disk is ciphertext. A SHA-256 of the plaintext is computed on the same single pass for integrity.
/// </para>
/// </summary>
public sealed class AttachmentService
{
    public const long MaxBytes = 25 * 1024 * 1024;
    public const int MaxFilesPerRequest = 10;
    /// <summary>10 files × 25 MB plus multipart framing headroom.</summary>
    public const long MaxRequestBytes = MaxFilesPerRequest * MaxBytes + 1024 * 1024;
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".json", ".yaml", ".yml", ".cfg", ".ini", ".txt", ".log", ".csv", ".png", ".jpg", ".jpeg", ".webp", ".pdf", ".bin" };

    private readonly string _root;
    private readonly byte[] _key;
    private readonly ILogger<AttachmentService> _logger;

    public AttachmentService(IConfiguration config, IHostEnvironment env, ILogger<AttachmentService> logger)
    {
        _logger = logger;
        var configuredRoot = config["Attachments:Root"];
        _root = Path.GetFullPath(string.IsNullOrWhiteSpace(configuredRoot)
            ? Path.Combine(env.ContentRootPath, "App_Data", "attachments")
            : configuredRoot);
        Directory.CreateDirectory(_root);

        var keyText = config["Attachments:AesKeyBase64"];
        _key = string.IsNullOrWhiteSpace(keyText) ? SHA256.HashData("smartx-dev-only-key"u8) : Convert.FromBase64String(keyText);
        if (_key.Length != 32) throw new InvalidOperationException("Attachments:AesKeyBase64 must decode to 32 bytes.");
    }

    public static string? Validate(string fileName, string contentType, long? length)
    {
        var ext = Path.GetExtension(fileName);
        if (string.IsNullOrEmpty(ext) || !AllowedExtensions.Contains(ext))
            return $"File type '{ext}' is not permitted. Allowed: {string.Join(", ", AllowedExtensions)}.";
        if (length is > MaxBytes) return $"File exceeds the {MaxBytes / 1024 / 1024} MB limit.";
        return null;
    }

    public async Task<AttachmentInfo> SaveAsync(SensorProfile sensor, string fileName, string contentType, Stream source, CancellationToken ct)
    {
        var info = new AttachmentInfo
        {
            Id = Guid.NewGuid(),
            FileName = Path.GetFileName(fileName),
            ContentType = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType,
            Encrypted = true,
            UploadedAt = DateTimeOffset.UtcNow
        };

        var dir = Path.Combine(_root, sensor.Id.ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, info.Id.ToString("N") + ".enc");

        using var aes = Aes.Create();
        aes.Key = _key;
        aes.GenerateIV();

        await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true);
        await file.WriteAsync(aes.IV, ct);                                     // IV prefix (16 bytes)

        await using var crypto = new CryptoStream(file, aes.CreateEncryptor(), CryptoStreamMode.Write, leaveOpen: true);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        var buffer = new byte[64 * 1024];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            total += read;
            if (total > MaxBytes)
            {
                await crypto.DisposeAsync(); file.Close(); File.Delete(path);
                throw new InvalidOperationException($"File exceeds the {MaxBytes / 1024 / 1024} MB limit.");
            }
            sha.AppendData(buffer, 0, read);
            await crypto.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        await crypto.FlushFinalBlockAsync(ct);

        info.SizeBytes = total;
        info.Sha256 = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
        lock (sensor.Attachments) sensor.Attachments.Add(info);

        _logger.LogInformation("Stored encrypted attachment {File} ({Bytes} B) for {Mac}.", info.FileName, total, sensor.MacAddress);
        return info;
    }

    public Stream? Open(SensorProfile sensor, AttachmentInfo info)
    {
        var path = Path.Combine(_root, sensor.Id.ToString("N"), info.Id.ToString("N") + ".enc");
        if (!File.Exists(path)) return null;

        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
        var iv = new byte[16];
        file.ReadExactly(iv);

        var aes = Aes.Create();
        aes.Key = _key;
        aes.IV = iv;
        return new CryptoStream(file, aes.CreateDecryptor(), CryptoStreamMode.Read);
    }

    public bool Delete(SensorProfile sensor, AttachmentInfo info)
    {
        var path = Path.Combine(_root, sensor.Id.ToString("N"), info.Id.ToString("N") + ".enc");
        if (File.Exists(path)) File.Delete(path);
        lock (sensor.Attachments) return sensor.Attachments.Remove(info);
    }
}

using Taipower.Safety.Core.Common;

namespace Taipower.Safety.Infrastructure.Data;

/// <summary>
/// 檔案系統實作。所有相對路徑解析後都必須落在 <see cref="StorageOptions.RootPath"/> 內，
/// 以阻擋目錄穿越（例如 StoredName 帶有 <c>..\</c>）。
/// </summary>
public sealed class FileStorage(StorageOptions options) : IFileStorage
{
    private readonly string _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.RootPath));

    public async Task<string> SaveAsync(string relativeDir, string extension, Stream content, CancellationToken ct = default)
    {
        var rel = $"{relativeDir.Trim('/', '\\')}/{Guid.NewGuid():N}{extension}";
        var full = Resolve(rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await using var fs = File.Create(full);
        await content.CopyToAsync(fs, ct);
        return rel;
    }

    public Task<Stream?> OpenReadAsync(string storedPath, CancellationToken ct = default)
    {
        var full = Resolve(storedPath);
        return Task.FromResult<Stream?>(File.Exists(full) ? File.OpenRead(full) : null);
    }

    public void Delete(string storedPath)
    {
        var full = Resolve(storedPath);
        if (File.Exists(full)) File.Delete(full);
    }

    private string Resolve(string relative)
    {
        var full = Path.GetFullPath(Path.Combine(_root, relative.Replace('\\', '/')));
        if (full != _root && !full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException("Storage path escapes the storage root.");
        return full;
    }
}

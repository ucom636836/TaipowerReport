namespace Taipower.Safety.Core.Common;

/// <summary>檔案儲存設定（走設定檔，正式環境指向可寫入且已備份的路徑）。</summary>
public sealed class StorageOptions
{
    public const string Section = "Storage";
    public string RootPath { get; set; } = "App_Data/uploads";
    public long MaxFileBytes { get; set; } = 10 * 1024 * 1024;
}

/// <summary>
/// 抽象檔案儲存。<c>StoredPath</c> 為相對於儲存根的路徑；實作必須確保解析後
/// 仍落在根目錄內（防目錄穿越），呼叫端給的檔名一律視為不可信。
/// </summary>
public interface IFileStorage
{
    /// <summary>存入一個檔案（檔名由實作產生），回傳其相對 StoredPath。</summary>
    Task<string> SaveAsync(string relativeDir, string extension, Stream content, CancellationToken ct = default);

    /// <summary>開啟檔案讀取串流；不存在回傳 null。</summary>
    Task<Stream?> OpenReadAsync(string storedPath, CancellationToken ct = default);

    /// <summary>刪除檔案（不存在則略過）。</summary>
    void Delete(string storedPath);
}

/// <summary>允許的圖片類型（副檔名 ↔ content type）與檔頭辨識。</summary>
public static class ImageContent
{
    private static readonly IReadOnlyDictionary<string, string> ByExtension = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".png"] = "image/png",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
    };

    public static bool IsAllowedExtension(string? ext) => ext is not null && ByExtension.ContainsKey(ext);

    public static string? ContentTypeFor(string ext) => ByExtension.TryGetValue(ext, out var c) ? c : null;

    /// <summary>以檔頭 magic bytes 確認內容真為允許的圖片（防偽裝副檔名）。</summary>
    public static bool LooksLikeImage(ReadOnlySpan<byte> head)
    {
        if (head.Length >= 3 && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF) return true;            // JPEG
        if (head.Length >= 8 && head[0] == 0x89 && head[1] == 0x50 && head[2] == 0x4E && head[3] == 0x47
            && head[4] == 0x0D && head[5] == 0x0A && head[6] == 0x1A && head[7] == 0x0A) return true;           // PNG
        if (head.Length >= 6 && head[0] == (byte)'G' && head[1] == (byte)'I' && head[2] == (byte)'F') return true; // GIF87a/89a
        if (head.Length >= 12 && head[0] == (byte)'R' && head[1] == (byte)'I' && head[2] == (byte)'F' && head[3] == (byte)'F'
            && head[8] == (byte)'W' && head[9] == (byte)'E' && head[10] == (byte)'B' && head[11] == (byte)'P') return true; // WEBP
        return false;
    }
}

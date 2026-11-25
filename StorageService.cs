using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace FluentClip;

public sealed class StorageService
{
    private const string AutoRunRegistryKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AutoRunValueName = "FluentClip";

    private readonly string _bootstrapDir;
    private readonly string _bootstrapPath;
    private readonly SemaphoreSlim _ioLock = new(1, 1);

    private HistoryDatabase _db = new();

    public BootstrapConfig Config { get; private set; } = new();

    public event Action? HistoryChanged;
    public event Action? ConfigChanged;

    public StorageService()
    {
        _bootstrapDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "FluentClip");
        _bootstrapPath = Path.Combine(_bootstrapDir, "bootstrap.json");
    }

    public string CacheRoot => Config.CacheDirectory;
    public string RawImagesDir => Path.Combine(CacheRoot, "images", "raw");
    public string ThumbImagesDir => Path.Combine(CacheRoot, "images", "thumbs");
    public string HistoryDbPath => Path.Combine(CacheRoot, "history.json");

    public string GetRawImagePath(string imageHash) => Path.Combine(RawImagesDir, $"{imageHash}.png");
    public string GetThumbImagePath(string imageHash) => Path.Combine(ThumbImagesDir, $"{imageHash}.png");

    public void Initialize()
    {
        Directory.CreateDirectory(_bootstrapDir);
        if (File.Exists(_bootstrapPath))
        {
            try
            {
                string json = File.ReadAllText(_bootstrapPath, Encoding.UTF8);
                var loaded = JsonSerializer.Deserialize(json, AppJsonContext.Default.BootstrapConfig);
                if (loaded != null && !string.IsNullOrWhiteSpace(loaded.CacheDirectory))
                {
                    Config = loaded;
                }
            }
            catch
            {
                // Fallback to default config if corrupted
            }
        }
        else
        {
            SaveBootstrapConfig();
        }

        EnsureCacheDirectories(Config.CacheDirectory);
        LoadHistoryDatabase();
    }

    private static void EnsureCacheDirectories(string root)
    {
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "images", "raw"));
        Directory.CreateDirectory(Path.Combine(root, "images", "thumbs"));
    }

    public void SaveBootstrapConfig()
    {
        Directory.CreateDirectory(_bootstrapDir);
        string tmp = _bootstrapPath + ".tmp";
        string json = JsonSerializer.Serialize(Config, AppJsonContext.Default.BootstrapConfig);
        File.WriteAllText(tmp, json, Encoding.UTF8);
        File.Move(tmp, _bootstrapPath, overwrite: true);
        ApplyAutoStartSetting(Config.AutoStartOnBoot);
        ConfigChanged?.Invoke();
    }

    private void LoadHistoryDatabase()
    {
        if (!File.Exists(HistoryDbPath))
        {
            _db = new HistoryDatabase();
            return;
        }

        try
        {
            string json = File.ReadAllText(HistoryDbPath, Encoding.UTF8);
            _db = JsonSerializer.Deserialize(json, AppJsonContext.Default.HistoryDatabase) ?? new HistoryDatabase();
        }
        catch
        {
            _db = new HistoryDatabase();
        }
    }

    private void SaveHistoryDatabaseInternal()
    {
        EnsureCacheDirectories(CacheRoot);
        string tmp = HistoryDbPath + ".tmp";
        string json = JsonSerializer.Serialize(_db, AppJsonContext.Default.HistoryDatabase);
        File.WriteAllText(tmp, json, Encoding.UTF8);
        File.Move(tmp, HistoryDbPath, overwrite: true);
    }

    public IReadOnlyList<ClipRecord> QueryItems(string searchText, string category)
    {
        _ioLock.Wait();
        try
        {
            IEnumerable<ClipRecord> q = _db.Items;

            if (category == "Text")
                q = q.Where(x => x.Kind == ClipKind.Text || x.Kind == ClipKind.Files);
            else if (category == "Image")
                q = q.Where(x => x.Kind == ClipKind.Image);
            else if (category == "Pinned")
                q = q.Where(x => x.IsPinned);

            if (!string.IsNullOrWhiteSpace(searchText))
            {
                string s = searchText.Trim();
                q = q.Where(x =>
                    x.TextContent.Contains(s, StringComparison.OrdinalIgnoreCase) ||
                    x.Summary.Contains(s, StringComparison.OrdinalIgnoreCase) ||
                    x.SourceApp.Contains(s, StringComparison.OrdinalIgnoreCase) ||
                    x.DetectedColorHex.Contains(s, StringComparison.OrdinalIgnoreCase));
            }

            return q
                .OrderByDescending(x => x.IsPinned)
                .ThenByDescending(x => x.UpdatedUnixMs)
                .ToList();
        }
        finally
        {
            _ioLock.Release();
        }
    }

    public async Task AddTextClipAsync(string text, string sourceApp)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        string hash = ComputeSha256(Encoding.UTF8.GetBytes(text));
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        await _ioLock.WaitAsync();
        try
        {
            var existing = _db.Items.FirstOrDefault(x => x.Kind == ClipKind.Text && x.ContentHash == hash);
            if (existing != null)
            {
                existing.UpdatedUnixMs = now;
                if (!string.IsNullOrEmpty(sourceApp))
                    existing.SourceApp = sourceApp;
                SaveHistoryDatabaseInternal();
            }
            else
            {
                var record = new ClipRecord
                {
                    Kind = ClipKind.Text,
                    TextContent = text,
                    Summary = BuildTextSummary(text),
                    ContentHash = hash,
                    ByteSize = Encoding.UTF8.GetByteCount(text),
                    SourceApp = sourceApp,
                    DetectedColorHex = DetectHexColor(text),
                    IsUrl = DetectUrl(text),
                    CreatedUnixMs = now,
                    UpdatedUnixMs = now
                };
                _db.Items.Insert(0, record);
                EnforceQuotaInternal();
                SaveHistoryDatabaseInternal();
            }
        }
        finally
        {
            _ioLock.Release();
        }

        HistoryChanged?.Invoke();
    }

    public async Task AddImageClipAsync(CapturedImageResult img, string sourceApp)
    {
        string imgHash = ComputeSha256(img.PngBytes);
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        await _ioLock.WaitAsync();
        try
        {
            EnsureCacheDirectories(CacheRoot);
            string rawPath = GetRawImagePath(imgHash);
            string thumbPath = GetThumbImagePath(imgHash);

            if (!File.Exists(rawPath))
                await File.WriteAllBytesAsync(rawPath, img.PngBytes);
            if (!File.Exists(thumbPath))
                await File.WriteAllBytesAsync(thumbPath, img.ThumbnailPngBytes);

            var existing = _db.Items.FirstOrDefault(x => x.Kind == ClipKind.Image && x.ImageHash == imgHash);
            if (existing != null)
            {
                existing.UpdatedUnixMs = now;
                if (!string.IsNullOrEmpty(sourceApp))
                    existing.SourceApp = sourceApp;
                SaveHistoryDatabaseInternal();
            }
            else
            {
                var record = new ClipRecord
                {
                    Kind = ClipKind.Image,
                    ImageHash = imgHash,
                    ContentHash = imgHash,
                    ImageWidth = img.Width,
                    ImageHeight = img.Height,
                    ByteSize = img.PngBytes.LongLength,
                    Summary = $"图片 {img.Width}×{img.Height} ({FormatSize(img.PngBytes.LongLength)})",
                    SourceApp = sourceApp,
                    FilePaths = string.IsNullOrEmpty(img.SourceFilePath) ? new List<string>() : new List<string> { img.SourceFilePath },
                    CreatedUnixMs = now,
                    UpdatedUnixMs = now
                };
                _db.Items.Insert(0, record);
                EnforceQuotaInternal();
                SaveHistoryDatabaseInternal();
            }
        }
        finally
        {
            _ioLock.Release();
        }

        HistoryChanged?.Invoke();
    }

    public async Task AddFileClipAsync(List<string> files, string sourceApp)
    {
        if (files.Count == 0) return;

        string joined = string.Join("\n", files);
        string hash = ComputeSha256(Encoding.UTF8.GetBytes(joined));
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        await _ioLock.WaitAsync();
        try
        {
            var existing = _db.Items.FirstOrDefault(x => x.Kind == ClipKind.Files && x.ContentHash == hash);
            if (existing != null)
            {
                existing.UpdatedUnixMs = now;
                SaveHistoryDatabaseInternal();
            }
            else
            {
                string firstFile = Path.GetFileName(files[0]);
                string summary = files.Count == 1 ? $"文件: {firstFile}" : $"{firstFile} 等 {files.Count} 个文件";
                var record = new ClipRecord
                {
                    Kind = ClipKind.Files,
                    TextContent = joined,
                    Summary = summary,
                    ContentHash = hash,
                    FilePaths = files,
                    ByteSize = Encoding.UTF8.GetByteCount(joined),
                    SourceApp = sourceApp,
                    CreatedUnixMs = now,
                    UpdatedUnixMs = now
                };
                _db.Items.Insert(0, record);
                EnforceQuotaInternal();
                SaveHistoryDatabaseInternal();
            }
        }
        finally
        {
            _ioLock.Release();
        }

        HistoryChanged?.Invoke();
    }

    public void TogglePin(string id)
    {
        _ioLock.Wait();
        try
        {
            var item = _db.Items.FirstOrDefault(x => x.Id == id);
            if (item != null)
            {
                item.IsPinned = !item.IsPinned;
                SaveHistoryDatabaseInternal();
            }
        }
        finally
        {
            _ioLock.Release();
        }
        HistoryChanged?.Invoke();
    }

    public void TouchItem(string id)
    {
        _ioLock.Wait();
        try
        {
            var item = _db.Items.FirstOrDefault(x => x.Id == id);
            if (item != null)
            {
                item.UpdatedUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                SaveHistoryDatabaseInternal();
            }
        }
        finally
        {
            _ioLock.Release();
        }
        HistoryChanged?.Invoke();
    }

    public void DeleteItem(string id)
    {
        _ioLock.Wait();
        try
        {
            _db.Items.RemoveAll(x => x.Id == id);
            GarbageCollectOrphanImagesInternal();
            SaveHistoryDatabaseInternal();
        }
        finally
        {
            _ioLock.Release();
        }
        HistoryChanged?.Invoke();
    }

    public void ClearUnpinned()
    {
        _ioLock.Wait();
        try
        {
            _db.Items.RemoveAll(x => !x.IsPinned);
            GarbageCollectOrphanImagesInternal();
            SaveHistoryDatabaseInternal();
        }
        finally
        {
            _ioLock.Release();
        }
        HistoryChanged?.Invoke();
    }

    public void EnforceQuota()
    {
        _ioLock.Wait();
        try
        {
            EnforceQuotaInternal();
            SaveHistoryDatabaseInternal();
        }
        finally
        {
            _ioLock.Release();
        }
        HistoryChanged?.Invoke();
    }

    private void EnforceQuotaInternal()
    {
        // 1. Trim by MaxHistoryCount (never trim pinned items)
        if (Config.MaxHistoryCount > 0 && _db.Items.Count > Config.MaxHistoryCount)
        {
            var ordered = _db.Items
                .OrderByDescending(x => x.IsPinned)
                .ThenByDescending(x => x.UpdatedUnixMs)
                .ToList();

            var kept = new List<ClipRecord>(Config.MaxHistoryCount);
            foreach (var item in ordered)
            {
                if (item.IsPinned || kept.Count < Config.MaxHistoryCount)
                    kept.Add(item);
            }
            _db.Items = kept;
        }

        // 2. Trim by MaxCacheSizeMb (protecting pinned items)
        if (Config.MaxCacheSizeMb > 0)
        {
            long maxBytes = (long)Config.MaxCacheSizeMb * 1024L * 1024L;
            var unpinnedOldest = _db.Items
                .Where(x => !x.IsPinned)
                .OrderBy(x => x.UpdatedUnixMs)
                .ToList();

            long totalImageBytes = _db.Items
                .Where(x => x.Kind == ClipKind.Image)
                .GroupBy(x => x.ImageHash)
                .Sum(g => g.First().ByteSize);

            int idx = 0;
            while (totalImageBytes > maxBytes && idx < unpinnedOldest.Count)
            {
                var victim = unpinnedOldest[idx++];
                _db.Items.Remove(victim);
                if (victim.Kind == ClipKind.Image)
                {
                    totalImageBytes -= victim.ByteSize;
                }
            }
        }

        GarbageCollectOrphanImagesInternal();
    }

    private void GarbageCollectOrphanImagesInternal()
    {
        var activeHashes = new HashSet<string>(
            _db.Items.Where(x => x.Kind == ClipKind.Image && !string.IsNullOrEmpty(x.ImageHash))
                     .Select(x => x.ImageHash),
            StringComparer.OrdinalIgnoreCase);

        DeleteOrphansInFolder(RawImagesDir, activeHashes);
        DeleteOrphansInFolder(ThumbImagesDir, activeHashes);
    }

    private static void DeleteOrphansInFolder(string folder, HashSet<string> activeHashes)
    {
        if (!Directory.Exists(folder)) return;
        foreach (string file in Directory.GetFiles(folder, "*.png"))
        {
            string name = Path.GetFileNameWithoutExtension(file);
            if (!activeHashes.Contains(name))
            {
                try { File.Delete(file); } catch { }
            }
        }
    }

    /// <summary>
    /// Atomically migrates existing history database & deduplicated image cache to a new user-selected directory.
    /// </summary>
    public async Task<(bool Success, string Message)> MigrateCacheDirectoryAsync(string newDirectory, bool migrateData)
    {
        if (string.IsNullOrWhiteSpace(newDirectory))
            return (false, "目录路径不能为空。");

        string fullNewDir;
        try
        {
            fullNewDir = Path.GetFullPath(newDirectory.Trim());
        }
        catch (Exception ex)
        {
            return (false, $"路径格式无效: {ex.Message}");
        }

        string oldDir = Path.GetFullPath(CacheRoot);
        if (string.Equals(oldDir.TrimEnd('\\'), fullNewDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            return (true, "新目录与当前缓存目录相同。");
        }

        await _ioLock.WaitAsync();
        try
        {
            EnsureCacheDirectories(fullNewDir);

            if (migrateData)
            {
                SaveHistoryDatabaseInternal();
                await Task.Run(() =>
                {
                    if (File.Exists(HistoryDbPath))
                    {
                        File.Copy(HistoryDbPath, Path.Combine(fullNewDir, "history.json"), overwrite: true);
                    }

                    CopyPngDirectory(RawImagesDir, Path.Combine(fullNewDir, "images", "raw"));
                    CopyPngDirectory(ThumbImagesDir, Path.Combine(fullNewDir, "images", "thumbs"));
                });
            }

            Config.CacheDirectory = fullNewDir;
            SaveBootstrapConfig();
            LoadHistoryDatabase();

            if (migrateData && Directory.Exists(oldDir))
            {
                try
                {
                    // Clean up old cache artifacts safely without deleting unrelated parent files
                    string oldHistory = Path.Combine(oldDir, "history.json");
                    string oldImages = Path.Combine(oldDir, "images");
                    if (File.Exists(oldHistory)) File.Delete(oldHistory);
                    if (Directory.Exists(oldImages)) Directory.Delete(oldImages, recursive: true);
                }
                catch
                {
                    // Non-fatal if user had folder open in Explorer
                }
            }
        }
        catch (Exception ex)
        {
            return (false, $"迁移缓存目录失败: {ex.Message}");
        }
        finally
        {
            _ioLock.Release();
        }

        HistoryChanged?.Invoke();
        return (true, migrateData ? "已成功迁移全部历史与图片缓存至新目录！" : "已切换至新缓存目录！");
    }

    private static void CopyPngDirectory(string srcDir, string dstDir)
    {
        if (!Directory.Exists(srcDir)) return;
        Directory.CreateDirectory(dstDir);
        foreach (string srcFile in Directory.GetFiles(srcDir, "*.png"))
        {
            string dstFile = Path.Combine(dstDir, Path.GetFileName(srcFile));
            File.Copy(srcFile, dstFile, overwrite: true);
        }
    }

    public CacheStats GetStats()
    {
        _ioLock.Wait();
        try
        {
            long imgBytes = 0;
            if (Directory.Exists(RawImagesDir))
            {
                foreach (string f in Directory.GetFiles(RawImagesDir, "*.png"))
                {
                    try { imgBytes += new FileInfo(f).Length; } catch { }
                }
            }
            if (Directory.Exists(ThumbImagesDir))
            {
                foreach (string f in Directory.GetFiles(ThumbImagesDir, "*.png"))
                {
                    try { imgBytes += new FileInfo(f).Length; } catch { }
                }
            }

            long dbBytes = File.Exists(HistoryDbPath) ? new FileInfo(HistoryDbPath).Length : 0;

            return new CacheStats
            {
                CacheDirectory = CacheRoot,
                TotalItems = _db.Items.Count,
                TextItems = _db.Items.Count(x => x.Kind != ClipKind.Image),
                ImageItems = _db.Items.Count(x => x.Kind == ClipKind.Image),
                PinnedItems = _db.Items.Count(x => x.IsPinned),
                TotalImageBytes = imgBytes,
                DatabaseBytes = dbBytes
            };
        }
        finally
        {
            _ioLock.Release();
        }
    }

    public static void ApplyAutoStartSetting(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(AutoRunRegistryKey, writable: true);
            if (key == null) return;

            if (enable)
            {
                string? exePath = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exePath))
                {
                    key.SetValue(AutoRunValueName, $"\"{exePath}\" --tray");
                }
            }
            else
            {
                key.DeleteValue(AutoRunValueName, throwOnMissingValue: false);
            }
        }
        catch
        {
        }
    }

    public static string ComputeSha256(byte[] data)
    {
        byte[] hash = SHA256.HashData(data);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes / (1024.0 * 1024.0):F2} MB";
    }

    private static string BuildTextSummary(string text)
    {
        string normalized = Regex.Replace(text.Trim(), @"\s+", " ");
        return normalized.Length <= 160 ? normalized : normalized[..160] + "…";
    }

    private static string DetectHexColor(string text)
    {
        string t = text.Trim();
        if (t.Length is 7 or 9 && t[0] == '#')
        {
            for (int i = 1; i < t.Length; i++)
            {
                char c = t[i];
                bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!hex) return string.Empty;
            }
            return t.ToUpperInvariant();
        }
        return string.Empty;
    }

    private static bool DetectUrl(string text)
    {
        string t = text.Trim();
        return !t.Contains(' ') &&
               (t.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                t.StartsWith("http://", StringComparison.OrdinalIgnoreCase));
    }
}

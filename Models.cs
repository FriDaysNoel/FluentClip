using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Serialization;

namespace FluentClip;

public enum ClipKind
{
    Text = 0,
    Image = 1,
    Files = 2
}

public sealed class ClipRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public ClipKind Kind { get; set; } = ClipKind.Text;
    public string TextContent { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string ContentHash { get; set; } = string.Empty;
    public string ImageHash { get; set; } = string.Empty;
    public int ImageWidth { get; set; }
    public int ImageHeight { get; set; }
    public long ByteSize { get; set; }
    public List<string> FilePaths { get; set; } = new();
    public string SourceApp { get; set; } = string.Empty;
    public string DetectedColorHex { get; set; } = string.Empty;
    public bool IsUrl { get; set; }
    public bool IsPinned { get; set; }
    public long CreatedUnixMs { get; set; }
    public long UpdatedUnixMs { get; set; }
}

public sealed class HistoryDatabase
{
    public int Version { get; set; } = 1;
    public List<ClipRecord> Items { get; set; } = new();
}

public sealed class BootstrapConfig
{
    public string CacheDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FluentClip",
        "Cache");

    // Default hotkey: Alt + V (MOD_ALT = 0x0001, 'V' = 0x56)
    public uint HotkeyModifiers { get; set; } = 0x0001;
    public uint HotkeyVirtualKey { get; set; } = 0x56;
    public string HotkeyDisplay { get; set; } = "Alt + V";

    public int MaxCacheSizeMb { get; set; } = 500;
    public int MaxHistoryCount { get; set; } = 500;
    public bool FollowCursorOrCaret { get; set; } = true;
    public bool AutoStartOnBoot { get; set; } = false;
    public string ThemeMode { get; set; } = "System"; // System, Dark, Light
}

public sealed class CacheStats
{
    public string CacheDirectory { get; set; } = string.Empty;
    public int TotalItems { get; set; }
    public int TextItems { get; set; }
    public int ImageItems { get; set; }
    public int PinnedItems { get; set; }
    public long TotalImageBytes { get; set; }
    public long DatabaseBytes { get; set; }
    public long TotalBytes => TotalImageBytes + DatabaseBytes;
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(BootstrapConfig))]
[JsonSerializable(typeof(HistoryDatabase))]
[JsonSerializable(typeof(ClipRecord))]
[JsonSerializable(typeof(List<ClipRecord>))]
internal partial class AppJsonContext : JsonSerializerContext
{
}

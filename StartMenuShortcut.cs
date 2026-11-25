using System;
using System.IO;
using System.Runtime.InteropServices;

namespace FluentClip;

/// <summary>
/// Creates / removes "FluentClip.lnk" in the user's Start menu Programs folder.
/// An unpackaged exe is invisible to Start and Windows search unless such a shortcut exists.
/// Uses IShellLinkW / IPersistFile through raw vtable calls, which stays Native-AOT safe
/// (no built-in COM interop / reflection needed).
/// </summary>
internal static unsafe class StartMenuShortcut
{
    private static readonly Guid ClsidShellLink = new("00021401-0000-0000-C000-000000000046");
    private static readonly Guid IidShellLinkW = new("000214F9-0000-0000-C000-000000000046");
    private static readonly Guid IidPersistFile = new("0000010b-0000-0000-C000-000000000046");

    private const uint CLSCTX_INPROC_SERVER = 0x1;

    // IShellLinkW vtable slots (IUnknown occupies 0..2)
    private const int SlotSetDescription = 7;
    private const int SlotSetWorkingDirectory = 9;
    private const int SlotSetIconLocation = 17;
    private const int SlotSetPath = 20;
    // IPersistFile: IUnknown 0..2, IPersist.GetClassID 3, IsDirty 4, Load 5, Save 6
    private const int SlotSave = 6;

    public static string ShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Programs), "FluentClip.lnk");

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(Guid* rclsid, nint pUnkOuter, uint dwClsContext, Guid* riid, nint* ppv);

    /// <summary>Brings the shortcut in line with the setting. Returns true when the config changed and should be saved.</summary>
    public static bool Sync(BootstrapConfig config)
    {
        try
        {
            if (!config.ShowInStartMenu)
            {
                if (File.Exists(ShortcutPath))
                    File.Delete(ShortcutPath);
                if (config.StartMenuShortcutTarget.Length == 0)
                    return false;
                config.StartMenuShortcutTarget = string.Empty;
                return true;
            }

            string? exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe))
                return false;

            // Re-create when missing or when the exe was moved / a newer copy runs from elsewhere.
            if (File.Exists(ShortcutPath) &&
                string.Equals(config.StartMenuShortcutTarget, exe, StringComparison.OrdinalIgnoreCase))
                return false;

            Create(exe, ShortcutPath);
            config.StartMenuShortcutTarget = exe;
            return true;
        }
        catch (Exception ex)
        {
            App.LogError("StartMenuShortcut", ex);
            return false;
        }
    }

    private static void Create(string exePath, string linkPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(linkPath)!);

        Guid clsid = ClsidShellLink;
        Guid iidLink = IidShellLinkW;
        nint link = 0;
        Check(CoCreateInstance(&clsid, 0, CLSCTX_INPROC_SERVER, &iidLink, &link));

        nint persist = 0;
        try
        {
            void** vtbl = *(void***)link;
            string workDir = Path.GetDirectoryName(exePath) ?? string.Empty;
            const string description = "FluentClip 剪贴板历史管理器";

            fixed (char* p = exePath)
                Check(((delegate* unmanaged[Stdcall]<nint, char*, int>)vtbl[SlotSetPath])(link, p));
            fixed (char* p = workDir)
                Check(((delegate* unmanaged[Stdcall]<nint, char*, int>)vtbl[SlotSetWorkingDirectory])(link, p));
            fixed (char* p = description)
                Check(((delegate* unmanaged[Stdcall]<nint, char*, int>)vtbl[SlotSetDescription])(link, p));
            fixed (char* p = exePath)
                Check(((delegate* unmanaged[Stdcall]<nint, char*, int, int>)vtbl[SlotSetIconLocation])(link, p, 0));

            Guid iidPersist = IidPersistFile;
            Check(((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)vtbl[0])(link, &iidPersist, &persist));

            void** pvtbl = *(void***)persist;
            fixed (char* p = linkPath)
                Check(((delegate* unmanaged[Stdcall]<nint, char*, int, int>)pvtbl[SlotSave])(persist, p, 1));
        }
        finally
        {
            if (persist != 0)
                ((delegate* unmanaged[Stdcall]<nint, uint>)(*(void***)persist)[2])(persist);
            ((delegate* unmanaged[Stdcall]<nint, uint>)(*(void***)link)[2])(link);
        }
    }

    private static void Check(int hr)
    {
        if (hr < 0)
            Marshal.ThrowExceptionForHR(hr);
    }
}

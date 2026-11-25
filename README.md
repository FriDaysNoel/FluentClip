# FluentClip

A lightweight, native Windows 11 Clipboard History Manager built with **WinUI 3 (Windows App SDK)** and **.NET 9 Native AOT**.

## Highlights
- **Pure Native AOT Machine Code**: Ahead-of-time compiled to native x64 instructions (`PublishAot=true`), zero JIT warmup, unpackaged standalone execution (`FluentClip.exe`).
- **Event-Driven Zero Polling**: Uses Win32 `AddClipboardFormatListener` (`0.0%` idle CPU) and respects password manager exclusion markers (`ExcludeClipboardContentFromMonitorProcessing` / `CanIncludeInClipboardHistory`).
- **Lossless Dual-Format Image Paste**: Captures `PNG` stream (preserving alpha channel transparency), `CF_DIBV5`/`CF_DIB`, and Explorer `CF_HDROP` image files; concurrently writes `PNG` + `CF_DIB` back on paste for universal app compatibility, plus `Shift+Click` to paste as a `.png` file.
- **Custom Cache Directory & Deduplication**: SHA-256 content-addressed original & thumbnail image store, configurable storage quota, pinned item protection, and one-click atomic cache folder migration.
- **Windows 11 Fluent Design**: Acrylic & Mica backdrops, DWM rounded corners, smart caret/cursor positioning (`Alt + V` default), instant search, color hex swatches, and full-resolution image lightbox.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using ComicReaderDesktop.Models;
using SharpCompress.Archives;

namespace ComicReaderDesktop
{
    public static class ArchiveComicHelper
    {
        public static readonly HashSet<string> SupportedArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".zip", ".cbz", ".rar", ".cbr", ".7z", ".cb7", ".tar", ".cbt"
        };

        private static readonly HashSet<string> SupportedImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".webp", ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".avif"
        };

        public static bool IsArchiveFile(string? path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string ext = Path.GetExtension(path);
            return !string.IsNullOrEmpty(ext) && SupportedArchiveExtensions.Contains(ext);
        }

        public static bool IsImageFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            // 过滤 macOS 自动生成的垃圾元数据条目
            if (name.Contains("__MACOSX") || name.StartsWith("._") || name.Contains("/._") || name.Contains("\\._"))
                return false;

            string ext = Path.GetExtension(name);
            return !string.IsNullOrEmpty(ext) && SupportedImageExtensions.Contains(ext);
        }

        public static string BuildPagePath(string archivePath, string entryKey)
        {
            return $"{archivePath}|{entryKey}";
        }

        public static bool TryParsePagePath(string? path, out string archivePath, out string entryKey)
        {
            archivePath = "";
            entryKey = "";
            if (string.IsNullOrEmpty(path)) return false;

            int idx = path.IndexOf('|');
            if (idx > 0 && idx < path.Length - 1)
            {
                archivePath = path.Substring(0, idx);
                entryKey = path.Substring(idx + 1);
                return true;
            }
            return false;
        }

        /// <summary>
        /// 获取压缩包内所有漫画图片切片的相对路径条目列表 (带自然排序)
        /// </summary>
        public static List<string> GetArchiveImageEntries(string archivePath)
        {
            var result = new List<string>();
            if (!File.Exists(archivePath)) return result;

            string ext = Path.GetExtension(archivePath).ToLowerInvariant();

            // 优先针对 .zip 和 .cbz 使用 .NET 原生高并发 ZipArchive 解析 (极速秒读目录头)
            if (ext == ".zip" || ext == ".cbz")
            {
                try
                {
                    using var fs = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
                    foreach (var entry in zip.Entries)
                    {
                        if (!string.IsNullOrEmpty(entry.Name) && IsImageFileName(entry.FullName))
                        {
                            result.Add(entry.FullName);
                        }
                    }
                    result.Sort(NaturalStringComparer.Instance);
                    return result;
                }
                catch
                {
                    // 若原生 Zip 解析失败，平滑进入 SharpCompress 兜底
                    result.Clear();
                }
            }

            // 针对 .rar, .cbr, .7z, .cb7, .tar 等使用 SharpCompress 跨平台流式解析
            try
            {
                using var fs = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var archive = ArchiveFactory.OpenArchive(fs);
                foreach (var entry in archive.Entries)
                {
                    if (!entry.IsDirectory && !string.IsNullOrEmpty(entry.Key) && IsImageFileName(entry.Key))
                    {
                        result.Add(entry.Key);
                    }
                }
                result.Sort(NaturalStringComparer.Instance);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"解析压缩包漫画 [{archivePath}] 失败: {ex.Message}");
            }

            return result;
        }

        /// <summary>
        /// 从压缩包中直接流式提取指定图片的原始二进制数据（零临时磁盘写入）
        /// </summary>
        public static byte[]? ExtractEntryBytes(string archivePath, string entryKey)
        {
            if (!File.Exists(archivePath) || string.IsNullOrEmpty(entryKey)) return null;

            entryKey = entryKey.Trim().TrimStart('/', '\\');
            string ext = Path.GetExtension(archivePath).ToLowerInvariant();

            if (ext == ".zip" || ext == ".cbz")
            {
                try
                {
                    using var fs = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
                    var entry = zip.GetEntry(entryKey) ??
                                zip.Entries.FirstOrDefault(e => string.Equals(e.FullName, entryKey, StringComparison.OrdinalIgnoreCase)) ??
                                zip.Entries.FirstOrDefault(e => string.Equals(e.FullName.Replace('\\', '/'), entryKey.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase));

                    if (entry != null)
                    {
                        using var entryStream = entry.Open();
                        using var ms = new MemoryStream((int)Math.Max(1024, entry.Length));
                        entryStream.CopyTo(ms);
                        return ms.ToArray();
                    }
                }
                catch
                {
                    // 兜底进入通用解析
                }
            }

            try
            {
                using var fs = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var archive = ArchiveFactory.OpenArchive(fs);
                var entry = archive.Entries.FirstOrDefault(e =>
                    !e.IsDirectory && (
                        string.Equals(e.Key, entryKey, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(e.Key?.Replace('\\', '/'), entryKey.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)
                    ));

                if (entry != null)
                {
                    using var entryStream = entry.OpenEntryStream();
                    using var ms = new MemoryStream();
                    entryStream.CopyTo(ms);
                    return ms.ToArray();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"读取压缩包切片 [{archivePath}|{entryKey}] 失败: {ex.Message}");
            }

            return null;
        }
    }
}

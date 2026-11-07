using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ComicReaderDesktop.Models
{
    public class ShelfDatabase
    {
        [JsonPropertyName("comics")]
        public List<ComicRecord> Comics { get; set; } = new();

        [JsonPropertyName("history")]
        public Dictionary<string, ComicHistoryRecord> History { get; set; } = new();

        [JsonPropertyName("lastRootFolder")]
        public string LastRootFolder { get; set; } = "";

        [JsonPropertyName("sortMode")]
        public string SortMode { get; set; } = "title_asc";

        [JsonPropertyName("pageSize")]
        public int PageSize { get; set; } = 20;

        [JsonPropertyName("readerWidth")]
        public string ReaderWidth { get; set; } = "900px";

        [JsonPropertyName("transferSavePath")]
        public string TransferSavePath { get; set; } = "";
    }

    public class LanPeerInfo
    {
        [JsonPropertyName("app")]
        public string App { get; set; } = "YYComicReader";

        [JsonPropertyName("deviceName")]
        public string DeviceName { get; set; } = "";

        [JsonPropertyName("platform")]
        public string Platform { get; set; } = "";

        [JsonPropertyName("ip")]
        public string Ip { get; set; } = "";

        [JsonPropertyName("port")]
        public int Port { get; set; } = 39528;

        [JsonPropertyName("comicCount")]
        public int ComicCount { get; set; }
    }

    public class LanRemoteComicInfo
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [JsonPropertyName("title")]
        public string Title { get; set; } = "";

        [JsonPropertyName("pageCount")]
        public int PageCount { get; set; }

        [JsonPropertyName("readCount")]
        public int ReadCount { get; set; }

        [JsonPropertyName("lastReadTime")]
        public long LastReadTime { get; set; }

        [JsonPropertyName("lastReadPage")]
        public int LastReadPage { get; set; } = 1;

        [JsonPropertyName("importTime")]
        public long ImportTime { get; set; }
    }

    public class LanComicListResponse
    {
        [JsonPropertyName("deviceName")]
        public string DeviceName { get; set; } = "";

        [JsonPropertyName("platform")]
        public string Platform { get; set; } = "";

        [JsonPropertyName("comics")]
        public List<LanRemoteComicInfo> Comics { get; set; } = new();
    }

    public class LanPageEntry
    {
        [JsonPropertyName("index")]
        public int Index { get; set; }

        [JsonPropertyName("fileName")]
        public string FileName { get; set; } = "";

        [JsonPropertyName("size")]
        public long Size { get; set; }
    }

    public class LanComicManifest
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [JsonPropertyName("title")]
        public string Title { get; set; } = "";

        [JsonPropertyName("pageCount")]
        public int PageCount { get; set; }

        [JsonPropertyName("pages")]
        public List<LanPageEntry> Pages { get; set; } = new();
    }

    public class ComicRecord
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [JsonPropertyName("title")]
        public string Title { get; set; } = "";

        [JsonPropertyName("subDirName")]
        public string SubDirName { get; set; } = "";

        [JsonPropertyName("folderPath")]
        public string FolderPath { get; set; } = "";

        [JsonPropertyName("coverPath")]
        public string CoverPath { get; set; } = "";

        [JsonPropertyName("customCoverPath")]
        public string CustomCoverPath { get; set; } = "";

        [JsonPropertyName("pagePaths")]
        public List<string> PagePaths { get; set; } = new();

        [JsonPropertyName("totalPages")]
        public int TotalPages { get; set; }

        [JsonPropertyName("importTime")]
        public long ImportTime { get; set; }

        [JsonPropertyName("readCount")]
        public int ReadCount { get; set; }

        [JsonPropertyName("lastReadTime")]
        public long LastReadTime { get; set; }

        [JsonPropertyName("lastReadPage")]
        public int LastReadPage { get; set; } = 1;

        [JsonPropertyName("isMissing")]
        public bool IsMissing { get; set; }
    }

    public class ComicHistoryRecord
    {
        [JsonPropertyName("readCount")]
        public int ReadCount { get; set; }

        [JsonPropertyName("lastReadTime")]
        public long LastReadTime { get; set; }

        [JsonPropertyName("lastReadPage")]
        public int LastReadPage { get; set; } = 1;

        [JsonPropertyName("importTime")]
        public long ImportTime { get; set; }

        [JsonPropertyName("customCoverPath")]
        public string CustomCoverPath { get; set; } = "";
    }

    /// <summary>
    /// 自然排序比较器 (支持 1.jpg, 2.jpg, 10.jpg 按数值大小排序)
    /// </summary>
    public class NaturalStringComparer : IComparer<string>
    {
        public static readonly NaturalStringComparer Instance = new();

        public int Compare(string? a, string? b)
        {
            if (ReferenceEquals(a, b)) return 0;
            if (a == null) return -1;
            if (b == null) return 1;

            int ia = 0, ib = 0;
            int la = a.Length, lb = b.Length;

            while (ia < la && ib < lb)
            {
                char ca = a[ia];
                char cb = b[ib];

                if (char.IsDigit(ca) && char.IsDigit(cb))
                {
                    // 提取数字段
                    int startA = ia;
                    while (ia < la && char.IsDigit(a[ia])) ia++;
                    int startB = ib;
                    while (ib < lb && char.IsDigit(b[ib])) ib++;

                    ReadOnlySpan<char> spanA = a.AsSpan(startA, ia - startA).TrimStart('0');
                    ReadOnlySpan<char> spanB = b.AsSpan(startB, ib - startB).TrimStart('0');

                    if (spanA.Length != spanB.Length)
                        return spanA.Length.CompareTo(spanB.Length);

                    int numCmp = spanA.SequenceCompareTo(spanB);
                    if (numCmp != 0)
                        return numCmp;

                    // 前导零个数不同时
                    int lenDiff = (ia - startA).CompareTo(ib - startB);
                    if (lenDiff != 0)
                        return lenDiff;
                }
                else
                {
                    char ua = char.ToLowerInvariant(ca);
                    char ub = char.ToLowerInvariant(cb);
                    if (ua != ub)
                        return ua.CompareTo(ub);
                    ia++;
                    ib++;
                }
            }

            return (la - ia).CompareTo(lb - ib);
        }
    }
}

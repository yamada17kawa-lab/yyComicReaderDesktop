using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using ComicReaderDesktop.Models;

namespace ComicReaderDesktop
{
    public class LanTransferService : IDisposable
    {
        public const int DISCOVERY_UDP_PORT = 39527;
        public const int DEFAULT_HTTP_PORT = 39528;

        private readonly Func<List<ComicRecord>> getActiveComics;
        private readonly Func<string> getTransferSaveDir;
        private readonly Action<string, string> onComicReceived;
        private readonly Action<string, int, string> onTransferProgress;
        private readonly Action<LanPeerInfo>? onPeerConnected;
        private readonly Action<string, string, int, int>? onBatchTransferComplete;
        private readonly Func<string, int, int?>? onRemoteComicRead;

        private TcpListener? tcpListener;
        private UdpClient? udpListener;
        private CancellationTokenSource? cts;
        private readonly HttpClient httpClient;
        private readonly HttpClient probeHttpClient;

        public int ListeningPort { get; private set; } = DEFAULT_HTTP_PORT;
        public string DeviceName { get; } = Environment.MachineName;

        public LanTransferService(
            Func<List<ComicRecord>> getActiveComics,
            Func<string> getTransferSaveDir,
            Action<string, string> onComicReceived,
            Action<string, int, string> onTransferProgress,
            Action<LanPeerInfo>? onPeerConnected = null,
            Action<string, string, int, int>? onBatchTransferComplete = null,
            Func<string, int, int?>? onRemoteComicRead = null)
        {
            this.getActiveComics = getActiveComics;
            this.getTransferSaveDir = getTransferSaveDir;
            this.onComicReceived = onComicReceived;
            this.onTransferProgress = onTransferProgress;
            this.onPeerConnected = onPeerConnected;
            this.onBatchTransferComplete = onBatchTransferComplete;
            this.onRemoteComicRead = onRemoteComicRead;

            httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            probeHttpClient = new HttpClient { Timeout = TimeSpan.FromMilliseconds(650) };
        }

        public void Start()
        {
            cts = new CancellationTokenSource();

            // 1. 启动 HTTP TCP 服务端 (优先 39528，若被占用顺延)
            for (int port = DEFAULT_HTTP_PORT; port <= DEFAULT_HTTP_PORT + 7; port++)
            {
                try
                {
                    tcpListener = new TcpListener(IPAddress.Any, port);
                    tcpListener.Start();
                    ListeningPort = port;
                    Task.Run(() => AcceptTcpClientsLoopAsync(cts.Token));
                    break;
                }
                catch
                {
                    tcpListener = null;
                }
            }

            // 2. 启动 UDP 局域网广播发现监听
            try
            {
                udpListener = new UdpClient();
                udpListener.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                udpListener.Client.Bind(new IPEndPoint(IPAddress.Any, DISCOVERY_UDP_PORT));
                udpListener.EnableBroadcast = true;
                Task.Run(() => ListenUdpDiscoveryLoopAsync(cts.Token));
            }
            catch
            {
                // Ignore if UDP port is already bound
            }
        }

        public List<string> GetLocalLanIps()
        {
            var result = new List<(string ip, int score)>();
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                    string desc = (ni.Name + " " + ni.Description).ToLowerInvariant();
                    bool isVirtual = desc.Contains("virtual") || desc.Contains("vmware") ||
                                     desc.Contains("vbox") || desc.Contains("wsl") ||
                                     desc.Contains("hyper-v") || desc.Contains("docker") ||
                                     desc.Contains("pseudo") || desc.Contains("tap") || desc.Contains("tun");

                    int baseScore = isVirtual ? 1 : 10;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211) baseScore += 5;
                    else if (ni.NetworkInterfaceType == NetworkInterfaceType.Ethernet) baseScore += 3;

                    var ipProps = ni.GetIPProperties();
                    bool hasGateway = ipProps.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
                    if (hasGateway) baseScore += 10;

                    foreach (var ua in ipProps.UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ua.Address))
                        {
                            string ipStr = ua.Address.ToString();
                            if (ipStr.StartsWith("169.254.")) continue;
                            int ipScore = baseScore;
                            if (ipStr.StartsWith("192.168.")) ipScore += 5;
                            else if (ipStr.StartsWith("10.") || ipStr.StartsWith("172.")) ipScore += 3;
                            result.Add((ipStr, ipScore));
                        }
                    }
                }
            }
            catch { }

            if (result.Count == 0)
            {
                try
                {
                    foreach (var addr in Dns.GetHostAddresses(Dns.GetHostName()))
                    {
                        if (addr.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(addr))
                        {
                            result.Add((addr.ToString(), 1));
                        }
                    }
                }
                catch { }
            }

            return result
                .OrderByDescending(x => x.score)
                .Select(x => x.ip)
                .Distinct()
                .ToList();
        }

        public LanPeerInfo BuildSelfPeerInfo(string? preferredIp = null)
        {
            var ips = GetLocalLanIps();
            string ip = preferredIp ?? (ips.Count > 0 ? ips[0] : "127.0.0.1");
            int count = 0;
            try { count = getActiveComics().Count; } catch { }
            return new LanPeerInfo
            {
                App = "YYComicReader",
                DeviceName = DeviceName,
                Platform = "Windows",
                Ip = ip,
                Port = ListeningPort,
                ComicCount = count
            };
        }

        private async Task ListenUdpDiscoveryLoopAsync(CancellationToken token)
        {
            if (udpListener == null) return;
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var res = await udpListener.ReceiveAsync(token);
                    string msg = Encoding.UTF8.GetString(res.Buffer);
                    if (msg.StartsWith("YYCOMIC_DISCOVER", StringComparison.Ordinal))
                    {
                        string localIp = MatchBestLocalIpForRemote(res.RemoteEndPoint.Address);
                        var info = BuildSelfPeerInfo(localIp);
                        string reply = "YYCOMIC_HERE|" + JsonSerializer.Serialize(info);
                        byte[] replyBytes = Encoding.UTF8.GetBytes(reply);
                        await udpListener.SendAsync(replyBytes, replyBytes.Length, res.RemoteEndPoint);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    await Task.Delay(200, token);
                }
            }
        }

        private string MatchBestLocalIpForRemote(IPAddress remoteAddr)
        {
            var ips = GetLocalLanIps();
            string remoteStr = remoteAddr.ToString();
            int lastDot = remoteStr.LastIndexOf('.');
            if (lastDot > 0)
            {
                string prefix = remoteStr.Substring(0, lastDot + 1);
                var match = ips.FirstOrDefault(ip => ip.StartsWith(prefix, StringComparison.Ordinal));
                if (match != null) return match;
            }
            return ips.FirstOrDefault() ?? "127.0.0.1";
        }

        public async Task<List<LanPeerInfo>> DiscoverPeersAsync()
        {
            var foundPeers = new ConcurrentDictionary<string, LanPeerInfo>(StringComparer.OrdinalIgnoreCase);
            var localIps = new HashSet<string>(GetLocalLanIps(), StringComparer.OrdinalIgnoreCase) { "127.0.0.1", "localhost" };

            // 1. UDP 广播探测
            var udpTask = Task.Run(async () =>
            {
                try
                {
                    using var client = new UdpClient();
                    client.EnableBroadcast = true;
                    client.Client.ReceiveTimeout = 900;
                    byte[] reqBytes = Encoding.UTF8.GetBytes("YYCOMIC_DISCOVER");

                    await client.SendAsync(reqBytes, reqBytes.Length, new IPEndPoint(IPAddress.Broadcast, DISCOVERY_UDP_PORT));
                    foreach (var myIp in GetLocalLanIps())
                    {
                        int dot = myIp.LastIndexOf('.');
                        if (dot > 0)
                        {
                            string bcast = myIp.Substring(0, dot + 1) + "255";
                            if (IPAddress.TryParse(bcast, out var bcastIp))
                            {
                                await client.SendAsync(reqBytes, reqBytes.Length, new IPEndPoint(bcastIp, DISCOVERY_UDP_PORT));
                            }
                        }
                    }

                    using var timeoutCts = new CancellationTokenSource(950);
                    while (!timeoutCts.IsCancellationRequested)
                    {
                        var res = await client.ReceiveAsync(timeoutCts.Token);
                        string text = Encoding.UTF8.GetString(res.Buffer);
                        if (text.StartsWith("YYCOMIC_HERE|", StringComparison.Ordinal))
                        {
                            string json = text.Substring("YYCOMIC_HERE|".Length);
                            var peer = JsonSerializer.Deserialize<LanPeerInfo>(json);
                            if (peer != null)
                            {
                                string remoteIp = res.RemoteEndPoint.Address.ToString();
                                if (!localIps.Contains(remoteIp) || peer.Port != ListeningPort)
                                {
                                    peer.Ip = remoteIp;
                                    foundPeers[$"{peer.Ip}:{peer.Port}"] = peer;
                                }
                            }
                        }
                    }
                }
                catch { }
            });

            // 2. 同时对当前主用 Wi-Fi 网段进行快速 HTTP 端口探测 (兼容路由器隔离 UDP 广播或防火墙拦截)
            var subnetTask = Task.Run(async () =>
            {
                var primaryIps = GetLocalLanIps().Take(2).ToList();
                var candidateIps = new List<string>();
                foreach (var myIp in primaryIps)
                {
                    int dot = myIp.LastIndexOf('.');
                    if (dot > 0)
                    {
                        string prefix = myIp.Substring(0, dot + 1);
                        for (int i = 1; i <= 254; i++)
                        {
                            string targetIp = prefix + i;
                            if (!localIps.Contains(targetIp))
                            {
                                candidateIps.Add(targetIp);
                            }
                        }
                    }
                }

                await Parallel.ForEachAsync(candidateIps, new ParallelOptions { MaxDegreeOfParallelism = 48 }, async (ip, ct) =>
                {
                    string key = $"{ip}:{DEFAULT_HTTP_PORT}";
                    if (foundPeers.ContainsKey(key)) return;
                    var peer = await PingPeerAsync(ip, DEFAULT_HTTP_PORT, true);
                    if (peer != null)
                    {
                        foundPeers[$"{peer.Ip}:{peer.Port}"] = peer;
                    }
                });
            });

            await Task.WhenAll(udpTask, subnetTask);
            return foundPeers.Values.OrderBy(p => p.DeviceName).ToList();
        }

        public async Task<LanPeerInfo?> PingPeerAsync(string ipOrHost, int port = DEFAULT_HTTP_PORT, bool fastProbe = false)
        {
            try
            {
                string cleanHost = ipOrHost.Trim();
                if (cleanHost.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                    cleanHost = cleanHost.Substring(7);
                cleanHost = cleanHost.TrimEnd('/');

                if (cleanHost.Contains(':'))
                {
                    var parts = cleanHost.Split(':');
                    cleanHost = parts[0];
                    if (parts.Length > 1 && int.TryParse(parts[1], out int parsedPort))
                        port = parsedPort;
                }

                var client = fastProbe ? probeHttpClient : httpClient;
                string url = $"http://{cleanHost}:{port}/api/ping";
                using var resp = await client.GetAsync(url);
                if (!resp.IsSuccessStatusCode) return null;

                string json = await resp.Content.ReadAsStringAsync();
                var info = JsonSerializer.Deserialize<LanPeerInfo>(json);
                if (info != null && info.App == "YYComicReader")
                {
                    info.Ip = cleanHost;
                    info.Port = port;
                    return info;
                }
            }
            catch { }
            return null;
        }

        public async Task<LanComicListResponse?> FetchRemoteComicsAsync(string ip, int port, bool notifyConnect = true)
        {
            string url = $"http://{ip}:{port}/api/comics";
            if (notifyConnect)
            {
                int myCount = 0;
                try { myCount = getActiveComics().Count; } catch { }
                url += $"?peerName={Uri.EscapeDataString(DeviceName)}&peerPlatform=Windows&peerPort={ListeningPort}&peerComics={myCount}";
            }
            using var resp = await httpClient.GetAsync(url);
            resp.EnsureSuccessStatusCode();
            string json = await resp.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<LanComicListResponse>(json);
        }

        public static string NormalizeTitleKey(string? title)
        {
            return (title ?? "").Trim().ToLowerInvariant();
        }

        public bool HasLocalActiveComic(string? title)
        {
            string key = NormalizeTitleKey(title);
            if (string.IsNullOrEmpty(key)) return false;
            return getActiveComics().Any(c => NormalizeTitleKey(c.Title) == key || NormalizeTitleKey(SanitizeName(c.Title)) == NormalizeTitleKey(SanitizeName(title ?? "")));
        }

        public async Task<(int downloadedCount, int skippedCount, List<string> skippedTitles)> PullComicsFromPeerAsync(string ip, int port, List<string> comicIds)
        {
            var skippedTitles = new List<string>();
            if (comicIds == null || comicIds.Count == 0) return (0, 0, skippedTitles);

            string baseSaveDir = getTransferSaveDir();
            Directory.CreateDirectory(baseSaveDir);

            int completedComics = 0;
            int totalComics = comicIds.Count;

            for (int cIdx = 0; cIdx < totalComics; cIdx++)
            {
                string comicId = comicIds[cIdx];
                string manifestUrl = $"http://{ip}:{port}/api/comic-manifest?id={Uri.EscapeDataString(comicId)}";
                using var mResp = await httpClient.GetAsync(manifestUrl);
                if (!mResp.IsSuccessStatusCode) continue;

                string mJson = await mResp.Content.ReadAsStringAsync();
                var manifest = JsonSerializer.Deserialize<LanComicManifest>(mJson);
                if (manifest == null || manifest.Pages.Count == 0) continue;

                // 检查本机是否已拥有该漫画
                if (HasLocalActiveComic(manifest.Title))
                {
                    skippedTitles.Add(manifest.Title);
                    continue;
                }

                string safeFolder = SanitizeName(manifest.Title);
                string comicFolder = Path.Combine(baseSaveDir, safeFolder);
                Directory.CreateDirectory(comicFolder);

                int pageTotal = manifest.Pages.Count;
                for (int pIdx = 0; pIdx < pageTotal; pIdx++)
                {
                    var page = manifest.Pages[pIdx];
                    int overallPct = (int)Math.Round(((cIdx + (double)(pIdx + 1) / pageTotal) / totalComics) * 100);
                    onTransferProgress(
                        $"📥 正在接收 ({cIdx + 1}/{totalComics}) 《{manifest.Title}》",
                        overallPct,
                        $"正在下载第 {pIdx + 1}/{pageTotal} 页 ({page.FileName})..."
                    );

                    string pageUrl = $"http://{ip}:{port}/api/comic-page?id={Uri.EscapeDataString(comicId)}&index={page.Index}&comicIndex={cIdx + 1}&totalComics={totalComics}&pageTotal={pageTotal}&peerName={Uri.EscapeDataString(DeviceName)}";
                    using var pResp = await httpClient.GetAsync(pageUrl, HttpCompletionOption.ResponseHeadersRead);
                    if (pResp.IsSuccessStatusCode)
                    {
                        string safeFile = SanitizeName(string.IsNullOrWhiteSpace(page.FileName) ? $"{pIdx + 1:D4}.jpg" : page.FileName);
                        string targetFile = Path.Combine(comicFolder, safeFile);
                        await using var netStream = await pResp.Content.ReadAsStreamAsync();
                        await using var fs = new FileStream(targetFile, FileMode.Create, FileAccess.Write, FileShare.Read);
                        await netStream.CopyToAsync(fs);
                    }
                }

                completedComics++;
                onComicReceived(manifest.Title, comicFolder);
            }

            try
            {
                string batchUrl = $"http://{ip}:{port}/api/batch-complete?mode=pull&peerName={Uri.EscapeDataString(DeviceName)}&transferredCount={completedComics}&skippedCount={skippedTitles.Count}";
                using var bContent = new StringContent("{}", Encoding.UTF8, "application/json");
                await httpClient.PostAsync(batchUrl, bContent);
            }
            catch { }

            return (completedComics, skippedTitles.Count, skippedTitles);
        }

        public async Task<(int sentCount, int skippedCount, List<string> skippedTitles)> PushComicsToPeerAsync(string ip, int port, List<ComicRecord> comics)
        {
            var skippedTitles = new List<string>();
            if (comics == null || comics.Count == 0) return (0, 0, skippedTitles);

            // 先查询对方设备已拥有的漫画列表，若对方已拥有则跳过不传送
            var remoteExistingKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var remoteList = await FetchRemoteComicsAsync(ip, port, false);
                if (remoteList?.Comics != null)
                {
                    foreach (var rc in remoteList.Comics)
                    {
                        remoteExistingKeys.Add(NormalizeTitleKey(rc.Title));
                        remoteExistingKeys.Add(NormalizeTitleKey(SanitizeName(rc.Title)));
                    }
                }
            }
            catch { }

            int sentComics = 0;
            int totalComics = comics.Count;

            for (int cIdx = 0; cIdx < totalComics; cIdx++)
            {
                var comic = comics[cIdx];
                if (remoteExistingKeys.Contains(NormalizeTitleKey(comic.Title)) ||
                    remoteExistingKeys.Contains(NormalizeTitleKey(SanitizeName(comic.Title))))
                {
                    skippedTitles.Add(comic.Title);
                    continue;
                }

                var validPages = comic.PagePaths.Where(File.Exists).ToList();
                if (validPages.Count == 0) continue;

                int pageTotal = validPages.Count;
                bool peerReportedExists = false;

                for (int pIdx = 0; pIdx < pageTotal; pIdx++)
                {
                    string filePath = validPages[pIdx];
                    string fileName = Path.GetFileName(filePath);
                    int overallPct = (int)Math.Round(((cIdx + (double)(pIdx + 1) / pageTotal) / totalComics) * 100);

                    onTransferProgress(
                        $"📤 正在发送 ({cIdx + 1}/{totalComics}) 《{comic.Title}》",
                        overallPct,
                        $"正在上传第 {pIdx + 1}/{pageTotal} 页 ({fileName})..."
                    );

                    string uploadUrl = $"http://{ip}:{port}/api/upload-page?comicTitle={Uri.EscapeDataString(comic.Title)}&fileName={Uri.EscapeDataString(fileName)}&index={pIdx}&total={pageTotal}&comicIndex={cIdx + 1}&totalComics={totalComics}&peerName={Uri.EscapeDataString(DeviceName)}";
                    byte[] fileBytes = await File.ReadAllBytesAsync(filePath);
                    using var content = new ByteArrayContent(fileBytes);
                    content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
                    using var resp = await httpClient.PostAsync(uploadUrl, content);
                    resp.EnsureSuccessStatusCode();

                    string respText = await resp.Content.ReadAsStringAsync();
                    if (respText.Contains("\"alreadyExists\":true", StringComparison.OrdinalIgnoreCase))
                    {
                        peerReportedExists = true;
                        break;
                    }
                }

                if (peerReportedExists)
                {
                    skippedTitles.Add(comic.Title);
                    continue;
                }

                string completeUrl = $"http://{ip}:{port}/api/upload-complete?comicTitle={Uri.EscapeDataString(comic.Title)}";
                using var cContent = new StringContent("{}", Encoding.UTF8, "application/json");
                await httpClient.PostAsync(completeUrl, cContent);

                sentComics++;
            }

            try
            {
                string batchUrl = $"http://{ip}:{port}/api/batch-complete?mode=push&peerName={Uri.EscapeDataString(DeviceName)}&transferredCount={sentComics}&skippedCount={skippedTitles.Count}";
                using var bContent = new StringContent("{}", Encoding.UTF8, "application/json");
                await httpClient.PostAsync(batchUrl, bContent);
            }
            catch { }

            return (sentComics, skippedTitles.Count, skippedTitles);
        }

        // ========================= 轻量级无权限要求 HTTP 服务端 =========================
        private async Task AcceptTcpClientsLoopAsync(CancellationToken token)
        {
            if (tcpListener == null) return;
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var client = await tcpListener.AcceptTcpClientAsync(token);
                    _ = Task.Run(() => HandleHttpClientAsync(client, token), token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    await Task.Delay(100, token);
                }
            }
        }

        private async Task HandleHttpClientAsync(TcpClient client, CancellationToken token)
        {
            using (client)
            {
                try
                {
                    client.ReceiveTimeout = 60000;
                    client.SendTimeout = 60000;
                    await using var stream = client.GetStream();

                    // 读取 HTTP 请求头
                    var headerBuffer = new List<byte>(1024);
                    byte[] oneByte = new byte[1];
                    int headerEndIndex = -1;

                    while (headerBuffer.Count < 32768)
                    {
                        int n = await stream.ReadAsync(oneByte.AsMemory(0, 1), token);
                        if (n == 0) return;
                        headerBuffer.Add(oneByte[0]);
                        int cnt = headerBuffer.Count;
                        if (cnt >= 4 &&
                            headerBuffer[cnt - 4] == '\r' &&
                            headerBuffer[cnt - 3] == '\n' &&
                            headerBuffer[cnt - 2] == '\r' &&
                            headerBuffer[cnt - 1] == '\n')
                        {
                            headerEndIndex = cnt;
                            break;
                        }
                    }

                    if (headerEndIndex < 0) return;

                    string headerStr = Encoding.UTF8.GetString(headerBuffer.ToArray());
                    string[] lines = headerStr.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
                    if (lines.Length == 0) return;

                    string[] reqLine = lines[0].Split(' ');
                    if (reqLine.Length < 2) return;

                    string method = reqLine[0].ToUpperInvariant();
                    string rawTarget = reqLine[1];

                    long contentLength = 0;
                    foreach (var line in lines.Skip(1))
                    {
                        if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                        {
                            long.TryParse(line.Substring("Content-Length:".Length).Trim(), out contentLength);
                        }
                    }

                    string path = rawTarget;
                    string queryStr = "";
                    int qIdx = rawTarget.IndexOf('?');
                    if (qIdx >= 0)
                    {
                        path = rawTarget.Substring(0, qIdx);
                        queryStr = rawTarget.Substring(qIdx + 1);
                    }
                    var query = HttpUtility.ParseQueryString(queryStr);

                    if (method == "GET" && path.Equals("/api/ping", StringComparison.OrdinalIgnoreCase))
                    {
                        var remoteEp = client.Client.RemoteEndPoint as IPEndPoint;
                        string bestIp = remoteEp != null ? MatchBestLocalIpForRemote(remoteEp.Address) : (GetLocalLanIps().FirstOrDefault() ?? "127.0.0.1");
                        string json = JsonSerializer.Serialize(BuildSelfPeerInfo(bestIp));
                        await WriteJsonResponseAsync(stream, 200, json, token);
                    }
                    else if ((method == "POST" || method == "GET") && path.Equals("/api/record-read", StringComparison.OrdinalIgnoreCase))
                    {
                        string? id = query["id"];
                        int.TryParse(query["page"], out int page);
                        int? updatedCount = null;
                        if (!string.IsNullOrEmpty(id) && onRemoteComicRead != null)
                        {
                            updatedCount = onRemoteComicRead(id, page);
                        }

                        if (updatedCount.HasValue)
                        {
                            string json = $"{{\"ok\":true,\"id\":{JsonSerializer.Serialize(id)},\"readCount\":{updatedCount.Value}}}";
                            await WriteJsonResponseAsync(stream, 200, json, token);
                        }
                        else
                        {
                            await WriteJsonResponseAsync(stream, 404, "{\"ok\":false,\"error\":\"Comic not found\"}", token);
                        }
                    }
                    else if (method == "GET" && path.Equals("/api/comics", StringComparison.OrdinalIgnoreCase))
                    {
                        string? peerName = query["peerName"];
                        if (!string.IsNullOrWhiteSpace(peerName))
                        {
                            var remoteEp = client.Client.RemoteEndPoint as IPEndPoint;
                            string remoteIp = remoteEp?.Address.ToString() ?? "";
                            int.TryParse(query["peerPort"], out int peerPort);
                            int.TryParse(query["peerComics"], out int peerComics);
                            if (!string.IsNullOrEmpty(remoteIp))
                            {
                                onPeerConnected?.Invoke(new LanPeerInfo
                                {
                                    App = "YYComicReader",
                                    DeviceName = peerName,
                                    Platform = query["peerPlatform"] ?? "Android",
                                    Ip = remoteIp,
                                    Port = peerPort > 0 ? peerPort : DEFAULT_HTTP_PORT,
                                    ComicCount = peerComics
                                });
                            }
                        }

                        var active = getActiveComics();
                        var respObj = new LanComicListResponse
                        {
                            DeviceName = DeviceName,
                            Platform = "Windows",
                            Comics = active.Select(c => new LanRemoteComicInfo
                            {
                                Id = c.Id,
                                Title = c.Title,
                                PageCount = (c.PagePaths != null && c.PagePaths.Count > 0) ? c.PagePaths.Count : c.TotalPages,
                                ReadCount = c.ReadCount,
                                LastReadTime = c.LastReadTime,
                                LastReadPage = c.LastReadPage > 0 ? c.LastReadPage : 1,
                                ImportTime = c.ImportTime
                            }).ToList()
                        };
                        string json = JsonSerializer.Serialize(respObj);
                        await WriteJsonResponseAsync(stream, 200, json, token);
                    }
                    else if (method == "GET" && path.Equals("/api/comic-cover", StringComparison.OrdinalIgnoreCase))
                    {
                        string? id = query["id"];
                        var comic = getActiveComics().FirstOrDefault(c => c.Id == id);
                        if (comic == null)
                        {
                            await WriteJsonResponseAsync(stream, 404, "{\"error\":\"Not found\"}", token);
                            return;
                        }

                        string? coverFile = null;
                        if (!string.IsNullOrEmpty(comic.CustomCoverPath) && File.Exists(comic.CustomCoverPath))
                        {
                            coverFile = comic.CustomCoverPath;
                        }
                        else if (!string.IsNullOrEmpty(comic.CoverPath) && File.Exists(comic.CoverPath))
                        {
                            coverFile = comic.CoverPath;
                        }
                        else if (comic.PagePaths != null && comic.PagePaths.Count > 0 && File.Exists(comic.PagePaths[0]))
                        {
                            coverFile = comic.PagePaths[0];
                        }

                        if (string.IsNullOrEmpty(coverFile) || !File.Exists(coverFile))
                        {
                            await WriteJsonResponseAsync(stream, 404, "{\"error\":\"Cover missing\"}", token);
                            return;
                        }

                        byte[] fileBytes;
                        await using (var fs = new FileStream(coverFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                        {
                            fileBytes = new byte[fs.Length];
                            int read = 0;
                            while (read < fileBytes.Length)
                            {
                                int r = await fs.ReadAsync(fileBytes.AsMemory(read, fileBytes.Length - read), token);
                                if (r <= 0) break;
                                read += r;
                            }
                        }

                        string header = $"HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\nContent-Length: {fileBytes.Length}\r\nConnection: close\r\n\r\n";
                        byte[] hBytes = Encoding.ASCII.GetBytes(header);
                        await stream.WriteAsync(hBytes, token);
                        await stream.WriteAsync(fileBytes, token);
                        await stream.FlushAsync(token);
                    }
                    else if (method == "GET" && path.Equals("/api/comic-manifest", StringComparison.OrdinalIgnoreCase))
                    {
                        string? id = query["id"];
                        var comic = getActiveComics().FirstOrDefault(c => c.Id == id);
                        if (comic == null)
                        {
                            await WriteJsonResponseAsync(stream, 404, "{\"error\":\"Not found\"}", token);
                            return;
                        }

                        var manifest = new LanComicManifest
                        {
                            Id = comic.Id,
                            Title = comic.Title,
                            PageCount = comic.PagePaths.Count,
                            Pages = comic.PagePaths.Select((p, idx) =>
                            {
                                long sz = 0;
                                try { if (File.Exists(p)) sz = new FileInfo(p).Length; } catch { }
                                return new LanPageEntry
                                {
                                    Index = idx,
                                    FileName = Path.GetFileName(p),
                                    Size = sz
                                };
                            }).ToList()
                        };
                        string json = JsonSerializer.Serialize(manifest);
                        await WriteJsonResponseAsync(stream, 200, json, token);
                    }
                    else if (method == "GET" && path.Equals("/api/comic-page", StringComparison.OrdinalIgnoreCase))
                    {
                        string? id = query["id"];
                        int.TryParse(query["index"], out int pageIdx);
                        var comic = getActiveComics().FirstOrDefault(c => c.Id == id);
                        if (comic == null || pageIdx < 0 || pageIdx >= comic.PagePaths.Count)
                        {
                            await WriteJsonResponseAsync(stream, 404, "{\"error\":\"Page not found\"}", token);
                            return;
                        }

                        string filePath = comic.PagePaths[pageIdx];
                        if (!File.Exists(filePath))
                        {
                            await WriteJsonResponseAsync(stream, 404, "{\"error\":\"File missing\"}", token);
                            return;
                        }

                        byte[] fileBytes;
                        await using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                        {
                            fileBytes = new byte[fs.Length];
                            int read = 0;
                            while (read < fileBytes.Length)
                            {
                                int r = await fs.ReadAsync(fileBytes.AsMemory(read, fileBytes.Length - read), token);
                                if (r <= 0) break;
                                read += r;
                            }
                        }

                        string header = $"HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\nContent-Length: {fileBytes.Length}\r\nConnection: close\r\n\r\n";
                        byte[] hBytes = Encoding.ASCII.GetBytes(header);
                        await stream.WriteAsync(hBytes, token);
                        await stream.WriteAsync(fileBytes, token);
                        await stream.FlushAsync(token);

                        int.TryParse(query["comicIndex"], out int cIdx);
                        int.TryParse(query["totalComics"], out int totalComics);
                        int.TryParse(query["pageTotal"], out int pageTotal);
                        string peerName = query["peerName"] ?? "对方设备";
                        if (pageTotal <= 0) pageTotal = comic.PagePaths.Count;
                        if (totalComics > 0 && pageTotal > 0)
                        {
                            int cZero = Math.Max(0, cIdx - 1);
                            int overallPct = (int)Math.Round(((cZero + (pageIdx + 1.0) / pageTotal) / totalComics) * 100);
                            onTransferProgress(
                                $"📤 正在向 {peerName} 传送 ({Math.Max(1, cIdx)}/{totalComics}) 《{comic.Title}》",
                                overallPct,
                                $"正在发送第 {pageIdx + 1}/{pageTotal} 页 ({Path.GetFileName(filePath)})..."
                            );
                        }
                    }
                    else if (method == "POST" && path.Equals("/api/upload-page", StringComparison.OrdinalIgnoreCase))
                    {
                        string comicTitle = query["comicTitle"] ?? "未命名漫画";
                        string fileName = query["fileName"] ?? "page.jpg";
                        int.TryParse(query["index"], out int idx);
                        int.TryParse(query["total"], out int total);
                        int.TryParse(query["comicIndex"], out int cIdx);
                        int.TryParse(query["totalComics"], out int totalComics);
                        string peerName = query["peerName"] ?? "";

                        // 如果本机书架已经拥有该漫画（在第 0 页上传时拦截）
                        if (idx == 0 && HasLocalActiveComic(comicTitle))
                        {
                            long remaining = contentLength;
                            byte[] discard = new byte[65536];
                            while (remaining > 0)
                            {
                                int r = await stream.ReadAsync(discard.AsMemory(0, (int)Math.Min(discard.Length, remaining)), token);
                                if (r <= 0) break;
                                remaining -= r;
                            }
                            await WriteJsonResponseAsync(stream, 200, "{\"ok\":true,\"alreadyExists\":true}", token);
                            return;
                        }

                        string baseDir = getTransferSaveDir();
                        string comicDir = Path.Combine(baseDir, SanitizeName(comicTitle));
                        Directory.CreateDirectory(comicDir);

                        string targetFile = Path.Combine(comicDir, SanitizeName(fileName));
                        await using (var fs = new FileStream(targetFile, FileMode.Create, FileAccess.Write, FileShare.Read))
                        {
                            long remaining = contentLength;
                            byte[] buf = new byte[65536];
                            while (remaining > 0)
                            {
                                int toRead = (int)Math.Min(buf.Length, remaining);
                                int r = await stream.ReadAsync(buf.AsMemory(0, toRead), token);
                                if (r <= 0) break;
                                await fs.WriteAsync(buf.AsMemory(0, r), token);
                                remaining -= r;
                            }
                        }

                        if (total > 0)
                        {
                            int pct;
                            string statusTitle;
                            string fromLabel = string.IsNullOrWhiteSpace(peerName) ? "" : $"自 {peerName} ";
                            if (totalComics > 0)
                            {
                                int cZero = Math.Max(0, cIdx - 1);
                                pct = (int)Math.Round(((cZero + (idx + 1.0) / total) / totalComics) * 100);
                                statusTitle = $"📥 正在接收{fromLabel}({Math.Max(1, cIdx)}/{totalComics}) 《{comicTitle}》";
                            }
                            else
                            {
                                pct = (int)Math.Round(((idx + 1.0) / total) * 100);
                                statusTitle = $"📥 正在接收{fromLabel}《{comicTitle}》";
                            }
                            onTransferProgress(statusTitle, pct, $"正在接收第 {idx + 1}/{total} 页 ({fileName})...");
                        }

                        await WriteJsonResponseAsync(stream, 200, "{\"ok\":true}", token);
                    }
                    else if (method == "POST" && path.Equals("/api/upload-complete", StringComparison.OrdinalIgnoreCase))
                    {
                        if (contentLength > 0)
                        {
                            long remaining = contentLength;
                            byte[] buf = new byte[4096];
                            while (remaining > 0)
                            {
                                int r = await stream.ReadAsync(buf.AsMemory(0, (int)Math.Min(buf.Length, remaining)), token);
                                if (r <= 0) break;
                                remaining -= r;
                            }
                        }

                        string comicTitle = query["comicTitle"] ?? "未命名漫画";
                        if (HasLocalActiveComic(comicTitle))
                        {
                            await WriteJsonResponseAsync(stream, 200, "{\"ok\":true,\"alreadyExists\":true}", token);
                            return;
                        }

                        string comicDir = Path.Combine(getTransferSaveDir(), SanitizeName(comicTitle));
                        if (Directory.Exists(comicDir))
                        {
                            onComicReceived(comicTitle, comicDir);
                        }

                        await WriteJsonResponseAsync(stream, 200, "{\"ok\":true}", token);
                    }
                    else if (method == "POST" && path.Equals("/api/batch-complete", StringComparison.OrdinalIgnoreCase))
                    {
                        if (contentLength > 0)
                        {
                            long remaining = contentLength;
                            byte[] buf = new byte[4096];
                            while (remaining > 0)
                            {
                                int r = await stream.ReadAsync(buf.AsMemory(0, (int)Math.Min(buf.Length, remaining)), token);
                                if (r <= 0) break;
                                remaining -= r;
                            }
                        }

                        string mode = query["mode"] ?? "push";
                        string peerName = query["peerName"] ?? "对方设备";
                        int.TryParse(query["transferredCount"], out int transferredCount);
                        int.TryParse(query["skippedCount"], out int skippedCount);
                        onBatchTransferComplete?.Invoke(mode, peerName, transferredCount, skippedCount);

                        await WriteJsonResponseAsync(stream, 200, "{\"ok\":true}", token);
                    }
                    else
                    {
                        await WriteJsonResponseAsync(stream, 404, "{\"error\":\"Unknown endpoint\"}", token);
                    }
                }
                catch { }
            }
        }

        private static async Task WriteJsonResponseAsync(NetworkStream stream, int statusCode, string json, CancellationToken token)
        {
            byte[] body = Encoding.UTF8.GetBytes(json);
            string statusText = statusCode == 200 ? "OK" : "Not Found";
            string header = $"HTTP/1.1 {statusCode} {statusText}\r\nContent-Type: application/json; charset=utf-8\r\nAccess-Control-Allow-Origin: *\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n";
            byte[] hBytes = Encoding.ASCII.GetBytes(header);
            await stream.WriteAsync(hBytes, token);
            await stream.WriteAsync(body, token);
            await stream.FlushAsync(token);
        }

        public static string SanitizeName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "未命名漫画";
            char[] invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(name.Length);
            foreach (char c in name.Trim())
            {
                sb.Append(invalid.Contains(c) ? '_' : c);
            }
            string res = sb.ToString().Trim('.');
            return string.IsNullOrWhiteSpace(res) ? "未命名漫画" : res;
        }

        public void Dispose()
        {
            try { cts?.Cancel(); } catch { }
            try { tcpListener?.Stop(); } catch { }
            try { udpListener?.Close(); } catch { }
            try { httpClient.Dispose(); } catch { }
            try { probeHttpClient.Dispose(); } catch { }
        }
    }
}

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
    public class AndroidUpdateInfo
    {
        [System.Text.Json.Serialization.JsonPropertyName("versionCode")]
        public int VersionCode { get; set; } = 5;

        [System.Text.Json.Serialization.JsonPropertyName("versionName")]
        public string VersionName { get; set; } = "1.0.4";

        [System.Text.Json.Serialization.JsonPropertyName("fileSize")]
        public long FileSize { get; set; } = 0;

        [System.Text.Json.Serialization.JsonPropertyName("changelog")]
        public string Changelog { get; set; } = "1. 完美适配浅色/暗色主题，优化互传弹窗与设置页界面空间排版\n2. 优化主动断开连接机制与双向退回离线本地书架\n3. 优化 Windows 端局域网互传即时呈现已连设备\n4. 支持电脑端一键强制/增量推送安卓安装包更新并重新安装";

        [System.Text.Json.Serialization.JsonPropertyName("apkPath")]
        public string ApkPath { get; set; } = "";
    }

    public class PushUpdateResult
    {
        [System.Text.Json.Serialization.JsonPropertyName("ok")]
        public bool Ok { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("status")]
        public string Status { get; set; } = ""; // "pushed", "already_latest", "error"

        [System.Text.Json.Serialization.JsonPropertyName("message")]
        public string Message { get; set; } = "";

        [System.Text.Json.Serialization.JsonPropertyName("remoteVersionCode")]
        public int RemoteVersionCode { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("targetVersionCode")]
        public int TargetVersionCode { get; set; }
    }

    public class LanTransferService : IDisposable
    {
        public const int DISCOVERY_UDP_PORT = 39527;
        public const int DEFAULT_HTTP_PORT = 39528;

        public Action<string, string>? OnUpdatePushed { get; set; }
        public Action<string, string, string>? OnPeerDisconnected { get; set; }
        private string? customApkPath = null;
        public void SetCustomApkPath(string? path) => customApkPath = path;

        private readonly Func<List<ComicRecord>> getActiveComics;
        private readonly Func<string> getTransferSaveDir;
        private readonly Action<string, string> onComicReceived;
        private readonly Action<string, int, string> onTransferProgress;
        private readonly Action<LanPeerInfo>? onPeerConnected;
        private readonly Action<string, string, int, int>? onBatchTransferComplete;
        private readonly Func<string, string?, int, bool, int?>? onRemoteComicRead;
        private readonly Func<string, string?, bool>? onRemoteComicDelete;
        private readonly Func<string, string?, string, bool>? onRemoteComicRename;
        private readonly Func<string, int, (bool ok, string coverUrl)>? onRemoteComicSetCoverPage;
        private readonly Func<string, bool>? onRemoteComicResetCover;
        private readonly Func<string, byte[], string, bool>? onRemoteComicSetCoverFile;
        private readonly Func<string>? getDeviceId;
        private readonly Func<bool>? getRequirePassword;
        private readonly Func<string>? getConnectionPassword;
        private readonly Func<string, bool>? isDeviceWhitelisted;
        private readonly Func<string, string, bool>? isDeviceBlacklisted;
        private readonly Action<LanPairRequestState>? onPairRequestReceived;

        private readonly ConcurrentDictionary<string, LanPairRequestState> pairRequests = new();

        private volatile string? currentSessionToken = null;
        private volatile string? currentConnectedPeerId = null;

        public string? CurrentSessionToken => currentSessionToken;

        public bool IsRequestAuthorized(string? fromId, string? remoteIp, string? token)
        {
            if (!string.IsNullOrEmpty(fromId) && isDeviceWhitelisted != null && isDeviceWhitelisted(fromId))
            {
                return true;
            }
            if (!string.IsNullOrEmpty(currentSessionToken) && !string.IsNullOrEmpty(token) && string.Equals(currentSessionToken, token, StringComparison.Ordinal))
            {
                return true;
            }
            if (!string.IsNullOrEmpty(token))
            {
                foreach (var pr in pairRequests.Values)
                {
                    if (!pr.IsExpired && string.Equals(pr.SessionToken, token, StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

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
            Func<string, string?, int, bool, int?>? onRemoteComicRead = null,
            Func<string, string?, bool>? onRemoteComicDelete = null,
            Func<string, string?, string, bool>? onRemoteComicRename = null,
            Func<string, int, (bool ok, string coverUrl)>? onRemoteComicSetCoverPage = null,
            Func<string, bool>? onRemoteComicResetCover = null,
            Func<string, byte[], string, bool>? onRemoteComicSetCoverFile = null,
            Func<string>? getDeviceId = null,
            Func<bool>? getRequirePassword = null,
            Func<string>? getConnectionPassword = null,
            Func<string, bool>? isDeviceWhitelisted = null,
            Func<string, string, bool>? isDeviceBlacklisted = null,
            Action<LanPairRequestState>? onPairRequestReceived = null)
        {
            this.getActiveComics = getActiveComics;
            this.getTransferSaveDir = getTransferSaveDir;
            this.onComicReceived = onComicReceived;
            this.onTransferProgress = onTransferProgress;
            this.onPeerConnected = onPeerConnected;
            this.onBatchTransferComplete = onBatchTransferComplete;
            this.onRemoteComicRead = onRemoteComicRead;
            this.onRemoteComicDelete = onRemoteComicDelete;
            this.onRemoteComicRename = onRemoteComicRename;
            this.onRemoteComicSetCoverPage = onRemoteComicSetCoverPage;
            this.onRemoteComicResetCover = onRemoteComicResetCover;
            this.onRemoteComicSetCoverFile = onRemoteComicSetCoverFile;
            this.getDeviceId = getDeviceId;
            this.getRequirePassword = getRequirePassword;
            this.getConnectionPassword = getConnectionPassword;
            this.isDeviceWhitelisted = isDeviceWhitelisted;
            this.isDeviceBlacklisted = isDeviceBlacklisted;
            this.onPairRequestReceived = onPairRequestReceived;

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
                DeviceId = getDeviceId?.Invoke() ?? "",
                DeviceName = DeviceName,
                Platform = "Windows",
                Ip = ip,
                Port = ListeningPort,
                ComicCount = count,
                RequirePassword = getRequirePassword?.Invoke() ?? false
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

        public bool IsSelfIp(string? ip)
        {
            if (string.IsNullOrWhiteSpace(ip)) return false;
            string clean = ip.Trim();
            if (clean.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) clean = clean.Substring(7);
            if (clean.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) clean = clean.Substring(8);
            if (clean.Contains(':')) clean = clean.Split(':')[0];
            if (clean.Contains('/')) clean = clean.Replace("/", "");
            if (clean == "127.0.0.1" || string.Equals(clean, "localhost", StringComparison.OrdinalIgnoreCase) || clean == "0.0.0.0" || clean == "::1") return true;
            var localIps = new HashSet<string>(GetLocalLanIps(), StringComparer.OrdinalIgnoreCase);
            return localIps.Contains(clean);
        }

        public bool IsSelf(LanPeerInfo? peer)
        {
            if (peer == null) return false;
            string myId = getDeviceId?.Invoke() ?? "";
            if (!string.IsNullOrEmpty(myId) && string.Equals(myId, peer.DeviceId, StringComparison.OrdinalIgnoreCase))
                return true;

            if (IsSelfIp(peer.Ip))
                return true;

            return false;
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
                            if (peer != null && peer.App == "YYComicReader")
                            {
                                string remoteIp = res.RemoteEndPoint.Address.ToString();
                                peer.Ip = remoteIp;
                                if (!IsSelf(peer) && !IsSelfIp(remoteIp))
                                {
                                    string peerKey = !string.IsNullOrEmpty(peer.DeviceId) ? peer.DeviceId : $"{peer.Ip}:{peer.Port}";
                                    foundPeers[peerKey] = peer;
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
                    if (peer != null && peer.App == "YYComicReader" && !IsSelf(peer) && !IsSelfIp(ip))
                    {
                        string peerKey = !string.IsNullOrEmpty(peer.DeviceId) ? peer.DeviceId : $"{peer.Ip}:{peer.Port}";
                        foundPeers[peerKey] = peer;
                    }
                });
            });

            await Task.WhenAll(udpTask, subnetTask);
            return foundPeers.Values.Where(p => !IsSelf(p) && !IsSelfIp(p.Ip)).OrderBy(p => p.DeviceName).ToList();
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

        public void NotifyPeerConnected(LanPeerInfo peer)
        {
            if (peer == null || IsSelf(peer) || IsSelfIp(peer.Ip)) return;
            onPeerConnected?.Invoke(peer);

            // 如果对方是 Android 设备，主动在后台推送更新信息
            if (!string.IsNullOrEmpty(peer.Platform) && peer.Platform.ToLowerInvariant().Contains("android"))
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(800);
                    await PushUpdateToAndroidAsync(peer.Ip, peer.Port, peer.DeviceName);
                });
            }
        }

        public AndroidUpdateInfo GetAndroidUpdateInfo()
        {
            string? apkPath = null;
            if (!string.IsNullOrEmpty(customApkPath) && File.Exists(customApkPath))
            {
                apkPath = customApkPath;
            }
            else
            {
                var candidates = new[]
                {
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "updates", "YY漫画阅读器.apk"),
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ComicReaderDesktop", "updates", "YY漫画阅读器.apk"),
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ComicReaderApp", "YY漫画阅读器.apk"),
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "YY漫画阅读器.apk"),
                    Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "updates", "YY漫画阅读器.apk")),
                    Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "ComicReaderDesktop", "updates", "YY漫画阅读器.apk")),
                    Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "ComicReaderApp", "YY漫画阅读器.apk")),
                    Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "YY漫画阅读器.apk")),
                    @"E:\y\updates\YY漫画阅读器.apk",
                    @"E:\y\ComicReaderDesktop\updates\YY漫画阅读器.apk",
                    @"E:\y\ComicReaderApp\YY漫画阅读器.apk"
                };
                apkPath = candidates.FirstOrDefault(File.Exists);
            }

            var info = new AndroidUpdateInfo();
            if (!string.IsNullOrEmpty(apkPath) && File.Exists(apkPath))
            {
                info.ApkPath = apkPath;
                try
                {
                    var fi = new FileInfo(apkPath);
                    info.FileSize = fi.Length;
                }
                catch { }

                string dir = Path.GetDirectoryName(apkPath) ?? "";
                var jsonCandidates = new[]
                {
                    Path.Combine(dir, "update_info.json"),
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "updates", "update_info.json"),
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ComicReaderDesktop", "updates", "update_info.json"),
                    Path.Combine(Directory.GetCurrentDirectory(), "updates", "update_info.json"),
                    Path.Combine(Directory.GetCurrentDirectory(), "ComicReaderDesktop", "updates", "update_info.json"),
                    @"E:\y\updates\update_info.json",
                    @"E:\y\ComicReaderDesktop\updates\update_info.json",
                    @"E:\y\ComicReaderApp\update_info.json"
                };
                string? targetJson = jsonCandidates.FirstOrDefault(File.Exists);

                if (targetJson != null)
                {
                    try
                    {
                        var text = File.ReadAllText(targetJson);
                        using var doc = JsonDocument.Parse(text);
                        var root = doc.RootElement;
                        if (root.TryGetProperty("versionCode", out var vcEl) && vcEl.TryGetInt32(out int vc)) info.VersionCode = vc;
                        if (root.TryGetProperty("versionName", out var vnEl)) info.VersionName = vnEl.GetString() ?? info.VersionName;
                        if (root.TryGetProperty("changelog", out var clEl)) info.Changelog = clEl.GetString() ?? info.Changelog;
                    }
                    catch { }
                }

                if (info.VersionCode <= 0)
                {
                    info.VersionCode = 10;
                    info.VersionName = "1.0.9";
                    info.Changelog = "优化局域网互传与安装更新体验。";
                }
            }
            return info;
        }

        public async Task<PushUpdateResult> PushUpdateToAndroidAsync(string ip, int port, string peerName, bool force = false)
        {
            if (string.IsNullOrEmpty(ip) || IsSelfIp(ip))
                return new PushUpdateResult { Ok = false, Status = "error", Message = "无法向本机自身或空 IP 推送更新" };

            try
            {
                var info = GetAndroidUpdateInfo();
                if (string.IsNullOrEmpty(info.ApkPath) || !File.Exists(info.ApkPath))
                    return new PushUpdateResult { Ok = false, Status = "error", Message = "未找到可用的安卓 APK 安装包" };

                // 核心：推送前先检测安卓端当前版本
                int remoteVersionCode = 0;
                try
                {
                    using var checkClient = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
                    var verResp = await checkClient.GetStringAsync($"http://{ip}:{port}/api/client-version");
                    using var doc = JsonDocument.Parse(verResp);
                    if (doc.RootElement.TryGetProperty("versionCode", out var vcEl) && vcEl.TryGetInt32(out int vc))
                    {
                        remoteVersionCode = vc;
                    }
                }
                catch
                {
                    try
                    {
                        var remotePeer = await PingPeerAsync(ip, port, true);
                        if (remotePeer != null && remotePeer.VersionCode > 0)
                        {
                            remoteVersionCode = remotePeer.VersionCode;
                        }
                    }
                    catch { }
                }

                if (!force && remoteVersionCode > 0 && remoteVersionCode >= info.VersionCode)
                {
                    // 对方已经是最新版本或更高版本，且非手动强制，静默跳过不打扰
                    return new PushUpdateResult
                    {
                        Ok = false,
                        Status = "already_latest",
                        Message = $"对方设备已是最新版本 (v{info.VersionName} · build {remoteVersionCode})，无需重复推送",
                        RemoteVersionCode = remoteVersionCode,
                        TargetVersionCode = info.VersionCode
                    };
                }

                string bestIp = MatchBestLocalIpForRemote(IPAddress.TryParse(ip, out var parsedIp) ? parsedIp : IPAddress.Loopback);
                string downloadUrl = $"http://{bestIp}:{ListeningPort}/api/download-apk";

                int targetPushVersionCode = info.VersionCode;
                if (force && remoteVersionCode > 0 && remoteVersionCode >= targetPushVersionCode)
                {
                    targetPushVersionCode = remoteVersionCode + 1;
                }
                else if (targetPushVersionCode <= 0)
                {
                    targetPushVersionCode = 10;
                }

                var payload = new
                {
                    versionCode = targetPushVersionCode,
                    versionName = info.VersionName,
                    fileSize = info.FileSize,
                    changelog = info.Changelog,
                    downloadUrl,
                    force
                };

                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                var jsonContent = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
                var resp = await client.PostAsync($"http://{ip}:{port}/api/push-update", jsonContent);
                if (resp.IsSuccessStatusCode)
                {
                    OnUpdatePushed?.Invoke(peerName, info.VersionName);
                    return new PushUpdateResult
                    {
                        Ok = true,
                        Status = "pushed",
                        Message = $"已成功向【{peerName}】推送 v{info.VersionName} 升级通知",
                        RemoteVersionCode = remoteVersionCode,
                        TargetVersionCode = info.VersionCode
                    };
                }
                else
                {
                    return new PushUpdateResult { Ok = false, Status = "error", Message = $"对方设备拒绝了推送请求 (HTTP {(int)resp.StatusCode})" };
                }
            }
            catch (Exception ex)
            {
                return new PushUpdateResult { Ok = false, Status = "error", Message = $"推送失败: {ex.Message}" };
            }
        }

        public bool RespondPairRequest(string reqId, bool accept)
        {
            if (string.IsNullOrEmpty(reqId)) return false;
            if (pairRequests.TryGetValue(reqId, out var state))
            {
                if (state.IsExpired || state.Status == "timeout" || state.Status == "expired")
                {
                    state.Status = "timeout";
                    return false;
                }
                state.Status = accept ? "accepted" : "rejected";
                if (accept)
                {
                    currentSessionToken = Guid.NewGuid().ToString("N");
                    currentConnectedPeerId = state.FromId;
                    state.SessionToken = currentSessionToken;
                    NotifyPeerConnected(new LanPeerInfo
                    {
                        App = "YYComicReader",
                        DeviceId = state.FromId,
                        DeviceName = state.FromName,
                        Platform = state.FromPlatform,
                        Ip = state.FromIp,
                        Port = state.FromPort,
                        SessionToken = currentSessionToken
                    });
                }
                return true;
            }
            return false;
        }

        public async Task<LanConnectResult> ConnectToRemotePeerAsync(LanPeerInfo peer, string? password = null, CancellationToken token = default)
        {
            if (peer == null || string.IsNullOrEmpty(peer.Ip) || IsSelf(peer) || IsSelfIp(peer.Ip))
            {
                return new LanConnectResult { Ok = false, Status = "error", Message = (peer != null && (IsSelf(peer) || IsSelfIp(peer.Ip))) ? "无法连接自身设备" : "目标设备无效" };
            }

            int port = peer.Port > 0 ? peer.Port : DEFAULT_HTTP_PORT;
            string myId = getDeviceId?.Invoke() ?? "";
            string authUrl = $"http://{peer.Ip}:{port}/api/connect-auth?fromId={Uri.EscapeDataString(myId)}&fromName={Uri.EscapeDataString(DeviceName)}&fromPlatform=Windows&fromPort={ListeningPort}";
            if (!string.IsNullOrEmpty(password))
            {
                authUrl += $"&password={Uri.EscapeDataString(password)}";
            }

            try
            {
                using var authResp = await httpClient.GetAsync(authUrl, token);
                if (!authResp.IsSuccessStatusCode)
                {
                    return new LanConnectResult { Ok = false, Status = "error", Message = $"HTTP {(int)authResp.StatusCode}" };
                }
                string authJson = await authResp.Content.ReadAsStringAsync(token);
                using var doc = JsonDocument.Parse(authJson);
                var root = doc.RootElement;
                string status = root.TryGetProperty("status", out var sEl) ? (sEl.GetString() ?? "") : "";

                if (status == "accepted")
                {
                    string st = root.TryGetProperty("sessionToken", out var stEl) ? (stEl.GetString() ?? "") : "";
                    peer.SessionToken = st;
                    currentSessionToken = st;
                    currentConnectedPeerId = peer.DeviceId;
                    NotifyPeerConnected(peer);
                    return new LanConnectResult { Ok = true, Status = "accepted", Message = "连接成功", SessionToken = st, Peer = peer };
                }
                else if (status == "need_password")
                {
                    return new LanConnectResult { Ok = false, Status = "need_password", Message = "对方设备已开启密码连接保护，请输入密码", Peer = peer };
                }
                else if (status == "wrong_password")
                {
                    return new LanConnectResult { Ok = false, Status = "wrong_password", Message = "连接密码错误，请重新输入", Peer = peer };
                }
                else if (status == "need_confirm")
                {
                    // 发起配对申请
                    string reqId = Guid.NewGuid().ToString("N");
                    string reqUrl = $"http://{peer.Ip}:{port}/api/connect-request?fromId={Uri.EscapeDataString(myId)}&fromName={Uri.EscapeDataString(DeviceName)}&fromPlatform=Windows&fromPort={ListeningPort}&reqId={reqId}";
                    using var reqResp = await httpClient.GetAsync(reqUrl, token);
                    if (!reqResp.IsSuccessStatusCode)
                    {
                        return new LanConnectResult { Ok = false, Status = "error", Message = "发送配对申请失败" };
                    }

                    // 轮询对方确认状态 (最多 35 秒)
                    int maxAttempts = 45;
                    for (int i = 0; i < maxAttempts; i++)
                    {
                        if (token.IsCancellationRequested) break;
                        await Task.Delay(800, token);

                        string statusUrl = $"http://{peer.Ip}:{port}/api/connect-status?reqId={reqId}";
                        using var pollResp = await httpClient.GetAsync(statusUrl, token);
                        if (pollResp.IsSuccessStatusCode)
                        {
                            string pollJson = await pollResp.Content.ReadAsStringAsync(token);
                            using var pollDoc = JsonDocument.Parse(pollJson);
                            string pollStatus = pollDoc.RootElement.TryGetProperty("status", out var psEl) ? (psEl.GetString() ?? "") : "";
                            if (pollStatus == "accepted")
                            {
                                string st = pollDoc.RootElement.TryGetProperty("sessionToken", out var pstEl) ? (pstEl.GetString() ?? "") : "";
                                peer.SessionToken = st;
                                currentSessionToken = st;
                                currentConnectedPeerId = peer.DeviceId;
                                NotifyPeerConnected(peer);
                                return new LanConnectResult { Ok = true, Status = "accepted", Message = "对方已允许连接", SessionToken = st, Peer = peer };
                            }
                            else if (pollStatus == "rejected")
                            {
                                return new LanConnectResult { Ok = false, Status = "rejected", Message = "对方拒绝了连接请求", Peer = peer };
                            }
                            else if (pollStatus == "blacklisted")
                            {
                                return new LanConnectResult { Ok = false, Status = "blacklisted", Message = "对方已将本机加入黑名单，禁止连接", Peer = peer };
                            }
                        }
                    }
                    return new LanConnectResult { Ok = false, Status = "timeout", Message = "等待对方确认超时", Peer = peer };
                }
                else
                {
                    return new LanConnectResult { Ok = false, Status = status, Message = root.TryGetProperty("message", out var mEl) ? (mEl.GetString() ?? "连接被拒绝") : "连接失败", Peer = peer };
                }
            }
            catch (Exception ex)
            {
                return new LanConnectResult { Ok = false, Status = "error", Message = $"连接失败: {ex.Message}" };
            }
        }

        public async Task<LanComicListResponse?> FetchRemoteComicsAsync(string ip, int port, bool notifyConnect = true, string? sessionToken = null)
        {
            if (IsSelfIp(ip)) return null;
            string url = $"http://{ip}:{port}/api/comics";
            var queryParams = new List<string>();
            string myId = getDeviceId?.Invoke() ?? "";
            if (!string.IsNullOrEmpty(myId))
            {
                queryParams.Add($"fromId={Uri.EscapeDataString(myId)}");
            }
            if (notifyConnect)
            {
                int myCount = 0;
                try { myCount = getActiveComics().Count; } catch { }
                queryParams.Add($"peerName={Uri.EscapeDataString(DeviceName)}");
                queryParams.Add("peerPlatform=Windows");
                queryParams.Add($"peerPort={ListeningPort}");
                queryParams.Add($"peerComics={myCount}");
            }
            string effectiveToken = !string.IsNullOrEmpty(sessionToken) ? sessionToken : (currentSessionToken ?? "");
            if (!string.IsNullOrEmpty(effectiveToken))
            {
                queryParams.Add($"sessionToken={Uri.EscapeDataString(effectiveToken)}");
            }
            if (queryParams.Count > 0)
            {
                url += "?" + string.Join("&", queryParams);
            }
            using var resp = await httpClient.GetAsync(url);
            if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                throw new InvalidOperationException("session_expired");
            }
            resp.EnsureSuccessStatusCode();
            string json = await resp.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<LanComicListResponse>(json);
        }

        public async Task<LanComicManifest?> FetchRemoteComicManifestAsync(string ip, int port, string id, string? sessionToken = null)
        {
            string effectiveToken = !string.IsNullOrEmpty(sessionToken) ? sessionToken : (currentSessionToken ?? "");
            string tokenParam = !string.IsNullOrEmpty(effectiveToken) ? $"&sessionToken={Uri.EscapeDataString(effectiveToken)}" : "";
            string myId = getDeviceId?.Invoke() ?? "";
            string fromIdParam = !string.IsNullOrEmpty(myId) ? $"&fromId={Uri.EscapeDataString(myId)}" : "";
            string url = $"http://{ip}:{port}/api/comic-manifest?id={Uri.EscapeDataString(id)}{fromIdParam}{tokenParam}";
            using var resp = await httpClient.GetAsync(url);
            if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                throw new InvalidOperationException("session_expired");
            }
            if (!resp.IsSuccessStatusCode) return null;
            string json = await resp.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<LanComicManifest>(json);
        }

        public async Task<bool> RemoteRenameComicAsync(string ip, int port, string id, string title, string newTitle)
        {
            string url = $"http://{ip}:{port}/api/comic-rename?id={Uri.EscapeDataString(id)}&title={Uri.EscapeDataString(title)}&newTitle={Uri.EscapeDataString(newTitle)}";
            using var resp = await httpClient.GetAsync(url);
            return resp.IsSuccessStatusCode;
        }

        public async Task<(bool ok, string coverUrl)> RemoteSetComicCoverPageAsync(string ip, int port, string id, int pageIndex)
        {
            string url = $"http://{ip}:{port}/api/comic-set-cover-page?id={Uri.EscapeDataString(id)}&pageIndex={pageIndex}";
            using var resp = await httpClient.GetAsync(url);
            if (!resp.IsSuccessStatusCode) return (false, "");
            string json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            string coverUrl = doc.RootElement.TryGetProperty("coverUrl", out var cEl) ? (cEl.GetString() ?? "") : "";
            return (true, coverUrl);
        }

        public async Task<bool> RemoteResetComicCoverAsync(string ip, int port, string id)
        {
            string url = $"http://{ip}:{port}/api/comic-reset-cover?id={Uri.EscapeDataString(id)}";
            using var resp = await httpClient.GetAsync(url);
            return resp.IsSuccessStatusCode;
        }

        public async Task<bool> RemoteDeleteComicAsync(string ip, int port, string id, string title)
        {
            string url = $"http://{ip}:{port}/api/comic-delete?id={Uri.EscapeDataString(id)}&title={Uri.EscapeDataString(title)}";
            using var resp = await httpClient.GetAsync(url);
            return resp.IsSuccessStatusCode;
        }

        public async Task<bool> RecordRemoteComicReadAsync(string ip, int port, string id, string title, int page, bool inc)
        {
            string url = $"http://{ip}:{port}/api/record-read?id={Uri.EscapeDataString(id)}&title={Uri.EscapeDataString(title)}&page={page}&inc={(inc ? 1 : 0)}";
            try
            {
                using var resp = await httpClient.GetAsync(url);
                return resp.IsSuccessStatusCode;
            }
            catch { return false; }
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
                    else if (method == "GET" && (path.Equals("/api/check-update", StringComparison.OrdinalIgnoreCase) || path.TrimStart('/').Equals("api/check-update", StringComparison.OrdinalIgnoreCase)))
                    {
                        var info = GetAndroidUpdateInfo();
                        bool hasApk = !string.IsNullOrEmpty(info.ApkPath) && File.Exists(info.ApkPath);
                        var remoteEp = client.Client.RemoteEndPoint as IPEndPoint;
                        string bestIp = remoteEp != null ? MatchBestLocalIpForRemote(remoteEp.Address) : (GetLocalLanIps().FirstOrDefault() ?? "127.0.0.1");
                        string downloadUrl = $"http://{bestIp}:{ListeningPort}/api/download-apk";

                        var resp = new
                        {
                            hasUpdate = hasApk,
                            versionCode = info.VersionCode,
                            versionName = info.VersionName,
                            fileSize = info.FileSize,
                            changelog = info.Changelog,
                            downloadUrl
                        };
                        await WriteJsonResponseAsync(stream, 200, JsonSerializer.Serialize(resp), token);
                    }
                    else if (method == "GET" && (path.Equals("/api/download-apk", StringComparison.OrdinalIgnoreCase) || path.TrimStart('/').Equals("api/download-apk", StringComparison.OrdinalIgnoreCase)))
                    {
                        var info = GetAndroidUpdateInfo();
                        if (string.IsNullOrEmpty(info.ApkPath) || !File.Exists(info.ApkPath))
                        {
                            await WriteJsonResponseAsync(stream, 404, "{\"error\":\"APK file not found on Windows server\"}", token);
                            return;
                        }

                        var fi = new FileInfo(info.ApkPath);
                        long totalLen = fi.Length;
                        string header = $"HTTP/1.1 200 OK\r\nContent-Type: application/vnd.android.package-archive\r\nContent-Length: {totalLen}\r\nContent-Disposition: attachment; filename=\"YYComicReader_v{info.VersionName}.apk\"\r\nConnection: close\r\n\r\n";
                        byte[] hBytes = Encoding.ASCII.GetBytes(header);
                        await stream.WriteAsync(hBytes, token);

                        await using (var fs = new FileStream(info.ApkPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        {
                            byte[] buf = new byte[65536];
                            int r;
                            while ((r = await fs.ReadAsync(buf.AsMemory(0, buf.Length), token)) > 0)
                            {
                                await stream.WriteAsync(buf.AsMemory(0, r), token);
                            }
                        }
                        await stream.FlushAsync(token);
                    }
                    else if (path.Equals("/api/connect-auth", StringComparison.OrdinalIgnoreCase) || path.TrimStart('/').Equals("api/connect-auth", StringComparison.OrdinalIgnoreCase))
                    {
                        string fromId = query["fromId"] ?? "";
                        string fromName = query["fromName"] ?? "未知设备";
                        string fromPlatform = query["fromPlatform"] ?? "";
                        int.TryParse(query["fromPort"], out int fromPort);
                        if (fromPort <= 0) fromPort = DEFAULT_HTTP_PORT;
                        string password = query["password"] ?? "";

                        var remoteEp = client.Client.RemoteEndPoint as IPEndPoint;
                        string fromIp = remoteEp?.Address.ToString() ?? "";
                        string myId = getDeviceId?.Invoke() ?? "";

                        if ((!string.IsNullOrEmpty(myId) && string.Equals(fromId, myId, StringComparison.OrdinalIgnoreCase)) || IsSelfIp(fromIp))
                        {
                            await WriteJsonResponseAsync(stream, 200, "{\"ok\":false,\"status\":\"rejected\",\"message\":\"无法连接本机自身设备\"}", token);
                            return;
                        }

                        if (isDeviceBlacklisted != null && isDeviceBlacklisted(fromId, fromIp))
                        {
                            await WriteJsonResponseAsync(stream, 200, "{\"ok\":false,\"status\":\"blacklisted\",\"message\":\"对方已将本机加入黑名单，禁止连接\"}", token);
                            return;
                        }

                        if (isDeviceWhitelisted != null && !string.IsNullOrEmpty(fromId) && isDeviceWhitelisted(fromId))
                        {
                            currentSessionToken = Guid.NewGuid().ToString("N");
                            currentConnectedPeerId = fromId;
                            NotifyPeerConnected(new LanPeerInfo
                            {
                                App = "YYComicReader",
                                DeviceId = fromId,
                                DeviceName = fromName,
                                Platform = fromPlatform,
                                Ip = fromIp,
                                Port = fromPort,
                                SessionToken = currentSessionToken
                            });
                            string whiteJson = $"{{\"ok\":true,\"status\":\"accepted\",\"message\":\"白名单免确认连接\",\"sessionToken\":{JsonSerializer.Serialize(currentSessionToken)}}}";
                            await WriteJsonResponseAsync(stream, 200, whiteJson, token);
                            return;
                        }

                        string incomingToken = query["sessionToken"] ?? "";
                        bool isWhitelisted = isDeviceWhitelisted != null && !string.IsNullOrEmpty(fromId) && isDeviceWhitelisted(fromId);
                        bool reqPass = getRequirePassword != null && getRequirePassword();
                        string expectedPass = getConnectionPassword != null ? getConnectionPassword() : "";

                        if (isWhitelisted)
                        {
                            currentSessionToken = !string.IsNullOrEmpty(incomingToken) ? incomingToken : Guid.NewGuid().ToString("N");
                            currentConnectedPeerId = fromId;
                            NotifyPeerConnected(new LanPeerInfo
                            {
                                App = "YYComicReader",
                                DeviceId = fromId,
                                DeviceName = fromName,
                                Platform = fromPlatform,
                                Ip = fromIp,
                                Port = fromPort,
                                SessionToken = currentSessionToken
                            });
                            string whiteJson = $"{{\"ok\":true,\"status\":\"accepted\",\"message\":\"白名单免确认连接\",\"sessionToken\":{JsonSerializer.Serialize(currentSessionToken)}}}";
                            await WriteJsonResponseAsync(stream, 200, whiteJson, token);
                        }
                        else if (reqPass)
                        {
                            if (string.IsNullOrEmpty(password))
                            {
                                await WriteJsonResponseAsync(stream, 200, "{\"ok\":false,\"status\":\"need_password\",\"message\":\"需要输入连接密码\"}", token);
                            }
                            else if (string.Equals(password, expectedPass, StringComparison.Ordinal))
                            {
                                currentSessionToken = !string.IsNullOrEmpty(incomingToken) ? incomingToken : Guid.NewGuid().ToString("N");
                                currentConnectedPeerId = fromId;
                                NotifyPeerConnected(new LanPeerInfo
                                {
                                    App = "YYComicReader",
                                    DeviceId = fromId,
                                    DeviceName = fromName,
                                    Platform = fromPlatform,
                                    Ip = fromIp,
                                    Port = fromPort,
                                    SessionToken = currentSessionToken
                                });
                                string json = $"{{\"ok\":true,\"status\":\"accepted\",\"message\":\"密码验证通过\",\"sessionToken\":{JsonSerializer.Serialize(currentSessionToken)}}}";
                                await WriteJsonResponseAsync(stream, 200, json, token);
                            }
                            else
                            {
                                await WriteJsonResponseAsync(stream, 200, "{\"ok\":false,\"status\":\"wrong_password\",\"message\":\"密码错误\"}", token);
                            }
                        }
                        else
                        {
                            await WriteJsonResponseAsync(stream, 200, "{\"ok\":false,\"status\":\"need_confirm\",\"message\":\"需要被连接方确认\"}", token);
                        }
                    }
                    else if (path.Equals("/api/connect-request", StringComparison.OrdinalIgnoreCase) || path.TrimStart('/').Equals("api/connect-request", StringComparison.OrdinalIgnoreCase))
                    {
                        string reqId = query["reqId"] ?? Guid.NewGuid().ToString("N");
                        string fromId = query["fromId"] ?? "";
                        string fromName = query["fromName"] ?? "未知设备";
                        string fromPlatform = query["fromPlatform"] ?? "";
                        int.TryParse(query["fromPort"], out int fromPort);
                        if (fromPort <= 0) fromPort = DEFAULT_HTTP_PORT;

                        var remoteEp = client.Client.RemoteEndPoint as IPEndPoint;
                        string fromIp = remoteEp?.Address.ToString() ?? "";
                        string myId = getDeviceId?.Invoke() ?? "";

                        if ((!string.IsNullOrEmpty(myId) && string.Equals(fromId, myId, StringComparison.OrdinalIgnoreCase)) || IsSelfIp(fromIp))
                        {
                            await WriteJsonResponseAsync(stream, 200, "{\"ok\":false,\"status\":\"rejected\",\"message\":\"无法连接本机自身设备\"}", token);
                            return;
                        }

                        if (isDeviceBlacklisted != null && isDeviceBlacklisted(fromId, fromIp))
                        {
                            await WriteJsonResponseAsync(stream, 200, "{\"ok\":false,\"status\":\"blacklisted\",\"message\":\"对方已将本机加入黑名单，禁止连接\"}", token);
                            return;
                        }

                        string incomingToken = query["sessionToken"] ?? "";

                        if (isDeviceWhitelisted != null && !string.IsNullOrEmpty(fromId) && isDeviceWhitelisted(fromId))
                        {
                            currentSessionToken = !string.IsNullOrEmpty(incomingToken) ? incomingToken : Guid.NewGuid().ToString("N");
                            currentConnectedPeerId = fromId;
                            NotifyPeerConnected(new LanPeerInfo
                            {
                                App = "YYComicReader",
                                DeviceId = fromId,
                                DeviceName = fromName,
                                Platform = fromPlatform,
                                Ip = fromIp,
                                Port = fromPort,
                                SessionToken = currentSessionToken
                            });
                            string whiteJson = $"{{\"ok\":true,\"status\":\"accepted\",\"message\":\"白名单免确认连接\",\"sessionToken\":{JsonSerializer.Serialize(currentSessionToken)}}}";
                            await WriteJsonResponseAsync(stream, 200, whiteJson, token);
                            return;
                        }

                        var reqState = new LanPairRequestState
                        {
                            ReqId = reqId,
                            FromId = fromId,
                            FromName = fromName,
                            FromPlatform = fromPlatform,
                            FromIp = fromIp,
                            FromPort = fromPort,
                            Status = "pending",
                            SessionToken = !string.IsNullOrEmpty(incomingToken) ? incomingToken : Guid.NewGuid().ToString("N"),
                            CreatedTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                            TimeoutSeconds = 30
                        };
                        pairRequests[reqId] = reqState;

                        onPairRequestReceived?.Invoke(reqState);

                        string json = $"{{\"ok\":true,\"status\":\"pending\",\"reqId\":{JsonSerializer.Serialize(reqId)}}}";
                        await WriteJsonResponseAsync(stream, 200, json, token);
                    }
                    else if (path.Equals("/api/connect-status", StringComparison.OrdinalIgnoreCase) || path.TrimStart('/').Equals("api/connect-status", StringComparison.OrdinalIgnoreCase))
                    {
                        string reqId = query["reqId"] ?? "";
                        if (!string.IsNullOrEmpty(reqId) && pairRequests.TryGetValue(reqId, out var state))
                        {
                            if (state.IsExpired)
                            {
                                state.Status = "timeout";
                            }
                            string tokenStr = !string.IsNullOrEmpty(state.SessionToken) ? $",\"sessionToken\":{JsonSerializer.Serialize(state.SessionToken)}" : "";
                            string json = $"{{\"ok\":true,\"status\":{JsonSerializer.Serialize(state.Status)}{tokenStr}}}";
                            await WriteJsonResponseAsync(stream, 200, json, token);
                        }
                        else
                        {
                            await WriteJsonResponseAsync(stream, 404, "{\"ok\":false,\"status\":\"not_found\"}", token);
                        }
                    }
                    else if (path.Equals("/api/connect-respond", StringComparison.OrdinalIgnoreCase) || path.TrimStart('/').Equals("api/connect-respond", StringComparison.OrdinalIgnoreCase))
                    {
                        string reqId = query["reqId"] ?? "";
                        string action = (query["action"] ?? "reject").ToLowerInvariant();
                        bool accept = action == "accept" || action == "1" || action == "true";

                        if (!string.IsNullOrEmpty(reqId) && pairRequests.TryGetValue(reqId, out var state))
                        {
                            if (state.IsExpired || state.Status == "timeout")
                            {
                                state.Status = "timeout";
                                await WriteJsonResponseAsync(stream, 200, "{\"ok\":false,\"status\":\"timeout\",\"error\":\"请求已超时失效\"}", token);
                                return;
                            }
                            state.Status = accept ? "accepted" : "rejected";
                            if (accept)
                            {
                                currentSessionToken = !string.IsNullOrEmpty(state.SessionToken) ? state.SessionToken : Guid.NewGuid().ToString("N");
                                currentConnectedPeerId = state.FromId;
                                state.SessionToken = currentSessionToken;
                                NotifyPeerConnected(new LanPeerInfo
                                {
                                    App = "YYComicReader",
                                    DeviceId = state.FromId,
                                    DeviceName = state.FromName,
                                    Platform = state.FromPlatform,
                                    Ip = state.FromIp,
                                    Port = state.FromPort,
                                    SessionToken = currentSessionToken
                                });
                            }
                            string tokenStr = !string.IsNullOrEmpty(state.SessionToken) ? $",\"sessionToken\":{JsonSerializer.Serialize(state.SessionToken)}" : "";
                            string json = $"{{\"ok\":true,\"status\":{JsonSerializer.Serialize(state.Status)}{tokenStr}}}";
                            await WriteJsonResponseAsync(stream, 200, json, token);
                        }
                        else
                        {
                            await WriteJsonResponseAsync(stream, 404, "{\"ok\":false,\"error\":\"Request not found\"}", token);
                        }
                    }
                    else if (path.Equals("/api/disconnect", StringComparison.OrdinalIgnoreCase) || path.TrimStart('/').Equals("api/disconnect", StringComparison.OrdinalIgnoreCase))
                    {
                        string fromId = query["fromId"] ?? "";
                        string fromName = query["fromName"] ?? "";
                        var remoteEp = client.Client.RemoteEndPoint as IPEndPoint;
                        string remoteIp = remoteEp?.Address.ToString() ?? "";
                        if (remoteEp?.Address.IsIPv4MappedToIPv6 == true)
                        {
                            remoteIp = remoteEp.Address.MapToIPv4().ToString();
                        }
                        else if (remoteIp.StartsWith("::ffff:", StringComparison.OrdinalIgnoreCase))
                        {
                            remoteIp = remoteIp.Substring(7);
                        }
                        currentSessionToken = null;
                        currentConnectedPeerId = null;
                        OnPeerDisconnected?.Invoke(fromId, fromName, remoteIp);
                        await WriteJsonResponseAsync(stream, 200, "{\"ok\":true}", token);
                    }
                    else if ((method == "POST" || method == "GET") && (path.Equals("/api/record-read", StringComparison.OrdinalIgnoreCase) || path.TrimStart('/').Equals("api/record-read", StringComparison.OrdinalIgnoreCase)))
                    {
                        string? id = query["id"];
                        string? title = query["title"];
                        int.TryParse(query["page"], out int page);
                        bool inc = query["inc"] == "1" || query["inc"] == null || string.Equals(query["inc"], "true", StringComparison.OrdinalIgnoreCase);
                        int? updatedCount = null;
                        if ((!string.IsNullOrEmpty(id) || !string.IsNullOrEmpty(title)) && onRemoteComicRead != null)
                        {
                            updatedCount = onRemoteComicRead(id ?? "", title, page, inc);
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
                    else if ((method == "POST" || method == "GET") && (path.Equals("/api/comic-delete", StringComparison.OrdinalIgnoreCase) || path.TrimStart('/').Equals("api/comic-delete", StringComparison.OrdinalIgnoreCase)))
                    {
                        string? id = query["id"];
                        string? title = query["title"];
                        bool success = false;
                        if ((!string.IsNullOrEmpty(id) || !string.IsNullOrEmpty(title)) && onRemoteComicDelete != null)
                        {
                            success = onRemoteComicDelete(id ?? "", title);
                        }

                        if (success)
                        {
                            await WriteJsonResponseAsync(stream, 200, "{\"ok\":true}", token);
                        }
                        else
                        {
                            await WriteJsonResponseAsync(stream, 404, "{\"ok\":false,\"error\":\"Comic not found or failed to delete\"}", token);
                        }
                    }
                    else if ((method == "POST" || method == "GET") && (path.Equals("/api/comic-rename", StringComparison.OrdinalIgnoreCase) || path.TrimStart('/').Equals("api/comic-rename", StringComparison.OrdinalIgnoreCase)))
                    {
                        string? id = query["id"];
                        string? title = query["title"];
                        string? newTitle = query["newTitle"];
                        bool success = false;
                        if (!string.IsNullOrEmpty(newTitle) && onRemoteComicRename != null)
                        {
                            success = onRemoteComicRename(id ?? "", title, newTitle);
                        }

                        if (success)
                        {
                            string jsonResp = JsonSerializer.Serialize(new { ok = true, id = id ?? "", newTitle = newTitle ?? "" });
                            await WriteJsonResponseAsync(stream, 200, jsonResp, token);
                        }
                        else
                        {
                            await WriteJsonResponseAsync(stream, 400, "{\"ok\":false,\"error\":\"Failed to rename comic\"}", token);
                        }
                    }
                    else if ((method == "POST" || method == "GET") && (path.Equals("/api/comic-set-cover-page", StringComparison.OrdinalIgnoreCase) || path.TrimStart('/').Equals("api/comic-set-cover-page", StringComparison.OrdinalIgnoreCase)))
                    {
                        string? id = query["id"];
                        int.TryParse(query["pageIndex"], out int pageIndex);
                        bool success = false;
                        string coverUrl = "";
                        if (!string.IsNullOrEmpty(id) && onRemoteComicSetCoverPage != null)
                        {
                            var res = onRemoteComicSetCoverPage(id, pageIndex);
                            success = res.ok;
                            coverUrl = res.coverUrl;
                        }

                        if (success)
                        {
                            string jsonResp = JsonSerializer.Serialize(new { ok = true, coverUrl });
                            await WriteJsonResponseAsync(stream, 200, jsonResp, token);
                        }
                        else
                        {
                            await WriteJsonResponseAsync(stream, 400, "{\"ok\":false,\"error\":\"Failed to set comic cover\"}", token);
                        }
                    }
                    else if ((method == "POST" || method == "GET") && (path.Equals("/api/comic-reset-cover", StringComparison.OrdinalIgnoreCase) || path.TrimStart('/').Equals("api/comic-reset-cover", StringComparison.OrdinalIgnoreCase)))
                    {
                        string? id = query["id"];
                        bool success = false;
                        if (!string.IsNullOrEmpty(id) && onRemoteComicResetCover != null)
                        {
                            success = onRemoteComicResetCover(id);
                        }

                        if (success)
                        {
                            await WriteJsonResponseAsync(stream, 200, "{\"ok\":true}", token);
                        }
                        else
                        {
                            await WriteJsonResponseAsync(stream, 400, "{\"ok\":false,\"error\":\"Failed to reset comic cover\"}", token);
                        }
                    }
                    else if (method == "POST" && (path.Equals("/api/comic-set-cover-file", StringComparison.OrdinalIgnoreCase) || path.TrimStart('/').Equals("api/comic-set-cover-file", StringComparison.OrdinalIgnoreCase)))
                    {
                        string? id = query["id"];
                        string? fileName = query["fileName"] ?? "cover.jpg";
                        if (contentLength > 0 && !string.IsNullOrEmpty(id) && onRemoteComicSetCoverFile != null)
                        {
                            byte[] imgBytes = new byte[contentLength];
                            int read = 0;
                            while (read < contentLength)
                            {
                                int r = await stream.ReadAsync(imgBytes, read, (int)(contentLength - read), token);
                                if (r <= 0) break;
                                read += r;
                            }
                            string ext = Path.GetExtension(fileName);
                            bool success = onRemoteComicSetCoverFile(id, imgBytes, ext);
                            if (success)
                            {
                                await WriteJsonResponseAsync(stream, 200, "{\"ok\":true}", token);
                            }
                            else
                            {
                                await WriteJsonResponseAsync(stream, 400, "{\"ok\":false,\"error\":\"Failed to save custom cover file\"}", token);
                            }
                        }
                        else
                        {
                            await WriteJsonResponseAsync(stream, 400, "{\"ok\":false,\"error\":\"Invalid request data\"}", token);
                        }
                    }
                    else if (method == "GET" && (path.Equals("/api/comic-progress", StringComparison.OrdinalIgnoreCase) || path.TrimStart('/').Equals("api/comic-progress", StringComparison.OrdinalIgnoreCase)))
                    {
                        string? id = query["id"];
                        string? title = query["title"];
                        var comic = getActiveComics().FirstOrDefault(c =>
                            (!string.IsNullOrEmpty(id) && string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase)) ||
                            (!string.IsNullOrEmpty(id) && string.Equals(c.Title, id, StringComparison.OrdinalIgnoreCase)) ||
                            (!string.IsNullOrEmpty(title) && string.Equals(c.Title, title, StringComparison.OrdinalIgnoreCase)) ||
                            (!string.IsNullOrEmpty(id) && NormalizeTitleKey(c.Title) == NormalizeTitleKey(id)) ||
                            (!string.IsNullOrEmpty(title) && NormalizeTitleKey(c.Title) == NormalizeTitleKey(title)));

                        if (comic != null)
                        {
                            int p = comic.LastReadPage > 0 ? comic.LastReadPage : 1;
                            string json = $"{{\"ok\":true,\"id\":{JsonSerializer.Serialize(comic.Id)},\"lastReadPage\":{p},\"readCount\":{comic.ReadCount}}}";
                            await WriteJsonResponseAsync(stream, 200, json, token);
                        }
                        else
                        {
                            await WriteJsonResponseAsync(stream, 404, "{\"ok\":false,\"error\":\"Comic not found\"}", token);
                        }
                    }
                    else if (method == "GET" && path.Equals("/api/comics", StringComparison.OrdinalIgnoreCase))
                    {
                        string? fromId = query["fromId"];
                        string? sessionToken = query["sessionToken"];
                        var remoteEp = client.Client.RemoteEndPoint as IPEndPoint;
                        string remoteIp = remoteEp?.Address.ToString() ?? "";

                        if (isDeviceBlacklisted != null && isDeviceBlacklisted(fromId ?? "", remoteIp))
                        {
                            await WriteJsonResponseAsync(stream, 403, "{\"error\":\"Device blacklisted\"}", token);
                            return;
                        }

                        if (!IsRequestAuthorized(fromId, remoteIp, sessionToken))
                        {
                            await WriteJsonResponseAsync(stream, 401, "{\"ok\":false,\"error\":\"session_expired\",\"message\":\"未授权或连接会话已失效，请重新连接\"}", token);
                            return;
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
                        if (!string.IsNullOrEmpty(comic.CustomCoverPath) && (File.Exists(comic.CustomCoverPath) || ArchiveComicHelper.TryParsePagePath(comic.CustomCoverPath, out _, out _)))
                        {
                            coverFile = comic.CustomCoverPath;
                        }
                        else if (!string.IsNullOrEmpty(comic.CoverPath))
                        {
                            coverFile = comic.CoverPath;
                        }
                        else if (comic.PagePaths != null && comic.PagePaths.Count > 0)
                        {
                            coverFile = comic.PagePaths[0];
                        }

                        if (string.IsNullOrEmpty(coverFile))
                        {
                            await WriteJsonResponseAsync(stream, 404, "{\"error\":\"Cover missing\"}", token);
                            return;
                        }

                        byte[]? fileBytes = null;
                        if (ArchiveComicHelper.TryParsePagePath(coverFile, out string arcPath, out string entryKey))
                        {
                            fileBytes = ArchiveComicHelper.ExtractEntryBytes(arcPath, entryKey);
                        }
                        else if (File.Exists(coverFile))
                        {
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
                        }

                        if (fileBytes == null || fileBytes.Length == 0)
                        {
                            await WriteJsonResponseAsync(stream, 404, "{\"error\":\"Cover missing or unreadable\"}", token);
                            return;
                        }

                        string header = $"HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\nContent-Length: {fileBytes.Length}\r\nConnection: close\r\n\r\n";
                        byte[] hBytes = Encoding.ASCII.GetBytes(header);
                        await stream.WriteAsync(hBytes, token);
                        await stream.WriteAsync(fileBytes, token);
                        await stream.FlushAsync(token);
                    }
                    else if (method == "GET" && path.Equals("/api/comic-manifest", StringComparison.OrdinalIgnoreCase))
                    {
                        string? fromId = query["fromId"];
                        string? sessionToken = query["sessionToken"];
                        var remoteEp = client.Client.RemoteEndPoint as IPEndPoint;
                        string remoteIp = remoteEp?.Address.ToString() ?? "";

                        if (isDeviceBlacklisted != null && isDeviceBlacklisted(fromId ?? "", remoteIp))
                        {
                            await WriteJsonResponseAsync(stream, 403, "{\"error\":\"Device blacklisted\"}", token);
                            return;
                        }

                        if (!IsRequestAuthorized(fromId, remoteIp, sessionToken))
                        {
                            await WriteJsonResponseAsync(stream, 401, "{\"ok\":false,\"error\":\"session_expired\",\"message\":\"未授权或连接会话已失效，请重新连接\"}", token);
                            return;
                        }

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
                                string fileName;
                                if (ArchiveComicHelper.TryParsePagePath(p, out _, out string eKey))
                                {
                                    fileName = Path.GetFileName(eKey);
                                }
                                else
                                {
                                    fileName = Path.GetFileName(p);
                                    try { if (File.Exists(p)) sz = new FileInfo(p).Length; } catch { }
                                }
                                return new LanPageEntry
                                {
                                    Index = idx,
                                    FileName = fileName,
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
                        byte[]? fileBytes = null;
                        if (ArchiveComicHelper.TryParsePagePath(filePath, out string arcPath, out string entryKey))
                        {
                            fileBytes = ArchiveComicHelper.ExtractEntryBytes(arcPath, entryKey);
                        }
                        else if (File.Exists(filePath))
                        {
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
                        }

                        if (fileBytes == null || fileBytes.Length == 0)
                        {
                            await WriteJsonResponseAsync(stream, 404, "{\"error\":\"File missing\"}", token);
                            return;
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

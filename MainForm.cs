using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web;
using System.Windows.Forms;
using ComicReaderDesktop.Models;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace ComicReaderDesktop
{
    public class MainForm : Form
    {
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_CAPTION_COLOR = 35;

        private readonly WebView2 webView;
        private readonly string appRootDir;
        private readonly string appDataDir;
        private readonly string customCoversDir;
        private readonly string defaultTransferDir;
        private readonly string dbFilePath;
        private readonly string htmlFilePath;
        private readonly object dbLock = new();
        private readonly LanTransferService lanService;

        private ShelfDatabase db = new();
        private readonly Task dbLoadTask;
        private Panel? startupSplashPanel;
        private bool isScanning = false;
        private bool isWebViewReady = false;
        private volatile bool isClosing = false;
        private volatile bool isDatabaseDirty = false;
        private int saveScheduledFlag = 0;

        private static readonly HashSet<string> SupportedImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".webp", ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".avif"
        };

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        public MainForm()
        {
            Text = "YY漫画 · ComicStream 极速漫画桌面版";
            Width = 1360;
            Height = 880;
            MinimumSize = new Size(920, 620);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(18, 19, 24);

            appRootDir = ResolveAppRootDir();
            appDataDir = Path.Combine(appRootDir, "AppData");
            customCoversDir = Path.Combine(appDataDir, "CustomCovers");
            defaultTransferDir = Path.Combine(appRootDir, "TransferredComics");
            Directory.CreateDirectory(appDataDir);
            Directory.CreateDirectory(customCoversDir);
            Directory.CreateDirectory(defaultTransferDir);

            LoadAppIcon();

            dbFilePath = Path.Combine(appDataDir, "shelf_data.json");
            htmlFilePath = ResolveHtmlFilePath(appRootDir);

            lanService = new LanTransferService(
                getActiveComics: () =>
                {
                    lock (dbLock)
                    {
                        return db.Comics.Where(c => !c.IsMissing).ToList();
                    }
                },
                getTransferSaveDir: GetCurrentTransferSaveDir,
                onComicReceived: OnLanComicReceived,
                onTransferProgress: (status, percent, detail) =>
                {
                    SendEventToUi("lanTransferProgress", new { status, percent, detail });
                },
                onPeerConnected: peer =>
                {
                    SendEventToUi("lanPeerConnected", peer);
                },
                onBatchTransferComplete: (mode, peerName, transferredCount, skippedCount) =>
                {
                    SendEventToUi("lanBatchComplete", new
                    {
                        mode,
                        peerName,
                        transferredCount,
                        skippedCount,
                        state = BuildInitPayload()
                    });
                },
                onRemoteComicRead: (id, title, page, inc) =>
                {
                    lock (dbLock)
                    {
                        var target = db.Comics.FirstOrDefault(c =>
                            (!string.IsNullOrEmpty(id) && string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase)) ||
                            (!string.IsNullOrEmpty(id) && string.Equals(c.Title, id, StringComparison.OrdinalIgnoreCase)) ||
                            (!string.IsNullOrEmpty(title) && string.Equals(c.Title, title, StringComparison.OrdinalIgnoreCase)) ||
                            (!string.IsNullOrEmpty(id) && LanTransferService.NormalizeTitleKey(c.Title) == LanTransferService.NormalizeTitleKey(id)) ||
                            (!string.IsNullOrEmpty(title) && LanTransferService.NormalizeTitleKey(c.Title) == LanTransferService.NormalizeTitleKey(title)));

                        if (target != null)
                        {
                            if (inc)
                            {
                                target.ReadCount++;
                                target.LastReadTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                            }
                            if (page > 1)
                            {
                                target.LastReadPage = page;
                            }
                            SyncComicToHistory(target);
                            Task.Run(() => SaveDatabase());
                            SendEventToUi("shelfUpdated", BuildInitPayload());
                            return target.ReadCount;
                        }
                    }
                    return null;
                }
            );

            // 后台并行读取 U 盘数据库与启动局域网监听，不阻塞窗口首帧创建
            dbLoadTask = Task.Run(() =>
            {
                LoadDatabase();
                try { lanService.Start(); } catch { }
            });

            webView = new WebView2
            {
                Dock = DockStyle.Fill,
                DefaultBackgroundColor = Color.FromArgb(18, 19, 24)
            };

            // 原生瞬时启动过渡层：在第 0 毫秒直接绘制顶栏与加载态，彻底消除 WebView2 初始化期间的黑屏
            startupSplashPanel = CreateStartupSplashPanel();
            Controls.Add(startupSplashPanel);
            Controls.Add(webView);
            startupSplashPanel.BringToFront();

            Load += MainForm_Load;
            Activated += MainForm_Activated;
        }

        private Panel CreateStartupSplashPanel()
        {
            var panel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(18, 19, 24)
            };
            typeof(Panel).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?.SetValue(panel, true, null);

            panel.Paint += (s, e) =>
            {
                var g = e.Graphics;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

                // 绘制与前端完全一致的顶部 Studio 导航栏 (#1A1D26)
                using (var headerBrush = new SolidBrush(Color.FromArgb(26, 29, 38)))
                {
                    g.FillRectangle(headerBrush, 0, 0, panel.Width, 64);
                }
                using (var borderPen = new Pen(Color.FromArgb(42, 47, 66), 1))
                {
                    g.DrawLine(borderPen, 0, 64, panel.Width, 64);
                }

                using (var brandFont = new Font("Microsoft YaHei UI", 13F, FontStyle.Bold))
                using (var whiteBrush = new SolidBrush(Color.FromArgb(248, 250, 252)))
                {
                    g.DrawString("YY漫画阅读器", brandFont, whiteBrush, 28, 19);
                }

                // 绘制中央卡片与加载提示
                int cardW = 420;
                int cardH = 130;
                int cardX = (panel.Width - cardW) / 2;
                int cardY = (panel.Height - cardH) / 2;

                using (var cardBrush = new SolidBrush(Color.FromArgb(26, 29, 38)))
                using (var cardBorder = new Pen(Color.FromArgb(42, 47, 66), 1))
                {
                    g.FillRectangle(cardBrush, cardX, cardY, cardW, cardH);
                    g.DrawRectangle(cardBorder, cardX, cardY, cardW, cardH);
                }

                using (var titleFont = new Font("Microsoft YaHei UI", 14F, FontStyle.Bold))
                using (var subFont = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Regular))
                using (var titleBrush = new SolidBrush(Color.FromArgb(248, 250, 252)))
                using (var subBrush = new SolidBrush(Color.FromArgb(148, 163, 184)))
                {
                    var sf = new StringFormat { Alignment = StringAlignment.Center };
                    g.DrawString("YY 漫阅 · 正在极速打开书架", titleFont, titleBrush, panel.Width / 2f, cardY + 34, sf);
                    g.DrawString("正在并行加载本地藏书索引与渲染引擎...", subFont, subBrush, panel.Width / 2f, cardY + 74, sf);
                }
            };

            panel.Resize += (s, e) => panel.Invalidate();
            return panel;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            isClosing = true;
            isWebViewReady = false;

            try
            {
                if (webView?.CoreWebView2 != null)
                {
                    webView.CoreWebView2.WebResourceRequested -= Core_WebResourceRequested;
                    webView.CoreWebView2.WebMessageReceived -= Core_WebMessageReceived;
                }
            }
            catch { }

            if (isDatabaseDirty)
            {
                SaveDatabaseSync();
            }

            Hide();
            _ = Task.Run(() =>
            {
                try { lanService.Dispose(); } catch { }
            });

            base.OnFormClosing(e);
            Environment.Exit(0);
        }

        private string GetCurrentTransferSaveDir()
        {
            string path;
            lock (dbLock)
            {
                path = db.TransferSavePath;
            }
            if (string.IsNullOrWhiteSpace(path))
            {
                path = defaultTransferDir;
            }
            try
            {
                Directory.CreateDirectory(path);
            }
            catch
            {
                path = defaultTransferDir;
                Directory.CreateDirectory(path);
            }
            return path;
        }

        private void OnLanComicReceived(string comicTitle, string comicDir)
        {
            try
            {
                var images = GetSortedImagesInDirectory(comicDir);
                if (images.Count == 0) return;

                ImportImageFilesAsComicAsync(images).GetAwaiter().GetResult();
                SendEventToUi("lanComicReceived", new
                {
                    title = comicTitle,
                    folderPath = comicDir,
                    state = BuildInitPayload()
                });
            }
            catch { }
        }

        private void LoadAppIcon()
        {
            try
            {
                var icoStream = typeof(MainForm).Assembly.GetManifestResourceStream("app.ico");
                if (icoStream != null)
                {
                    Icon = new Icon(icoStream);
                    return;
                }

                string diskIco = Path.Combine(appRootDir, "app.ico");
                if (File.Exists(diskIco))
                {
                    Icon = new Icon(diskIco);
                    return;
                }

                var assoc = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (assoc != null)
                {
                    Icon = assoc;
                }
            }
            catch
            {
                // Ignore icon load errors
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyDarkTitleBar();
        }

        private const int WM_XBUTTONDOWN = 0x020B;
        private const int WM_XBUTTONUP = 0x020C;
        private const int WM_APPCOMMAND = 0x0319;
        private const int XBUTTON1 = 0x0001;
        private const int APPCOMMAND_BROWSER_BACKWARD = 1;
        private const int APPCOMMAND_BROWSER_FORWARD = 2;

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_APPCOMMAND)
            {
                int cmd = (int)((long)m.LParam >> 16) & 0xFFF;
                if (cmd == APPCOMMAND_BROWSER_BACKWARD)
                {
                    if (isWebViewReady)
                    {
                        SendEventToUi("mouseBackButton", null);
                    }
                    m.Result = (IntPtr)1;
                    return;
                }
                if (cmd == APPCOMMAND_BROWSER_FORWARD)
                {
                    m.Result = (IntPtr)1;
                    return;
                }
            }
            else if (m.Msg == WM_XBUTTONUP)
            {
                int button = (int)((long)m.WParam >> 16) & 0xFFFF;
                if (button == XBUTTON1)
                {
                    if (isWebViewReady)
                    {
                        SendEventToUi("mouseBackButton", null);
                    }
                    m.Result = (IntPtr)1;
                    return;
                }
            }
            else if (m.Msg == WM_XBUTTONDOWN)
            {
                int button = (int)((long)m.WParam >> 16) & 0xFFFF;
                if (button == XBUTTON1)
                {
                    m.Result = (IntPtr)1;
                    return;
                }
            }

            base.WndProc(ref m);
        }

        private void ApplyDarkTitleBar()
        {
            try
            {
                int useDark = 1;
                DwmSetWindowAttribute(Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDark, sizeof(int));
                // COLORREF BGR format for #121318 -> 0x00181312
                int captionColor = 0x00181312;
                DwmSetWindowAttribute(Handle, DWMWA_CAPTION_COLOR, ref captionColor, sizeof(int));
            }
            catch
            {
                // Ignore on older Windows builds
            }
        }

        private static string ResolveAppRootDir()
        {
            string baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            DirectoryInfo? dir = new(baseDir);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "ComicReaderDesktop.csproj")) ||
                    Directory.Exists(Path.Combine(dir.FullName, "wwwroot")))
                {
                    return dir.FullName;
                }
                dir = dir.Parent;
            }
            return baseDir;
        }

        private static string ResolveHtmlFilePath(string rootDir)
        {
            string candidate1 = Path.Combine(rootDir, "wwwroot", "index.html");
            if (File.Exists(candidate1)) return candidate1;

            string candidate2 = Path.Combine(AppContext.BaseDirectory, "wwwroot", "index.html");
            return candidate2;
        }

        private void DismissStartupSplash()
        {
            if (startupSplashPanel != null)
            {
                startupSplashPanel.Visible = false;
                Controls.Remove(startupSplashPanel);
                startupSplashPanel.Dispose();
                startupSplashPanel = null;
            }
        }

        private async void MainForm_Load(object? sender, EventArgs e)
        {
            try
            {
                startupSplashPanel?.BringToFront();
                startupSplashPanel?.Refresh();

                // 将 WebView2 浏览器内核临时缓存放在本机高速系统盘 (%LocalAppData%)，避免在 U 盘 (E:) 上创建数百个 Chromium 临时文件导致黑屏卡顿 2~4 秒
                // 书架数据 (shelf_data.json) 与自定义封面依然保存在 U 盘 AppData 目录中，保持完全便携
                string localSysAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string webViewCacheDir = !string.IsNullOrEmpty(localSysAppData)
                    ? Path.Combine(localSysAppData, "YYComicReaderDesktop", "WebView2Cache")
                    : Path.Combine(appDataDir, "WebView2Cache");
                Directory.CreateDirectory(webViewCacheDir);

                var env = await CoreWebView2Environment.CreateAsync(null, webViewCacheDir);
                await webView.EnsureCoreWebView2Async(env);
                startupSplashPanel?.BringToFront();
                startupSplashPanel?.Refresh();

                var core = webView.CoreWebView2;
                core.Settings.IsStatusBarEnabled = false;
                core.Settings.AreDefaultContextMenusEnabled = false;
                core.Settings.IsZoomControlEnabled = true;

                webView.AllowExternalDrop = true;

                core.AddWebResourceRequestedFilter("https://comic.local/*", CoreWebView2WebResourceContext.All);
                core.WebResourceRequested += Core_WebResourceRequested;
                core.WebMessageReceived += Core_WebMessageReceived;
                core.NavigationCompleted += (s, args) =>
                {
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(1200);
                        if (!isClosing && IsHandleCreated)
                        {
                            try { BeginInvoke(new Action(DismissStartupSplash)); } catch { }
                        }
                    });
                };
                core.NavigationStarting += (s, args) =>
                {
                    if (!args.Uri.StartsWith("https://comic.local", StringComparison.OrdinalIgnoreCase))
                    {
                        args.Cancel = true;
                        SendEventToUi("mouseBackButton", null);
                    }
                };

                isWebViewReady = true;
                core.Navigate("https://comic.local/index.html");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"初始化桌面引擎失败: {ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void MainForm_Activated(object? sender, EventArgs e)
        {
            if (isClosing || !isWebViewReady || isScanning) return;

            // 窗口重新激活时在后台线程异步核验文件存在状态，绝不阻塞 UI 主线程
            _ = Task.Run(() =>
            {
                if (isClosing) return;
                if (VerifyAllComicsExistence(out _))
                {
                    SaveDatabase();
                    SendEventToUi("shelfUpdated", BuildInitPayload());
                }
            });
        }

        private void Core_WebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
        {
            if (isClosing) return;
            try
            {
                var uri = new Uri(e.Request.Uri);
                if (!string.Equals(uri.Host, "comic.local", StringComparison.OrdinalIgnoreCase))
                    return;

                if (uri.AbsolutePath == "/" || uri.AbsolutePath.Equals("/index.html", StringComparison.OrdinalIgnoreCase))
                {
                    string htmlPath = ResolveHtmlFilePath(appRootDir);
                    if (File.Exists(htmlPath))
                    {
                        byte[] bytes = File.ReadAllBytes(htmlPath);
                        var ms = new MemoryStream(bytes);
                        e.Response = webView.CoreWebView2.Environment.CreateWebResourceResponse(
                            ms, 200, "OK", "Content-Type: text/html; charset=utf-8\r\nCache-Control: no-store");
                        return;
                    }
                    else
                    {
                        var asmStream = typeof(MainForm).Assembly.GetManifestResourceStream("wwwroot.index.html");
                        if (asmStream != null)
                        {
                            e.Response = webView.CoreWebView2.Environment.CreateWebResourceResponse(
                                asmStream, 200, "OK", "Content-Type: text/html; charset=utf-8\r\nCache-Control: no-store");
                            return;
                        }
                    }
                }
                else if (uri.AbsolutePath.Equals("/app-icon.png", StringComparison.OrdinalIgnoreCase))
                {
                    string iconPath = Path.Combine(appRootDir, "wwwroot", "app-icon.png");
                    if (File.Exists(iconPath))
                    {
                        byte[] bytes = File.ReadAllBytes(iconPath);
                        var ms = new MemoryStream(bytes);
                        e.Response = webView.CoreWebView2.Environment.CreateWebResourceResponse(
                            ms, 200, "OK", "Content-Type: image/png\r\nCache-Control: public, max-age=86400");
                        return;
                    }
                    var iconStream = typeof(MainForm).Assembly.GetManifestResourceStream("wwwroot.app-icon.png");
                    if (iconStream != null)
                    {
                        e.Response = webView.CoreWebView2.Environment.CreateWebResourceResponse(
                            iconStream, 200, "OK", "Content-Type: image/png\r\nCache-Control: public, max-age=86400");
                        return;
                    }
                }
                else if (uri.AbsolutePath.Equals("/img", StringComparison.OrdinalIgnoreCase))
                {
                    var query = HttpUtility.ParseQueryString(uri.Query);
                    string? filePath = query["path"];
                    if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
                    {
                        // 使用 FileShare.ReadWrite | FileShare.Delete 读取到内存后立即关闭文件句柄，绝不锁死本地文件
                        byte[] bytes;
                        using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                        {
                            bytes = new byte[fs.Length];
                            int read = 0;
                            while (read < bytes.Length)
                            {
                                int n = fs.Read(bytes, read, bytes.Length - read);
                                if (n <= 0) break;
                                read += n;
                            }
                        }

                        var ms = new MemoryStream(bytes);
                        string mime = GetMimeType(filePath);
                        e.Response = webView.CoreWebView2.Environment.CreateWebResourceResponse(
                            ms, 200, "OK", $"Content-Type: {mime}\r\nCache-Control: max-age=3600\r\nAccess-Control-Allow-Origin: *");
                        return;
                    }

                    e.Response = webView.CoreWebView2.Environment.CreateWebResourceResponse(
                        null, 404, "Not Found", "Content-Type: text/plain");
                }
            }
            catch
            {
                e.Response = webView.CoreWebView2.Environment.CreateWebResourceResponse(
                    null, 500, "Internal Error", "Content-Type: text/plain");
            }
        }

        private static string GetMimeType(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            return ext switch
            {
                ".webp" => "image/webp",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".png" => "image/png",
                ".gif" => "image/gif",
                ".bmp" => "image/bmp",
                ".avif" => "image/avif",
                _ => "application/octet-stream"
            };
        }

        private async void Core_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            int reqId = 0;
            try
            {
                string raw = e.TryGetWebMessageAsString();
                using var doc = JsonDocument.Parse(raw);
                var root = doc.RootElement;

                reqId = root.TryGetProperty("reqId", out var reqIdProp) ? reqIdProp.GetInt32() : 0;
                string action = root.TryGetProperty("action", out var actProp) ? (actProp.GetString() ?? "") : "";
                JsonElement payload = root.TryGetProperty("payload", out var payProp) ? payProp : default;

                // 处理来自前端拖拽的真实磁盘文件/文件夹对象 (AdditionalObjects)
                if (action == "dropFiles" && e.AdditionalObjects != null && e.AdditionalObjects.Count > 0)
                {
                    var droppedPaths = new List<string>();
                    foreach (var obj in e.AdditionalObjects)
                    {
                        if (obj is CoreWebView2File webFile && !string.IsNullOrEmpty(webFile.Path))
                        {
                            droppedPaths.Add(webFile.Path);
                        }
                    }

                    SendResponse(reqId, new { accepted = droppedPaths.Count });
                    if (droppedPaths.Count > 0)
                    {
                        await HandleDroppedPathsAsync(droppedPaths);
                    }
                    return;
                }

                object? result = await HandleApiActionAsync(action, payload);
                SendResponse(reqId, result);
                if (action == "init")
                {
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(80);
                        if (!isClosing && IsHandleCreated)
                        {
                            try { BeginInvoke(new Action(DismissStartupSplash)); } catch { }
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                SendError(reqId, ex.Message);
            }
        }

        private async Task<object?> HandleApiActionAsync(string action, JsonElement payload)
        {
            switch (action)
            {
                case "init":
                {
                    await dbLoadTask;
                    _ = Task.Run(() =>
                    {
                        if (!isClosing && VerifyAllComicsExistence(out _))
                        {
                            SaveDatabase();
                            SendEventToUi("shelfUpdated", BuildInitPayload());
                        }
                    });
                    return BuildInitPayload();
                }

                case "getComicPages":
                {
                    string id = payload.TryGetProperty("id", out var idEl) ? (idEl.GetString() ?? "") : "";
                    lock (dbLock)
                    {
                        var target = db.Comics.FirstOrDefault(c => c.Id == id);
                        if (target != null)
                        {
                            if ((target.PagePaths == null || target.PagePaths.Count == 0) &&
                                !string.IsNullOrEmpty(target.FolderPath) && Directory.Exists(target.FolderPath))
                            {
                                target.PagePaths = GetSortedImagesInDirectory(target.FolderPath);
                                target.TotalPages = target.PagePaths.Count;
                            }
                            return new
                            {
                                id = target.Id,
                                pagePaths = target.PagePaths ?? new List<string>()
                            };
                        }
                    }
                    return new { id, pagePaths = Array.Empty<string>() };
                }

                case "pickFolder":
                {
                    using var dialog = new FolderBrowserDialog
                    {
                        Description = "选择包含漫画的文件夹（支持总目录或单本漫画文件夹）",
                        UseDescriptionForTitle = true,
                        ShowNewFolderButton = false
                    };
                    if (!string.IsNullOrEmpty(db.LastRootFolder) && Directory.Exists(db.LastRootFolder))
                    {
                        dialog.InitialDirectory = db.LastRootFolder;
                    }

                    if (dialog.ShowDialog(this) == DialogResult.OK && !string.IsNullOrEmpty(dialog.SelectedPath))
                    {
                        string selected = dialog.SelectedPath;
                        lock (dbLock)
                        {
                            db.LastRootFolder = selected;
                            SaveDatabase();
                        }
                        _ = Task.Run(() => ScanFoldersAsync(new List<string> { selected }));
                        return new { started = true, path = selected };
                    }
                    return new { started = false };
                }

                case "refreshLastFolder":
                {
                    string lastRoot;
                    lock (dbLock)
                    {
                        lastRoot = db.LastRootFolder;
                    }
                    if (!string.IsNullOrEmpty(lastRoot) && Directory.Exists(lastRoot))
                    {
                        _ = Task.Run(() => ScanFoldersAsync(new List<string> { lastRoot }));
                        return new { started = true, path = lastRoot };
                    }
                    else
                    {
                        // 若未记录过目录，则弹出选择框
                        return await HandleApiActionAsync("pickFolder", payload);
                    }
                }

                case "pickImages":
                {
                    using var dialog = new OpenFileDialog
                    {
                        Title = "多选漫画切片图片导入",
                        Filter = "漫画图片 (*.webp;*.jpg;*.jpeg;*.png;*.gif;*.bmp;*.avif)|*.webp;*.jpg;*.jpeg;*.png;*.gif;*.bmp;*.avif|所有文件 (*.*)|*.*",
                        Multiselect = true
                    };
                    if (dialog.ShowDialog(this) == DialogResult.OK && dialog.FileNames.Length > 0)
                    {
                        await ImportImageFilesAsComicAsync(dialog.FileNames);
                        return BuildInitPayload();
                    }
                    return null;
                }

                case "openComic":
                {
                    string id = payload.GetProperty("id").GetString() ?? "";
                    ComicRecord? target = null;
                    lock (dbLock)
                    {
                        target = db.Comics.FirstOrDefault(c => c.Id == id);
                        if (target != null)
                        {
                            // 动态检查该文件夹下最新图片列表
                            if (!string.IsNullOrEmpty(target.FolderPath) && Directory.Exists(target.FolderPath))
                            {
                                var latestPages = GetSortedImagesInDirectory(target.FolderPath);
                                if (latestPages.Count > 0)
                                {
                                    target.PagePaths = latestPages;
                                    target.TotalPages = latestPages.Count;
                                    string customCover = ResolveCustomCoverPath(target, GetComicHistory(target.Id, target.Title));
                                    target.CustomCoverPath = customCover;
                                    target.CoverPath = !string.IsNullOrEmpty(customCover) ? customCover : latestPages[0];
                                    target.IsMissing = false;
                                }
                            }

                            target.ReadCount++;
                            target.LastReadTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                            SyncComicToHistory(target);
                            SaveDatabase();
                        }
                    }
                    return target;
                }

                case "saveProgress":
                {
                    string id = payload.GetProperty("id").GetString() ?? "";
                    int page = payload.TryGetProperty("page", out var p) ? p.GetInt32() : 1;
                    lock (dbLock)
                    {
                        var target = db.Comics.FirstOrDefault(c => c.Id == id);
                        if (target != null)
                        {
                            target.LastReadPage = Math.Max(1, page);
                            SyncComicToHistory(target);
                            SaveDatabase();
                        }
                    }
                    return new { ok = true };
                }

                case "saveSettings":
                {
                    lock (dbLock)
                    {
                        if (payload.TryGetProperty("sortMode", out var sm) && sm.ValueKind == JsonValueKind.String)
                            db.SortMode = sm.GetString() ?? "title_asc";
                        if (payload.TryGetProperty("pageSize", out var ps) && ps.ValueKind == JsonValueKind.Number)
                            db.PageSize = ps.GetInt32();
                        if (payload.TryGetProperty("readerWidth", out var rw) && rw.ValueKind == JsonValueKind.String)
                            db.ReaderWidth = rw.GetString() ?? "900px";
                        SaveDatabase();
                    }
                    return new { ok = true };
                }

                case "removeComics":
                {
                    var ids = new HashSet<string>();
                    if (payload.TryGetProperty("ids", out var idsArr) && idsArr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in idsArr.EnumerateArray())
                        {
                            string? s = item.GetString();
                            if (!string.IsNullOrEmpty(s)) ids.Add(s);
                        }
                    }

                    int deletedPhysicalCount = 0;
                    lock (dbLock)
                    {
                        var toRemove = db.Comics.Where(c => ids.Contains(c.Id)).ToList();
                        foreach (var c in toRemove)
                        {
                            if (DeleteComicPhysicalFiles(c))
                            {
                                deletedPhysicalCount++;
                            }
                            PurgeComicHistory(c);
                            db.Comics.Remove(c);
                        }
                        SaveDatabase();
                    }
                    return BuildInitPayload();
                }

                case "pickCustomCover":
                {
                    string id = payload.TryGetProperty("id", out var idEl) ? (idEl.GetString() ?? "") : "";
                    ComicRecord? comic;
                    lock (dbLock)
                    {
                        comic = db.Comics.FirstOrDefault(c => c.Id == id);
                    }
                    if (comic == null)
                    {
                        return new { changed = false };
                    }

                    using var dialog = new OpenFileDialog
                    {
                        Title = $"为《{comic.Title}》选择自定义封面图片",
                        Filter = "图片文件 (*.webp;*.jpg;*.jpeg;*.png;*.gif;*.bmp;*.avif)|*.webp;*.jpg;*.jpeg;*.png;*.gif;*.bmp;*.avif|所有文件 (*.*)|*.*",
                        Multiselect = false
                    };
                    if (!string.IsNullOrEmpty(comic.FolderPath) && Directory.Exists(comic.FolderPath))
                    {
                        dialog.InitialDirectory = comic.FolderPath;
                    }

                    if (dialog.ShowDialog(this) == DialogResult.OK && !string.IsNullOrEmpty(dialog.FileName) && File.Exists(dialog.FileName))
                    {
                        string ext = Path.GetExtension(dialog.FileName);
                        if (string.IsNullOrEmpty(ext)) ext = ".jpg";
                        Directory.CreateDirectory(customCoversDir);
                        CleanupCustomCoverFilesForId(id);

                        string destFileName = $"{id}_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}{ext}";
                        string destPath = Path.Combine(customCoversDir, destFileName);
                        File.Copy(dialog.FileName, destPath, true);

                        lock (dbLock)
                        {
                            comic.CustomCoverPath = destPath;
                            comic.CoverPath = destPath;
                            SyncComicToHistory(comic);
                            SaveDatabase();
                        }

                        return new
                        {
                            changed = true,
                            comic,
                            state = BuildInitPayload()
                        };
                    }
                    return new { changed = false };
                }

                case "setComicCoverFromPage":
                {
                    string id = payload.TryGetProperty("id", out var idEl) ? (idEl.GetString() ?? "") : "";
                    int pageIndex = payload.TryGetProperty("pageIndex", out var pEl) && pEl.ValueKind == JsonValueKind.Number
                        ? pEl.GetInt32()
                        : 0;

                    ComicRecord? comic = null;
                    lock (dbLock)
                    {
                        comic = db.Comics.FirstOrDefault(c => c.Id == id);
                        if (comic != null && comic.PagePaths != null && pageIndex >= 0 && pageIndex < comic.PagePaths.Count)
                        {
                            string pagePath = comic.PagePaths[pageIndex];
                            if (File.Exists(pagePath))
                            {
                                CleanupCustomCoverFilesForId(id);
                                comic.CustomCoverPath = pagePath;
                                comic.CoverPath = pagePath;
                                SyncComicToHistory(comic);
                                SaveDatabase();
                            }
                        }
                    }

                    return new
                    {
                        changed = comic != null,
                        comic,
                        state = BuildInitPayload()
                    };
                }

                case "resetComicCover":
                {
                    string id = payload.TryGetProperty("id", out var idEl) ? (idEl.GetString() ?? "") : "";
                    ComicRecord? comic = null;
                    lock (dbLock)
                    {
                        comic = db.Comics.FirstOrDefault(c => c.Id == id);
                        if (comic != null)
                        {
                            CleanupCustomCoverFilesForId(id);
                            comic.CustomCoverPath = "";
                            if (comic.PagePaths != null && comic.PagePaths.Count > 0)
                            {
                                comic.CoverPath = comic.PagePaths[0];
                            }
                            SyncComicToHistory(comic);
                            SaveDatabase();
                        }
                    }

                    return new
                    {
                        changed = comic != null,
                        comic,
                        state = BuildInitPayload()
                    };
                }

                case "renameComic":
                {
                    string id = payload.TryGetProperty("id", out var idEl) ? (idEl.GetString() ?? "") : "";
                    string rawNewTitle = payload.TryGetProperty("newTitle", out var tEl) ? (tEl.GetString() ?? "") : "";
                    string newTitle = rawNewTitle.Trim();

                    if (string.IsNullOrEmpty(newTitle))
                    {
                        return new { ok = false, message = "漫画名称不能为空" };
                    }

                    char[] invalidChars = Path.GetInvalidFileNameChars();
                    if (newTitle.IndexOfAny(invalidChars) >= 0)
                    {
                        return new { ok = false, message = "漫画名称包含 Windows 文件名不支持的特殊字符（如 \\ / : * ? \" < > |）" };
                    }

                    lock (dbLock)
                    {
                        var comic = db.Comics.FirstOrDefault(c => c.Id == id);
                        if (comic == null)
                        {
                            return new { ok = false, message = "未找到该漫画记录" };
                        }

                        if (string.Equals(comic.Title, newTitle, StringComparison.Ordinal))
                        {
                            return new { ok = true, comic, state = BuildInitPayload() };
                        }

                        bool duplicateInShelf = db.Comics.Any(c =>
                            c.Id != id && string.Equals(c.Title, newTitle, StringComparison.OrdinalIgnoreCase));
                        if (duplicateInShelf)
                        {
                            return new { ok = false, message = $"书架上已存在名为《{newTitle}》的漫画，请使用其他名称" };
                        }

                        string oldFolder = comic.FolderPath ?? "";
                        string newFolder = oldFolder;

                        if (!string.IsNullOrEmpty(oldFolder) && Directory.Exists(oldFolder))
                        {
                            string fullOldFolder = Path.GetFullPath(oldFolder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                            string? parentDir = Path.GetDirectoryName(fullOldFolder);
                            string? rootDrive = Path.GetPathRoot(fullOldFolder)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                            string fullAppRoot = Path.GetFullPath(appRootDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                            string fullAppData = Path.GetFullPath(appDataDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                            bool isProtectedDir =
                                string.Equals(fullOldFolder, rootDrive, StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(fullOldFolder, fullAppRoot, StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(fullOldFolder, fullAppData, StringComparison.OrdinalIgnoreCase);

                            if (!isProtectedDir && !string.IsNullOrEmpty(parentDir))
                            {
                                string candidateFolder = Path.Combine(parentDir, newTitle);
                                bool isCaseOnlyChange = string.Equals(fullOldFolder, candidateFolder, StringComparison.OrdinalIgnoreCase);

                                if (!isCaseOnlyChange && Directory.Exists(candidateFolder))
                                {
                                    return new { ok = false, message = $"本地磁盘同级目录下已存在《{newTitle}》文件夹" };
                                }

                                try
                                {
                                    if (isCaseOnlyChange && !string.Equals(fullOldFolder, candidateFolder, StringComparison.Ordinal))
                                    {
                                        string tempFolder = Path.Combine(parentDir, newTitle + "_renametmp_" + Guid.NewGuid().ToString("N")[..6]);
                                        Directory.Move(fullOldFolder, tempFolder);
                                        Directory.Move(tempFolder, candidateFolder);
                                    }
                                    else if (!string.Equals(fullOldFolder, candidateFolder, StringComparison.Ordinal))
                                    {
                                        Directory.Move(fullOldFolder, candidateFolder);
                                    }
                                    newFolder = candidateFolder;
                                }
                                catch (Exception ex)
                                {
                                    return new { ok = false, message = $"重命名本地物理文件夹失败：{ex.Message}" };
                                }
                            }
                        }

                        string newId = GenerateComicId(newTitle, newTitle);

                        // 如果自定义封面是漫画内部图片切片，跟随文件夹路径同步更新
                        if (!string.IsNullOrEmpty(comic.CustomCoverPath) && !string.IsNullOrEmpty(oldFolder) && !string.Equals(oldFolder, newFolder, StringComparison.OrdinalIgnoreCase))
                        {
                            string normalizedOld = Path.GetFullPath(oldFolder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                            if (comic.CustomCoverPath.StartsWith(normalizedOld, StringComparison.OrdinalIgnoreCase))
                            {
                                string rel = comic.CustomCoverPath.Substring(normalizedOld.Length);
                                comic.CustomCoverPath = Path.Combine(newFolder, rel);
                            }
                        }

                        // 更新文件夹与切片路径列表
                        comic.FolderPath = newFolder;
                        if (!string.IsNullOrEmpty(newFolder) && Directory.Exists(newFolder))
                        {
                            var refreshedPages = GetSortedImagesInDirectory(newFolder);
                            if (refreshedPages.Count > 0)
                            {
                                comic.PagePaths = refreshedPages;
                                comic.TotalPages = refreshedPages.Count;
                            }
                        }

                        if (!string.IsNullOrEmpty(comic.CustomCoverPath) && File.Exists(comic.CustomCoverPath))
                        {
                            comic.CoverPath = comic.CustomCoverPath;
                        }
                        else if (comic.PagePaths != null && comic.PagePaths.Count > 0)
                        {
                            comic.CoverPath = comic.PagePaths[0];
                        }

                        // 迁移历史记录字典并写入新标题与新 ID
                        PurgeComicHistory(comic);
                        comic.Id = newId;
                        comic.Title = newTitle;
                        comic.SubDirName = newTitle;
                        SyncComicToHistory(comic);
                        SaveDatabase();

                        return new
                        {
                            ok = true,
                            comic,
                            state = BuildInitPayload()
                        };
                    }
                }

                case "clearShelf":
                {
                    lock (dbLock)
                    {
                        db.Comics.Clear();
                        db.History.Clear();
                        db.LastRootFolder = "";
                        SaveDatabase();
                    }
                    return BuildInitPayload();
                }

                case "rescanMissing":
                {
                    VerifyAllComicsExistence(out int recoveredCount);
                    SaveDatabase();
                    return new
                    {
                        state = BuildInitPayload(),
                        recoveredCount
                    };
                }

                case "openDataFolder":
                {
                    try
                    {
                        if (File.Exists(dbFilePath))
                        {
                            Process.Start("explorer.exe", $"/select,\"{dbFilePath}\"");
                        }
                        else
                        {
                            Process.Start("explorer.exe", $"\"{appDataDir}\"");
                        }
                    }
                    catch { }
                    return new { ok = true };
                }

                case "openComicFolder":
                {
                    string id = payload.TryGetProperty("id", out var idEl) ? (idEl.GetString() ?? "") : "";
                    ComicRecord? comic;
                    lock (dbLock)
                    {
                        comic = db.Comics.FirstOrDefault(c => c.Id == id);
                    }
                    if (comic != null && !string.IsNullOrEmpty(comic.FolderPath) && Directory.Exists(comic.FolderPath))
                    {
                        try
                        {
                            Process.Start("explorer.exe", $"\"{comic.FolderPath}\"");
                        }
                        catch { }
                    }
                    return new { ok = true };
                }

                case "toggleFullscreen":
                {
                    if (FormBorderStyle == FormBorderStyle.None)
                    {
                        FormBorderStyle = FormBorderStyle.Sizable;
                        WindowState = FormWindowState.Normal;
                    }
                    else
                    {
                        FormBorderStyle = FormBorderStyle.None;
                        WindowState = FormWindowState.Maximized;
                    }
                    return new { fullscreen = FormBorderStyle == FormBorderStyle.None };
                }

                case "lanGetInfo":
                {
                    return new
                    {
                        deviceName = lanService.DeviceName,
                        ips = lanService.GetLocalLanIps(),
                        port = lanService.ListeningPort,
                        transferSavePath = GetCurrentTransferSaveDir(),
                        defaultTransferSavePath = defaultTransferDir
                    };
                }

                case "pickTransferFolder":
                {
                    string currentDir = GetCurrentTransferSaveDir();
                    using var dialog = new FolderBrowserDialog
                    {
                        Description = "选择局域网互传漫画的接收存储目录",
                        UseDescriptionForTitle = true,
                        ShowNewFolderButton = true,
                        InitialDirectory = Directory.Exists(currentDir) ? currentDir : appRootDir
                    };

                    if (dialog.ShowDialog(this) == DialogResult.OK && !string.IsNullOrEmpty(dialog.SelectedPath))
                    {
                        lock (dbLock)
                        {
                            db.TransferSavePath = dialog.SelectedPath;
                            SaveDatabase();
                        }
                        return new
                        {
                            changed = true,
                            transferSavePath = GetCurrentTransferSaveDir(),
                            state = BuildInitPayload()
                        };
                    }
                    return new { changed = false, transferSavePath = currentDir };
                }

                case "resetTransferFolder":
                {
                    lock (dbLock)
                    {
                        db.TransferSavePath = defaultTransferDir;
                        SaveDatabase();
                    }
                    return new
                    {
                        transferSavePath = GetCurrentTransferSaveDir(),
                        state = BuildInitPayload()
                    };
                }

                case "openTransferFolder":
                {
                    string dir = GetCurrentTransferSaveDir();
                    try
                    {
                        Process.Start("explorer.exe", $"\"{dir}\"");
                    }
                    catch { }
                    return new { ok = true };
                }

                case "lanDiscover":
                {
                    var peers = await lanService.DiscoverPeersAsync();
                    return new
                    {
                        self = new
                        {
                            deviceName = lanService.DeviceName,
                            ips = lanService.GetLocalLanIps(),
                            port = lanService.ListeningPort
                        },
                        peers
                    };
                }

                case "lanConnectManual":
                {
                    string host = payload.TryGetProperty("host", out var hEl) ? (hEl.GetString() ?? "") : "";
                    int port = payload.TryGetProperty("port", out var pEl) && pEl.ValueKind == JsonValueKind.Number
                        ? pEl.GetInt32()
                        : LanTransferService.DEFAULT_HTTP_PORT;
                    var peer = await lanService.PingPeerAsync(host, port, false);
                    return new { peer };
                }

                case "lanFetchPeerComics":
                {
                    string ip = payload.GetProperty("ip").GetString() ?? "";
                    int port = payload.TryGetProperty("port", out var pEl) && pEl.ValueKind == JsonValueKind.Number
                        ? pEl.GetInt32()
                        : LanTransferService.DEFAULT_HTTP_PORT;
                    bool notifyConnect = !payload.TryGetProperty("notifyConnect", out var ncEl) || ncEl.GetBoolean();
                    var remote = await lanService.FetchRemoteComicsAsync(ip, port, notifyConnect);
                    return remote;
                }

                case "lanPullComics":
                {
                    string ip = payload.GetProperty("ip").GetString() ?? "";
                    int port = payload.TryGetProperty("port", out var pEl) && pEl.ValueKind == JsonValueKind.Number
                        ? pEl.GetInt32()
                        : LanTransferService.DEFAULT_HTTP_PORT;
                    var ids = new List<string>();
                    if (payload.TryGetProperty("ids", out var arr) && arr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in arr.EnumerateArray())
                        {
                            string? s = item.GetString();
                            if (!string.IsNullOrEmpty(s)) ids.Add(s);
                        }
                    }

                    var pullResult = await Task.Run(() => lanService.PullComicsFromPeerAsync(ip, port, ids));
                    return new
                    {
                        count = pullResult.downloadedCount,
                        skippedCount = pullResult.skippedCount,
                        skippedTitles = pullResult.skippedTitles,
                        state = BuildInitPayload()
                    };
                }

                case "lanPushComics":
                {
                    string ip = payload.GetProperty("ip").GetString() ?? "";
                    int port = payload.TryGetProperty("port", out var pEl) && pEl.ValueKind == JsonValueKind.Number
                        ? pEl.GetInt32()
                        : LanTransferService.DEFAULT_HTTP_PORT;
                    var ids = new HashSet<string>();
                    if (payload.TryGetProperty("ids", out var arr) && arr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in arr.EnumerateArray())
                        {
                            string? s = item.GetString();
                            if (!string.IsNullOrEmpty(s)) ids.Add(s);
                        }
                    }

                    List<ComicRecord> toSend;
                    lock (dbLock)
                    {
                        toSend = db.Comics.Where(c => !c.IsMissing && ids.Contains(c.Id)).ToList();
                    }

                    var pushResult = await Task.Run(() => lanService.PushComicsToPeerAsync(ip, port, toSend));
                    return new
                    {
                        count = pushResult.sentCount,
                        skippedCount = pushResult.skippedCount,
                        skippedTitles = pushResult.skippedTitles
                    };
                }

                default:
                    return null;
            }
        }

        private object BuildInitPayload()
        {
            lock (dbLock)
            {
                // 书架初始化与列表刷新只下发轻量元数据（不含每部漫画数百条 PagePaths），IPC 体积减少 95%
                var lightComics = db.Comics.Select(c => new
                {
                    id = c.Id,
                    title = c.Title,
                    subDirName = c.SubDirName,
                    folderPath = c.FolderPath,
                    coverPath = c.CoverPath,
                    customCoverPath = c.CustomCoverPath,
                    totalPages = c.TotalPages > 0 ? c.TotalPages : (c.PagePaths?.Count ?? 0),
                    importTime = c.ImportTime,
                    readCount = c.ReadCount,
                    lastReadTime = c.LastReadTime,
                    lastReadPage = c.LastReadPage,
                    isMissing = c.IsMissing
                }).ToList();

                return new
                {
                    comics = lightComics,
                    sortMode = db.SortMode,
                    pageSize = db.PageSize,
                    readerWidth = db.ReaderWidth,
                    lastRootFolder = db.LastRootFolder,
                    dataFilePath = dbFilePath,
                    transferSavePath = GetCurrentTransferSaveDir(),
                    defaultTransferSavePath = defaultTransferDir
                };
            }
        }

        private async Task HandleDroppedPathsAsync(List<string> paths)
        {
            var dirs = new List<string>();
            var files = new List<string>();

            foreach (string p in paths)
            {
                if (Directory.Exists(p))
                    dirs.Add(p);
                else if (File.Exists(p) && IsImageFile(p))
                    files.Add(p);
            }

            if (dirs.Count > 0)
            {
                lock (dbLock)
                {
                    if (dirs.Count == 1)
                        db.LastRootFolder = dirs[0];
                    SaveDatabase();
                }
                await Task.Run(() => ScanFoldersAsync(dirs));
            }

            if (files.Count > 0)
            {
                await ImportImageFilesAsComicAsync(files);
                SendEventToUi("shelfUpdated", BuildInitPayload());
            }
        }

        private async Task ImportImageFilesAsComicAsync(IEnumerable<string> imageFiles)
        {
            await Task.Run(() =>
            {
                var validFiles = imageFiles.Where(f => File.Exists(f) && IsImageFile(f)).ToList();
                if (validFiles.Count == 0) return;

                validFiles.Sort((a, b) => NaturalStringComparer.Instance.Compare(Path.GetFileName(a), Path.GetFileName(b)));

                string firstFile = validFiles[0];
                string folderPath = Path.GetDirectoryName(firstFile) ?? "";
                string folderName = !string.IsNullOrEmpty(folderPath) ? new DirectoryInfo(folderPath).Name : "未命名漫画";
                string id = GenerateComicId(folderName, folderName);

                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                long folderModTime = now;
                try
                {
                    if (!string.IsNullOrEmpty(folderPath) && Directory.Exists(folderPath))
                    {
                        folderModTime = new DateTimeOffset(Directory.GetLastWriteTimeUtc(folderPath)).ToUnixTimeMilliseconds();
                    }
                }
                catch { }

                lock (dbLock)
                {
                    var existing = db.Comics.FirstOrDefault(c => c.Id == id || string.Equals(c.Title, folderName, StringComparison.OrdinalIgnoreCase));
                    var hist = GetComicHistory(id, folderName);

                    if (existing != null)
                    {
                        string customCover = ResolveCustomCoverPath(existing, hist);
                        existing.FolderPath = folderPath;
                        existing.CustomCoverPath = customCover;
                        existing.CoverPath = !string.IsNullOrEmpty(customCover) ? customCover : validFiles[0];
                        existing.PagePaths = validFiles;
                        existing.TotalPages = validFiles.Count;
                        existing.IsMissing = false;
                        if (hist != null)
                        {
                            existing.ReadCount = Math.Max(existing.ReadCount, hist.ReadCount);
                            existing.LastReadTime = Math.Max(existing.LastReadTime, hist.LastReadTime);
                            existing.LastReadPage = Math.Max(existing.LastReadPage, hist.LastReadPage);
                        }
                        SyncComicToHistory(existing);
                    }
                    else
                    {
                        string customCover = ResolveCustomCoverPath(null, hist);
                        var record = new ComicRecord
                        {
                            Id = id,
                            Title = folderName,
                            SubDirName = folderName,
                            FolderPath = folderPath,
                            CustomCoverPath = customCover,
                            CoverPath = !string.IsNullOrEmpty(customCover) ? customCover : validFiles[0],
                            PagePaths = validFiles,
                            TotalPages = validFiles.Count,
                            ImportTime = hist?.ImportTime > 0 ? hist.ImportTime : (folderModTime > 0 ? folderModTime : now),
                            ReadCount = hist?.ReadCount ?? 0,
                            LastReadTime = hist?.LastReadTime ?? 0,
                            LastReadPage = hist?.LastReadPage ?? 1,
                            IsMissing = false
                        };
                        db.Comics.Add(record);
                        SyncComicToHistory(record);
                    }
                    SaveDatabase();
                }
            });
        }

        private void ScanFoldersAsync(List<string> rootFolders)
        {
            if (isScanning) return;
            isScanning = true;

            try
            {
                SendEventToUi("scanProgress", new
                {
                    status = "正在扫描磁盘目录结构...",
                    percent = 2,
                    detail = "正在检索包含漫画切片的文件夹..."
                });

                // 收集所有含图片的漫画文件夹
                var comicDirs = new List<string>();
                var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (string root in rootFolders)
                {
                    if (Directory.Exists(root))
                    {
                        CollectComicDirectoriesRecursive(root, comicDirs, visited);
                    }
                }

                // 同时核验书架上已有漫画是否被移走或恢复
                VerifyAllComicsExistence(out _);

                if (comicDirs.Count == 0)
                {
                    SaveDatabase();
                    SendEventToUi("scanComplete", new
                    {
                        newlyCount = 0,
                        totalScanned = 0,
                        state = BuildInitPayload()
                    });
                    return;
                }

                comicDirs.Sort((a, b) => NaturalStringComparer.Instance.Compare(Path.GetFileName(a), Path.GetFileName(b)));

                int total = comicDirs.Count;
                int newlyAdded = 0;

                for (int i = 0; i < total; i++)
                {
                    string dirPath = comicDirs[i];
                    string dirName = new DirectoryInfo(dirPath).Name;
                    int pct = Math.Min(99, (int)Math.Round(((i + 1) * 100.0) / total));

                    SendEventToUi("scanProgress", new
                    {
                        status = $"正在导入漫画 ({i + 1}/{total})...",
                        percent = pct,
                        detail = $"当前解析: {dirName}"
                    });

                    var images = GetSortedImagesInDirectory(dirPath);
                    if (images.Count == 0) continue;

                    string id = GenerateComicId(dirName, dirName);
                    long folderModTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    try
                    {
                        folderModTime = new DateTimeOffset(Directory.GetLastWriteTimeUtc(dirPath)).ToUnixTimeMilliseconds();
                    }
                    catch { }

                    lock (dbLock)
                    {
                        var existing = db.Comics.FirstOrDefault(c =>
                            c.Id == id ||
                            string.Equals(c.FolderPath, dirPath, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(c.Title, dirName, StringComparison.OrdinalIgnoreCase));

                        var hist = GetComicHistory(id, dirName);

                        if (existing != null)
                        {
                            string customCover = ResolveCustomCoverPath(existing, hist);
                            existing.FolderPath = dirPath;
                            existing.CustomCoverPath = customCover;
                            existing.CoverPath = !string.IsNullOrEmpty(customCover) ? customCover : images[0];
                            existing.PagePaths = images;
                            existing.TotalPages = images.Count;
                            existing.IsMissing = false;
                            if (hist != null)
                            {
                                existing.ReadCount = Math.Max(existing.ReadCount, hist.ReadCount);
                                existing.LastReadTime = Math.Max(existing.LastReadTime, hist.LastReadTime);
                                existing.LastReadPage = Math.Max(existing.LastReadPage, hist.LastReadPage);
                            }
                            SyncComicToHistory(existing);
                        }
                        else
                        {
                            string customCover = ResolveCustomCoverPath(null, hist);
                            var record = new ComicRecord
                            {
                                Id = id,
                                Title = dirName,
                                SubDirName = dirName,
                                FolderPath = dirPath,
                                CustomCoverPath = customCover,
                                CoverPath = !string.IsNullOrEmpty(customCover) ? customCover : images[0],
                                PagePaths = images,
                                TotalPages = images.Count,
                                ImportTime = hist?.ImportTime > 0 ? hist.ImportTime : folderModTime,
                                ReadCount = hist?.ReadCount ?? 0,
                                LastReadTime = hist?.LastReadTime ?? 0,
                                LastReadPage = hist?.LastReadPage ?? 1,
                                IsMissing = false
                            };
                            db.Comics.Add(record);
                            SyncComicToHistory(record);
                            newlyAdded++;
                        }
                    }

                    // 每导入 5 部或最后一部时实时刷新一次书架界面并落盘
                    if ((i + 1) % 5 == 0 || i == total - 1)
                    {
                        SaveDatabase();
                        SendEventToUi("shelfUpdated", BuildInitPayload());
                    }
                }

                SaveDatabase();
                SendEventToUi("scanComplete", new
                {
                    newlyCount = newlyAdded,
                    totalScanned = total,
                    state = BuildInitPayload()
                });
            }
            catch (Exception ex)
            {
                SendEventToUi("scanError", new { message = ex.Message });
            }
            finally
            {
                isScanning = false;
            }
        }

        private static void CollectComicDirectoriesRecursive(string currentDir, List<string> result, HashSet<string> visited)
        {
            if (!visited.Add(currentDir)) return;

            try
            {
                bool hasDirectImages = Directory.EnumerateFiles(currentDir)
                    .Any(IsImageFile);

                if (hasDirectImages)
                {
                    result.Add(currentDir);
                }

                foreach (string subDir in Directory.EnumerateDirectories(currentDir))
                {
                    string name = Path.GetFileName(subDir);
                    if (name.StartsWith(".")) continue;
                    CollectComicDirectoriesRecursive(subDir, result, visited);
                }
            }
            catch
            {
                // Ignore permission-denied system folders
            }
        }

        private static List<string> GetSortedImagesInDirectory(string dirPath)
        {
            try
            {
                var files = Directory.EnumerateFiles(dirPath)
                    .Where(IsImageFile)
                    .ToList();

                files.Sort((a, b) => NaturalStringComparer.Instance.Compare(Path.GetFileName(a), Path.GetFileName(b)));
                return files;
            }
            catch
            {
                return new List<string>();
            }
        }

        private static bool IsImageFile(string path)
        {
            string ext = Path.GetExtension(path);
            return !string.IsNullOrEmpty(ext) && SupportedImageExtensions.Contains(ext);
        }

        /// <summary>
        /// 核验所有漫画源文件夹是否仍在原位置。如果被移走或删除，标记 IsMissing = true，但绝不删除其阅读记录。
        /// </summary>
        private bool VerifyAllComicsExistence(out int recoveredCount)
        {
            bool anyChanged = false;
            recoveredCount = 0;

            lock (dbLock)
            {
                foreach (var comic in db.Comics)
                {
                    bool folderOk = !string.IsNullOrEmpty(comic.FolderPath) && Directory.Exists(comic.FolderPath);
                    bool coverOk = !string.IsNullOrEmpty(comic.CoverPath) && File.Exists(comic.CoverPath);

                    bool nowMissing = !(folderOk || coverOk);
                    if (comic.IsMissing && !nowMissing)
                    {
                        recoveredCount++;
                    }
                    if (comic.IsMissing != nowMissing)
                    {
                        comic.IsMissing = nowMissing;
                        anyChanged = true;
                    }
                }
            }

            return anyChanged;
        }

        private static string GenerateComicId(string title, string subDirName)
        {
            string raw = (string.IsNullOrWhiteSpace(title) ? subDirName : title).Trim().ToLowerInvariant();
            int hash = 0;
            foreach (char c in raw)
            {
                hash = ((hash << 5) - hash) + c;
            }
            string safeName = Regex.Replace(raw, @"[^a-zA-Z0-9_\u4e00-\u9fa5]", "_");
            if (safeName.Length > 30) safeName = safeName.Substring(0, 30);
            long absHash = Math.Abs((long)hash);
            return $"comic_{safeName}_{absHash}";
        }

        private ComicHistoryRecord? GetComicHistory(string id, string title)
        {
            if (db.History.TryGetValue(id, out var h1)) return h1;
            string cleanTitle = (title ?? "").Trim().ToLowerInvariant();
            if (db.History.TryGetValue(cleanTitle, out var h2)) return h2;
            return null;
        }

        private static string ResolveCustomCoverPath(ComicRecord? existing, ComicHistoryRecord? hist)
        {
            if (existing != null && !string.IsNullOrEmpty(existing.CustomCoverPath) && File.Exists(existing.CustomCoverPath))
            {
                return existing.CustomCoverPath;
            }
            if (hist != null && !string.IsNullOrEmpty(hist.CustomCoverPath) && File.Exists(hist.CustomCoverPath))
            {
                return hist.CustomCoverPath;
            }
            return "";
        }

        private void CleanupCustomCoverFilesForId(string id)
        {
            if (string.IsNullOrEmpty(id) || !Directory.Exists(customCoversDir)) return;
            try
            {
                foreach (string f in Directory.EnumerateFiles(customCoversDir, $"{id}_*"))
                {
                    try
                    {
                        File.SetAttributes(f, FileAttributes.Normal);
                        File.Delete(f);
                    }
                    catch { }
                }
            }
            catch { }
        }

        private bool DeleteComicPhysicalFiles(ComicRecord c)
        {
            bool anyDeleted = false;
            try
            {
                CleanupCustomCoverFilesForId(c.Id);

                // 1. 删除该漫画记录的所有页面切片文件
                if (c.PagePaths != null)
                {
                    foreach (string pagePath in c.PagePaths)
                    {
                        if (!string.IsNullOrEmpty(pagePath) && File.Exists(pagePath))
                        {
                            try
                            {
                                File.SetAttributes(pagePath, FileAttributes.Normal);
                                File.Delete(pagePath);
                                anyDeleted = true;
                            }
                            catch { }
                        }
                    }
                }

                // 2. 如果漫画拥有专属文件夹，且该文件夹不是受保护的系统/根目录且不含子文件夹，则彻底删除整个漫画文件夹
                if (!string.IsNullOrEmpty(c.FolderPath) && Directory.Exists(c.FolderPath))
                {
                    string fullFolder = Path.GetFullPath(c.FolderPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    string? rootDrive = Path.GetPathRoot(fullFolder)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    string fullAppRoot = Path.GetFullPath(appRootDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    string fullAppData = Path.GetFullPath(appDataDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    string fullDefaultTrans = Path.GetFullPath(defaultTransferDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    string fullCurrentTrans = Path.GetFullPath(GetCurrentTransferSaveDir()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                    bool isProtectedDir =
                        string.Equals(fullFolder, rootDrive, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(fullFolder, fullAppRoot, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(fullFolder, fullAppData, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(fullFolder, fullDefaultTrans, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(fullFolder, fullCurrentTrans, StringComparison.OrdinalIgnoreCase);

                    if (!isProtectedDir)
                    {
                        bool hasSubDirs = Directory.EnumerateDirectories(fullFolder).Any();
                        if (!hasSubDirs)
                        {
                            foreach (string f in Directory.EnumerateFiles(fullFolder))
                            {
                                try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
                            }
                            Directory.Delete(fullFolder, true);
                            anyDeleted = true;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("删除漫画物理文件时出错: " + ex.Message);
            }
            return anyDeleted;
        }

        private void SyncComicToHistory(ComicRecord c)
        {
            var record = new ComicHistoryRecord
            {
                ReadCount = c.ReadCount,
                LastReadTime = c.LastReadTime,
                LastReadPage = c.LastReadPage,
                ImportTime = c.ImportTime,
                CustomCoverPath = c.CustomCoverPath ?? ""
            };
            db.History[c.Id] = record;
            string cleanTitle = (c.Title ?? "").Trim().ToLowerInvariant();
            if (!string.IsNullOrEmpty(cleanTitle))
            {
                db.History[cleanTitle] = record;
            }
        }

        private void PurgeComicHistory(ComicRecord c)
        {
            db.History.Remove(c.Id);
            if (!string.IsNullOrEmpty(c.Title))
            {
                db.History.Remove(c.Title);
                db.History.Remove(c.Title.Trim().ToLowerInvariant());
            }
            if (!string.IsNullOrEmpty(c.SubDirName))
            {
                db.History.Remove(c.SubDirName);
                db.History.Remove(c.SubDirName.Trim().ToLowerInvariant());
            }
        }

        private void LoadDatabase()
        {
            lock (dbLock)
            {
                try
                {
                    if (File.Exists(dbFilePath))
                    {
                        string json = File.ReadAllText(dbFilePath, Encoding.UTF8);
                        var loaded = JsonSerializer.Deserialize<ShelfDatabase>(json, JsonOptions);
                        if (loaded != null)
                        {
                            db = loaded;
                        }
                    }
                }
                catch
                {
                    db = new ShelfDatabase();
                }
            }
        }

        private void SaveDatabase()
        {
            isDatabaseDirty = true;
            if (System.Threading.Interlocked.CompareExchange(ref saveScheduledFlag, 1, 0) == 0)
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(500);
                    System.Threading.Interlocked.Exchange(ref saveScheduledFlag, 0);
                    if (isDatabaseDirty && !isClosing)
                    {
                        SaveDatabaseSync();
                    }
                });
            }
        }

        private void SaveDatabaseSync()
        {
            lock (dbLock)
            {
                try
                {
                    isDatabaseDirty = false;
                    string tempPath = dbFilePath + ".tmp";
                    string json = JsonSerializer.Serialize(db, JsonOptions);
                    File.WriteAllText(tempPath, json, Encoding.UTF8);
                    File.Move(tempPath, dbFilePath, true);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("保存数据库失败: " + ex.Message);
                }
            }
        }

        private void SendResponse(int reqId, object? data)
        {
            if (reqId <= 0) return;
            PostJsonToWebView(new
            {
                type = "response",
                reqId,
                data
            });
        }

        private void SendError(int reqId, string errorMessage)
        {
            if (reqId <= 0) return;
            PostJsonToWebView(new
            {
                type = "response",
                reqId,
                error = errorMessage
            });
        }

        private void SendEventToUi(string eventName, object? data)
        {
            PostJsonToWebView(new
            {
                type = "event",
                @event = eventName,
                data
            });
        }

        private void PostJsonToWebView(object payload)
        {
            if (IsDisposed || !isWebViewReady) return;
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => PostJsonToWebView(payload)));
                return;
            }
            try
            {
                string json = JsonSerializer.Serialize(payload, JsonOptions);
                webView.CoreWebView2?.PostWebMessageAsJson(json);
            }
            catch { }
        }
    }
}

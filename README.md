# YY漫画阅读器 桌面版 (ComicStream Desktop)

> 🎨 **极速、优雅、现代化的本地离线漫画阅读中枢**  
> 基于 **.NET 10** 与 **Microsoft Edge WebView2** 技术栈打造，支持极速瀑布流阅读、局域网多端漫画互传、免下载在线书架共享与跨端阅读进度同步。

---

## 🌟 核心特性

- 🚀 **极速无缝的连续瀑布流阅读**
  - 单卷与多卷漫画自适应无缝垂直滚动，支持按需队列流式图片预加载与视口防抖释放，杜绝百页长图卡顿或内存膨胀。
  - 支持键盘（`↑`/`↓`/`PageUp`/`PageDown`/`Home`/`End`）、鼠标滚轮与侧键平滑导航，随时沉浸式全屏观看。
- ⚡ **零黑屏瞬时启动**
  - 自研 GDI+ 双缓冲平滑过渡层架构，在窗口初始化的第 0 毫秒即可绘制与前端完全一致的暗黑主题骨架，彻底消除 WebView2 初始化过程中的黑屏等待。
- 📡 **局域网 Wi-Fi 智能双向互传**
  - 内嵌轻量高效的 HTTP / UDP 传输网络服务，无需公网服务器，自动发现同一 Wi-Fi 下的手机与电脑设备。
  - 智能去重与双向同步：传送前自动校验对端已拥有的漫画，自动跳过重复内容，支持断点重试与传输进度实时可视化。
- ☁️ **局域网免下载在线观看服务端**
  - 为安卓手机端提供流式切片与封面读取接口（`/api/comic-cover`、`/api/comic-page`），让手机在零占用本地物理存储的情况下，畅读电脑端数千部漫画。
  - **跨端阅读状态实时联动**：当手机端在在线书架中阅读漫画时，电脑端数据库自动同步递增对应漫画的累计阅读次数并更新时间戳，电脑桌面端 UI 实时响应刷新。
- 📚 **全能书架与文件安全管理**
  - **物理文件夹与标题安全重命名**：一键修改漫画名称，同时同步安全重命名磁盘上的真实文件夹，无缝迁移历史阅读记录、统计与自定义封面。
  - **可视化相册级自定义封面**：支持网格预览漫画内部任意页数，一键点选设为封面，或导入外部图片。
  - **智能检索与灵活排序**：支持跳字模糊搜索、拼音匹配，提供名称自然排序（Natural Sort，兼容 1/2/10）、导入时间、阅读量及页数等多维度排序。
  - **批处理彻底清理**：支持多选批量移除，可选彻底同步清理磁盘本地文件夹。

---

## 🛠️ 技术架构

- **运行环境**: Windows 10 / Windows 11 (x64)
- **后端语言**: C# / .NET 10.0 (Windows Forms 混合宿主)
- **核心组件**:
  - `Microsoft.Web.WebView2` (Chromium 内核渲染)
  - 内嵌原生异步 `TcpListener` 与 `UdpClient`（局域网发现与流式服务）
- **前端架构**: HTML5 / Modern CSS3 (Google Material Design 3 暗黑主题色彩规范) / 现代原生 JavaScript (ESNext)

---

## 📁 目录结构

```
ComicReaderDesktop/
├── Models/                     # 数据模型与实体定义
│   ├── ComicModels.cs          # 漫画记录、历史记录、局域网互传通信契约
│   └── ...
├── wwwroot/                    # 前端 Web 应用中枢
│   └── index.html              # 现代化单页阅读器前端（M3 设计风格、交互与虚拟滚动）
├── LanTransferService.cs       # 局域网传输网络核心（HTTP 服务端与 UDP 广播雷达）
├── MainForm.cs                 # WinForms 主窗口宿主、WebView2 通信桥接、数据库持久化
├── Program.cs                  # 程序入口与环境初始化
├── ComicReaderDesktop.csproj   # 项目工程定义与依赖包引用
└── README.md                   # 项目工程说明文档
```

---

## 🚀 源码编译与运行

### 前置要求
1. 安装 [.NET 10.0 SDK](https://dotnet.microsoft.com/) 或更新版本。
2. Windows 10 1809 及以上系统（自带或安装 [WebView2 运行时](https://developer.microsoft.com/microsoft-edge/webview2/)）。

### 1. 克隆代码仓库
```bash
git clone https://github.com/yamada17kawa-lab/yyComicReaderDesktop.git
cd yyComicReaderDesktop
```

### 2. 还原依赖并运行
```bash
dotnet restore
dotnet run
```

### 3. 发布独立单文件发行包 (Release)
```bash
dotnet publish -c Release -r win-x64 --self-contained false -o ./publish
```

---

## 📄 开源许可证

本项目基于 [MIT License](LICENSE) 开源发布。

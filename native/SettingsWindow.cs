using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace PivkeyOrganizer
{
    public sealed class SettingsWindow : Window
    {
        private readonly DesktopWindow host;
        private readonly CoreWebView2Environment environment;
        private readonly string webRoot;
        private readonly WebView2 webView;
        private readonly JavaScriptSerializer serializer = new JavaScriptSerializer();

        private bool initialized;
        private bool isExiting;

        public SettingsWindow(DesktopWindow host, CoreWebView2Environment environment, string webRoot)
        {
            this.host = host;
            this.environment = environment;
            this.webRoot = webRoot;
            Title = "片刻收纳设置";
            try
            {
                string iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", "pivkey-organizer.ico");
                if (File.Exists(iconPath)) Icon = new BitmapImage(new Uri(iconPath, UriKind.Absolute));
            }
            catch { }
            Width = 860;
            Height = 700;
            MinWidth = 680;
            MinHeight = 520;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            ShowInTaskbar = true;
            ShowActivated = true;
            Topmost = false;
            Background = new SolidColorBrush(Color.FromRgb(243, 243, 243));

            // 使用标准高效的 WebView2 控件（直接基于 Win32 HWND 硬件直显，彻底消除 Composition 渲染层性能开销）
            webView = new WebView2();
            webView.DefaultBackgroundColor = System.Drawing.Color.FromArgb(243, 243, 243);
            Content = webView;
            serializer.MaxJsonLength = int.MaxValue;
            Loaded += OnLoaded;
        }

        private async void OnLoaded(object sender, RoutedEventArgs args)
        {
            await InitWebViewAsync();
        }

        public async Task InitWebViewAsync()
        {
            if (initialized) return;
            initialized = true;
            try
            {
                await webView.EnsureCoreWebView2Async(environment);
                webView.CoreWebView2.Settings.AreDevToolsEnabled = false;
                webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
                webView.CoreWebView2.Settings.IsZoomControlEnabled = false;
                webView.CoreWebView2.Settings.AreHostObjectsAllowed = false;
                webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
                webView.CoreWebView2.NavigationStarting += delegate(object navSender, CoreWebView2NavigationStartingEventArgs navArgs)
                {
                    bool trusted = DesktopWindow.IsTrustedWebSource(navArgs.Uri);
                    if (!trusted)
                    {
                        DesktopWindow.Log("设置窗口拒绝非受信导航: " + navArgs.Uri);
                        navArgs.Cancel = true;
                    }
                };
                webView.CoreWebView2.NavigationCompleted += delegate(object navSender, CoreWebView2NavigationCompletedEventArgs navArgs)
                {
                    DesktopWindow.Log("设置窗口导航结束: IsSuccess=" + navArgs.IsSuccess + ", Status=" + navArgs.WebErrorStatus);
                };
                webView.CoreWebView2.NewWindowRequested += delegate(object windowSender, CoreWebView2NewWindowRequestedEventArgs windowArgs)
                {
                    windowArgs.Handled = true;
                };
                // 彻底淘汰 .local 顶级域名（.local 会强制触发 Windows RFC 6762 Multicast DNS 局域网组播探测，超时卡死 15 秒）；
                // 采用官方推荐的 appassets.example 虚拟主机，0ms 本地瞬发映射！
                webView.CoreWebView2.SetVirtualHostNameToFolderMapping("appassets.example", webRoot, CoreWebView2HostResourceAccessKind.Deny);
                webView.Source = new Uri("https://appassets.example/index.html?view=settings");
            }
            catch (Exception error)
            {
                MessageBox.Show("设置窗口启动失败：\n" + error.Message, "片刻收纳", MessageBoxButton.OK, MessageBoxImage.Error);
                isExiting = true;
                Close();
            }
        }

        public void PrepareExit()
        {
            isExiting = true;
        }

        public async void NotifyRefresh()
        {
            try
            {
                if (webView != null && webView.CoreWebView2 != null)
                {
                    await webView.CoreWebView2.ExecuteScriptAsync("window.dispatchEvent(new CustomEvent('pivkey-refresh-settings'))");
                }
            }
            catch { }
        }

        public async void FlushAndHide()
        {
            try
            {
                if (webView != null && webView.CoreWebView2 != null)
                {
                    await webView.CoreWebView2.ExecuteScriptAsync("window.dispatchEvent(new CustomEvent('pivkey-flush-settings'))");
                }
            }
            catch { }
            Hide();
            host.OnSettingsWindowHidden();
        }

        private async void OnWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs args)
        {
            string id = "";
            try
            {
                if (!DesktopWindow.IsTrustedWebSource(args.Source)) throw new UnauthorizedAccessException("已拒绝非受信页面的设置请求");
                Dictionary<string, object> request = serializer.DeserializeObject(args.TryGetWebMessageAsString()) as Dictionary<string, object>;
                if (request == null) throw new InvalidOperationException("无效的设置请求");
                id = Convert.ToString(request["id"]);
                string method = Convert.ToString(request["method"]);
                if (method == "closeSettings")
                {
                    PostResponse(id, true, null);
                    FlushAndHide();
                    return;
                }
                Dictionary<string, object> values = request.ContainsKey("args") ? request["args"] as Dictionary<string, object> : new Dictionary<string, object>();
                Dictionary<string, object> safeValues = values ?? new Dictionary<string, object>();
                object result = DesktopWindow.IsBackgroundMethod(method)
                    ? await Task.Run<object>(() => host.Execute(method, safeValues))
                    : host.Execute(method, safeValues);
                PostResponse(id, result, null);
            }
            catch (Exception error)
            {
                PostResponse(id, null, error.Message);
            }
        }

        // 标题栏 X / Alt+F4 关闭：拦截关闭并改为隐藏，保留 WebView2 运行实例实现后续秒开
        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (isExiting)
            {
                base.OnClosing(e);
                return;
            }
            e.Cancel = true;
            FlushAndHide();
        }

        private void PostResponse(string id, object result, string error)
        {
            try
            {
                if (webView.CoreWebView2 == null) return;
                Dictionary<string, object> response = new Dictionary<string, object>();
                response["id"] = id;
                if (error == null) response["result"] = result;
                else response["error"] = error;
                webView.CoreWebView2.PostWebMessageAsJson(serializer.Serialize(response));
            }
            catch { }
        }
    }
}

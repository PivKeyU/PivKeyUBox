// 约定：C# 5 语法（csc /langversion:5），中文注释，UTF-8 无 BOM。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;

namespace PivkeyOrganizer
{
    /// <summary>
    /// 片刻收纳更新管理器：支持通过 GitHub Releases 检查更新、异步下载安装包及调用 Inno Setup 升级。
    /// </summary>
    public static class UpdateManager
    {
        public static readonly string CurrentVersion = "0.1.1";
        public static readonly string DefaultRepo = "PivKeyU/PivKeyUBox";

        private static readonly object syncLock = new object();
        private static WebClient activeClient = null;
        private static bool isDownloading = false;
        private static bool isDownloaded = false;
        private static int downloadPercentage = 0;
        private static long bytesReceived = 0;
        private static long totalBytes = 0;
        private static string downloadedPath = null;
        private static string lastError = null;

        static UpdateManager()
        {
            try
            {
                // 确保支持现代 TLS 1.2 加密协议（对接 GitHub API 必须）
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            }
            catch { }
        }

        public static Dictionary<string, object> GetVersionInfo()
        {
            Dictionary<string, object> info = new Dictionary<string, object>();
            info["version"] = CurrentVersion;
            info["defaultRepo"] = DefaultRepo;
            return info;
        }

        /// <summary>
        /// 检查 GitHub Releases 最新版本
        /// </summary>
        public static Dictionary<string, object> CheckForUpdates(string repo)
        {
            string targetRepo = string.IsNullOrEmpty(repo) ? DefaultRepo : repo.Trim();
            string apiUrl = "https://api.github.com/repos/" + targetRepo + "/releases/latest";

            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(apiUrl);
            request.Method = "GET";
            request.UserAgent = "PivKeyUBox-Updater/" + CurrentVersion;
            request.Accept = "application/vnd.github.v3+json";
            request.Timeout = 15000;
            request.ReadWriteTimeout = 15000;

            string json = null;
            try
            {
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                using (Stream stream = response.GetResponseStream())
                using (StreamReader reader = new StreamReader(stream))
                {
                    json = reader.ReadToEnd();
                }
            }
            catch (WebException wex)
            {
                string msg = "检查更新失败";
                if (wex.Response is HttpWebResponse)
                {
                    HttpWebResponse httpRes = (HttpWebResponse)wex.Response;
                    if (httpRes.StatusCode == HttpStatusCode.NotFound)
                    {
                        msg = "仓库未找到或尚未发布任何 Release (" + targetRepo + ")";
                    }
                    else if ((int)httpRes.StatusCode == 403)
                    {
                        msg = "请求频繁，已触及 GitHub API 速率限制，请稍后重试";
                    }
                    else
                    {
                        msg = "GitHub 接口返回: " + httpRes.StatusCode.ToString();
                    }
                }
                else
                {
                    msg = "网络连接失败: " + wex.Message;
                }
                throw new Exception(msg);
            }

            JavaScriptSerializer serializer = new JavaScriptSerializer();
            serializer.MaxJsonLength = int.MaxValue;
            Dictionary<string, object> release = serializer.DeserializeObject(json) as Dictionary<string, object>;
            if (release == null) throw new Exception("无法解析 GitHub Releases 返回数据");

            string tagName = release.ContainsKey("tag_name") && release["tag_name"] != null ? release["tag_name"].ToString() : "";
            string releaseName = release.ContainsKey("name") && release["name"] != null ? release["name"].ToString() : tagName;
            string body = release.ContainsKey("body") && release["body"] != null ? release["body"].ToString() : "";
            string publishedAt = release.ContainsKey("published_at") && release["published_at"] != null ? release["published_at"].ToString() : "";
            string htmlUrl = release.ContainsKey("html_url") && release["html_url"] != null ? release["html_url"].ToString() : "";

            string cleanVersion = tagName.Trim();
            if (cleanVersion.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            {
                cleanVersion = cleanVersion.Substring(1).Trim();
            }

            bool hasUpdate = false;
            try
            {
                hasUpdate = CompareVersions(cleanVersion, CurrentVersion) > 0;
            }
            catch
            {
                hasUpdate = !string.Equals(cleanVersion, CurrentVersion, StringComparison.OrdinalIgnoreCase);
            }

            // 查找 assets 中的安装包文件 (.exe)
            string downloadUrl = "";
            string assetName = "";
            long assetSize = 0;

            if (release.ContainsKey("assets") && release["assets"] is object[])
            {
                object[] assets = (object[])release["assets"];
                foreach (object item in assets)
                {
                    Dictionary<string, object> asset = item as Dictionary<string, object>;
                    if (asset == null) continue;
                    string name = asset.ContainsKey("name") && asset["name"] != null ? asset["name"].ToString() : "";
                    if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        assetName = name;
                        downloadUrl = asset.ContainsKey("browser_download_url") && asset["browser_download_url"] != null ? asset["browser_download_url"].ToString() : "";
                        if (asset.ContainsKey("size") && asset["size"] != null)
                        {
                            long.TryParse(asset["size"].ToString(), out assetSize);
                        }
                        // 优先选择名称包含 Setup 的安装包
                        if (name.IndexOf("Setup", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            break;
                        }
                    }
                }
            }

            Dictionary<string, object> result = new Dictionary<string, object>();
            result["hasUpdate"] = hasUpdate;
            result["currentVersion"] = CurrentVersion;
            result["latestVersion"] = cleanVersion;
            result["tagName"] = tagName;
            result["releaseName"] = releaseName;
            result["releaseNotes"] = body;
            result["publishedAt"] = publishedAt;
            result["downloadUrl"] = downloadUrl;
            result["assetName"] = assetName;
            result["assetSize"] = assetSize;
            result["htmlUrl"] = htmlUrl;
            result["repo"] = targetRepo;

            return result;
        }

        /// <summary>
        /// 开始异步下载更新安装包
        /// </summary>
        public static bool StartDownload(string downloadUrl)
        {
            if (string.IsNullOrEmpty(downloadUrl)) throw new ArgumentException("下载链接不能为空");

            lock (syncLock)
            {
                if (isDownloading && activeClient != null)
                {
                    return true;
                }

                if (activeClient != null)
                {
                    try { activeClient.CancelAsync(); activeClient.Dispose(); } catch { }
                    activeClient = null;
                }

                string tempDir = Path.Combine(Path.GetTempPath(), "PivKeyUBox-Update");
                if (!Directory.Exists(tempDir)) Directory.CreateDirectory(tempDir);

                string targetFile = Path.Combine(tempDir, "PivKeyUBox-Setup.exe");
                if (File.Exists(targetFile))
                {
                    try { File.Delete(targetFile); } catch { }
                }

                isDownloading = true;
                isDownloaded = false;
                downloadPercentage = 0;
                bytesReceived = 0;
                totalBytes = 0;
                downloadedPath = targetFile;
                lastError = null;

                WebClient client = new WebClient();
                client.Headers.Add("User-Agent", "PivKeyUBox-Updater/" + CurrentVersion);

                client.DownloadProgressChanged += delegate(object sender, DownloadProgressChangedEventArgs e)
                {
                    lock (syncLock)
                    {
                        downloadPercentage = e.ProgressPercentage;
                        bytesReceived = e.BytesReceived;
                        totalBytes = e.TotalBytesToReceive;
                    }
                };

                client.DownloadFileCompleted += delegate(object sender, System.ComponentModel.AsyncCompletedEventArgs e)
                {
                    lock (syncLock)
                    {
                        isDownloading = false;
                        if (e.Error != null)
                        {
                            lastError = e.Error.Message;
                            isDownloaded = false;
                        }
                        else if (e.Cancelled)
                        {
                            lastError = "下载已取消";
                            isDownloaded = false;
                        }
                        else
                        {
                            isDownloaded = true;
                            downloadPercentage = 100;
                        }
                    }
                };

                activeClient = client;
                client.DownloadFileAsync(new Uri(downloadUrl), targetFile);
                return true;
            }
        }

        /// <summary>
        /// 获取当前下载进度状态
        /// </summary>
        public static Dictionary<string, object> GetDownloadProgress()
        {
            lock (syncLock)
            {
                Dictionary<string, object> status = new Dictionary<string, object>();
                status["isDownloading"] = isDownloading;
                status["isDownloaded"] = isDownloaded;
                status["percentage"] = downloadPercentage;
                status["bytesReceived"] = bytesReceived;
                status["totalBytes"] = totalBytes;
                status["filePath"] = downloadedPath;
                status["error"] = lastError;
                return status;
            }
        }

        /// <summary>
        /// 启动安装包并请求宿主退出
        /// </summary>
        public static bool LaunchInstaller(string customPath, bool silent)
        {
            string targetPath = string.IsNullOrEmpty(customPath) ? downloadedPath : customPath;
            if (string.IsNullOrEmpty(targetPath) || !File.Exists(targetPath))
            {
                throw new FileNotFoundException("未找到待安装的更新文件: " + targetPath);
            }

            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = targetPath;
            psi.UseShellExecute = true;
            if (silent)
            {
                psi.Arguments = "/SILENT /CLOSEAPPLICATIONS";
            }

            Process.Start(psi);
            return true;
        }

        public static int CompareVersions(string v1, string v2)
        {
            Version p1 = NormalizeVersion(v1);
            Version p2 = NormalizeVersion(v2);
            return p1.CompareTo(p2);
        }

        private static Version NormalizeVersion(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return new Version(0, 0, 0, 0);
            raw = raw.Trim();
            if (raw.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            {
                raw = raw.Substring(1).Trim();
            }
            int dashIndex = raw.IndexOf('-');
            if (dashIndex >= 0) raw = raw.Substring(0, dashIndex);
            string[] parts = raw.Split('.');
            int major = parts.Length > 0 ? ParseIntSafe(parts[0]) : 0;
            int minor = parts.Length > 1 ? ParseIntSafe(parts[1]) : 0;
            int build = parts.Length > 2 ? ParseIntSafe(parts[2]) : 0;
            int revision = parts.Length > 3 ? ParseIntSafe(parts[3]) : 0;
            return new Version(major, minor, build, revision);
        }

        private static int ParseIntSafe(string text)
        {
            int val = 0;
            int.TryParse(text, out val);
            return val;
        }
    }
}

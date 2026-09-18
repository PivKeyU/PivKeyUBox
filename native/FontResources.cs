using System;
using System.IO;
using System.Windows.Media;

namespace PivkeyOrganizer
{
    // 统一解析随应用发布的 Maple Mono；开发环境缺少 web 产物时回退到系统字体。
    internal static class FontResources
    {
        internal static readonly FontFamily WpfUiFont = CreateWpfUiFont();
        // DeskBox 视觉语言使用系统界面字体（Win11 Fluent）：标题/标签/菜单统一走它
        internal static readonly FontFamily SystemUiFont = new FontFamily("Microsoft YaHei UI, Segoe UI Variable Text, Segoe UI");
        private static readonly System.Drawing.Text.PrivateFontCollection TrayFontCollection = CreateTrayFontCollection();
        private static readonly System.Drawing.FontFamily TrayFontFamily = GetTrayFontFamily();

        private static string FindFont(string fileName)
        {
            try
            {
                string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
                string directPath = Path.Combine(baseDirectory, "fonts", fileName);
                if (File.Exists(directPath)) return directPath;
                string assetsDirectory = Path.Combine(baseDirectory, "web", "assets");
                if (Directory.Exists(assetsDirectory))
                {
                    string[] matches = Directory.GetFiles(assetsDirectory, Path.GetFileNameWithoutExtension(fileName) + "*.ttf");
                    if (matches.Length > 0) return matches[0];
                }
            }
            catch { }
            return null;
        }

        private static FontFamily CreateWpfUiFont()
        {
            return new FontFamily("Microsoft YaHei UI, Segoe UI Variable Text, Segoe UI");
        }

        private static System.Drawing.Text.PrivateFontCollection CreateTrayFontCollection()
        {
            System.Drawing.Text.PrivateFontCollection collection = new System.Drawing.Text.PrivateFontCollection();
            try
            {
                string regular = FindFont("MapleMono-NF-CN-Regular.ttf");
                string bold = FindFont("MapleMono-NF-CN-Bold.ttf");
                if (!String.IsNullOrWhiteSpace(regular)) collection.AddFontFile(regular);
                if (!String.IsNullOrWhiteSpace(bold) && !String.Equals(regular, bold, StringComparison.OrdinalIgnoreCase)) collection.AddFontFile(bold);
            }
            catch { }
            return collection;
        }

        private static System.Drawing.FontFamily GetTrayFontFamily()
        {
            try
            {
                if (TrayFontCollection.Families.Length > 0) return TrayFontCollection.Families[0];
            }
            catch { }
            return null;
        }

        internal static System.Drawing.Font CreateTrayFont(float size, System.Drawing.FontStyle style)
        {
            // 托盘菜单跟随 DeskBox 菜单字体语言：系统 UI 字体（等宽点阵字体会造成
            // 间距局促、发糊），与 WPF 菜单的 MenuUiFont 保持一致
            try { return new System.Drawing.Font("Microsoft YaHei UI", size, style, System.Drawing.GraphicsUnit.Point); }
            catch { }
            try { return new System.Drawing.Font("Segoe UI Variable Text", size, style); }
            catch { return new System.Drawing.Font("Segoe UI", size, style); }
        }
    }
}

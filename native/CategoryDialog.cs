// =====================================================================
// CategoryDialog.cs —— 「片刻收纳」新建分类对话框（WPF，标准窗口）
// 字段：分类名称 / 扩展名（逗号、空格分隔）/ 颜色（7 个预设圆点）/
//       接收文件夹 / 确定 / 取消；名称校验与前端 categoryValidation 一致
// 约定：C# 5 语法（csc /langversion:5），中文注释，UTF-8 无 BOM。
// =====================================================================
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace PivkeyOrganizer
{
    internal sealed class CategoryDialog : Window
    {
        // 预设颜色（与前端 CategoryModal 色板一致）
        private static readonly string[] PresetColors = new string[]
        {
            "#3478f6", "#34a853", "#ff9f0a", "#ff375f", "#af52de", "#18a999", "#8e8e93"
        };

        private readonly TextBox nameBox = new TextBox();
        private readonly TextBox extensionsBox = new TextBox();
        private readonly CheckBox acceptsFoldersBox = new CheckBox();
        private readonly Button okButton = new Button();
        private readonly List<Border> colorDots = new List<Border>();
        private readonly List<string> existingNames;
        private string selectedColor = PresetColors[0];

        // 确定后可用（取消时为 null）
        internal CategoryData Result { get; private set; }

        public CategoryDialog(List<string> existingNames)
        {
            this.existingNames = existingNames ?? new List<string>();
            Title = "新建分区";
            Width = 440;
            Height = 300;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            FontFamily = FontResources.WpfUiFont;

            nameBox.Height = 26;
            nameBox.FontSize = 12;
            nameBox.VerticalContentAlignment = VerticalAlignment.Center;
            extensionsBox.Height = 26;
            extensionsBox.FontSize = 12;
            extensionsBox.VerticalContentAlignment = VerticalAlignment.Center;

            StackPanel form = new StackPanel { Margin = new Thickness(20, 16, 20, 14) };
            form.Children.Add(LabeledRow("分类名称", nameBox));
            form.Children.Add(LabeledRow("扩展名（逗号或空格分隔，可选）", extensionsBox));

            // 颜色选择：7 个预设圆点（点击切换选中态）
            StackPanel colors = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            foreach (string color in PresetColors) colors.Children.Add(CreateColorDot(color));
            form.Children.Add(LabeledRow("颜色", colors));

            acceptsFoldersBox.Content = "接收文件夹（允许把整个文件夹收纳到本分区）";
            acceptsFoldersBox.FontSize = 12;
            acceptsFoldersBox.Margin = new Thickness(0, 2, 0, 14);
            form.Children.Add(acceptsFoldersBox);

            // 确定 / 取消（Enter / Esc 对应默认/取消按钮）
            okButton.Content = "确定";
            okButton.Width = 86;
            okButton.Height = 30;
            okButton.FontSize = 12;
            okButton.IsDefault = true;
            okButton.IsEnabled = false;   // 名称为空时禁用
            okButton.Click += delegate { Commit(); };
            Button cancelButton = new Button { Content = "取消", Width = 86, Height = 30, FontSize = 12, IsCancel = true, Margin = new Thickness(10, 0, 0, 0) };
            StackPanel buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(okButton);
            buttons.Children.Add(cancelButton);
            form.Children.Add(buttons);

            nameBox.TextChanged += delegate { okButton.IsEnabled = !String.IsNullOrWhiteSpace(nameBox.Text); };
            Content = form;
            Loaded += delegate { nameBox.Focus(); };
        }

        // 一行 = 左标签 + 右控件
        private static UIElement LabeledRow(string label, UIElement control)
        {
            Grid row = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            TextBlock text = new TextBlock
            {
                Text = label,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0),
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(55, 50, 42))
            };
            Grid.SetColumn(text, 0);
            Grid.SetColumn(control, 1);
            row.Children.Add(text);
            row.Children.Add(control);
            return row;
        }

        private Border CreateColorDot(string color)
        {
            Border dot = new Border
            {
                Width = 24,
                Height = 24,
                CornerRadius = new CornerRadius(12),
                Background = new SolidColorBrush(ParseColor(color)),
                BorderThickness = new Thickness(2),
                Cursor = Cursors.Hand,
                Tag = color,
                Margin = new Thickness(0, 0, 10, 0)
            };
            dot.MouseLeftButtonDown += delegate
            {
                selectedColor = color;
                foreach (Border other in colorDots) UpdateColorDotSelection(other);
            };
            colorDots.Add(dot);
            UpdateColorDotSelection(dot);
            return dot;
        }

        private void UpdateColorDotSelection(Border dot)
        {
            bool selected = String.Equals(Convert.ToString(dot.Tag), selectedColor, StringComparison.Ordinal);
            dot.BorderBrush = selected
                ? new SolidColorBrush(Color.FromRgb(55, 50, 42))
                : new SolidColorBrush(Color.FromArgb(26, 0, 0, 0));
            dot.BorderThickness = new Thickness(selected ? 2.5 : 1);
        }

        private void Commit()
        {
            try
            {
                // 名称校验复用 Manager 的规则（长度/非法字符/保留名/重名）
                string name = Manager.NormalizeCategoryName(nameBox.Text, existingNames);
                // 扩展名解析与前端 CategoryModal 一致：逗号/空格分隔，去点、小写、去重
                string[] rawExtensions = extensionsBox.Text.Split(
                    new char[] { ',', '，', ' ', '\t', '\n', '\r', ';' }, StringSplitOptions.RemoveEmptyEntries);
                List<string> extensions = new List<string>();
                HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (string raw in rawExtensions)
                {
                    string extension = raw.Trim().ToLowerInvariant();
                    if (extension.StartsWith(".")) extension = extension.Substring(1);
                    if (extension.Length == 0 || seen.Contains(extension)) continue;
                    seen.Add(extension);
                    extensions.Add(extension);
                }
                CategoryData data = new CategoryData();
                // id 生成与前端一致：custom-{Date.now().toString(36)}
                long millis = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
                data.Id = "custom-" + ToBase36(millis);
                data.Name = name;
                data.Icon = "folder";
                data.Color = selectedColor;
                data.Extensions = extensions;
                data.AcceptsFolders = acceptsFoldersBox.IsChecked == true;
                Result = data;
                DialogResult = true;
            }
            catch (Exception error)
            {
                MessageBox.Show(this, error.Message, "新建分区", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // JS Number.toString(36)：.NET Convert.ToString 不支持基数 36，手写实现
        private static string ToBase36(long value)
        {
            if (value <= 0) return "0";
            const string digits = "0123456789abcdefghijklmnopqrstuvwxyz";
            string result = "";
            while (value > 0)
            {
                result = digits[(int)(value % 36)] + result;
                value /= 36;
            }
            return result;
        }

        private static Color ParseColor(string value)
        {
            try { return (Color)ColorConverter.ConvertFromString(value); } catch { return Color.FromRgb(140, 115, 80); }
        }
    }
}

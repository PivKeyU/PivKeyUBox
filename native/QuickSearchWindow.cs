using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace PivkeyOrganizer
{
    internal sealed class QuickSearchEntry
    {
        public string Name;
        public string Path;
        public string Category;
        public string Kind;
        public bool Favorite;
        public bool Pinned;
        public int RecentRank;
    }

    // 轻量 WPF 快速搜索：不启用第二个 WebView2，只在用户按下快捷键时创建。
    internal sealed class QuickSearchWindow : Window
    {
        private readonly DesktopWindow host;
        private readonly TextBox queryBox;
        private readonly ListBox resultList;
        private readonly TextBlock summary;
        private List<QuickSearchEntry> entries = new List<QuickSearchEntry>();

        internal QuickSearchWindow(DesktopWindow owner)
        {
            host = owner;
            Title = "片刻收纳快速搜索";
            Width = 560;
            Height = 430;
            MinWidth = 420;
            MinHeight = 300;
            WindowStyle = WindowStyle.ToolWindow;
            ResizeMode = ResizeMode.CanResizeWithGrip;
            ShowInTaskbar = false;
            Topmost = true;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Background = new SolidColorBrush(Color.FromRgb(255, 253, 247));
            FontFamily = FontResources.WpfUiFont;

            StackPanel root = new StackPanel { Margin = new Thickness(18) };
            TextBlock title = new TextBlock
            {
                Text = "快速搜索桌面项目",
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(42, 38, 32)),
                Margin = new Thickness(0, 0, 0, 4)
            };
            root.Children.Add(title);
            TextBlock hint = new TextBlock
            {
                Text = "搜索名称、路径或分类；Enter 打开，Esc 关闭",
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(132, 122, 108)),
                Margin = new Thickness(0, 0, 0, 12)
            };
            root.Children.Add(hint);

            queryBox = new TextBox
            {
                Height = 36,
                FontSize = 14,
                Padding = new Thickness(10, 6, 10, 6),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromRgb(214, 201, 179)),
                Background = Brushes.White,
                Foreground = new SolidColorBrush(Color.FromRgb(42, 38, 32))
            };
            queryBox.ToolTip = "名称、路径、扩展名或分类";
            queryBox.TextChanged += delegate { RenderResults(); };
            queryBox.KeyDown += OnQueryKeyDown;
            root.Children.Add(queryBox);

            summary = new TextBlock
            {
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(132, 122, 108)),
                Margin = new Thickness(2, 9, 0, 5)
            };
            root.Children.Add(summary);

            resultList = new ListBox
            {
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromRgb(232, 224, 211)),
                Background = Brushes.White,
                FontSize = 12,
                Padding = new Thickness(2),
                HorizontalContentAlignment = HorizontalAlignment.Stretch
            };
            resultList.MouseDoubleClick += delegate { OpenSelected(); };
            resultList.KeyDown += OnResultKeyDown;
            root.Children.Add(resultList);
            Content = root;

            Loaded += delegate
            {
                PositionWindow();
                LoadEntries();
                queryBox.Focus();
            };
            Activated += delegate { if (IsVisible) LoadEntries(); };
        }

        private void PositionWindow()
        {
            Rect work = SystemParameters.WorkArea;
            Left = work.Left + Math.Max(0, (work.Width - Width) / 2);
            Top = work.Top + Math.Max(0, (work.Height - Height) / 3);
        }

        private void LoadEntries()
        {
            try { entries = host.GetQuickSearchEntries() ?? new List<QuickSearchEntry>(); }
            catch { entries = new List<QuickSearchEntry>(); }
            RenderResults();
        }

        private void RenderResults()
        {
            if (resultList == null) return;
            string query = (queryBox.Text ?? "").Trim();
            resultList.Items.Clear();
            int shown = 0;
            foreach (QuickSearchEntry entry in entries)
            {
                if (!Matches(entry, query)) continue;
                ListBoxItem row = new ListBoxItem
                {
                    Tag = entry,
                    Padding = new Thickness(9, 7, 9, 7),
                    HorizontalContentAlignment = HorizontalAlignment.Stretch
                };
                Grid grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                Border marker = new Border
                {
                    Width = 6,
                    Height = 6,
                    CornerRadius = new CornerRadius(3),
                    Background = new SolidColorBrush(entry.Pinned ? Color.FromRgb(113, 137, 107) : entry.Favorite ? Color.FromRgb(190, 145, 74) : Color.FromRgb(211, 201, 185)),
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, 6, 0, 0)
                };
                Grid.SetColumn(marker, 0);
                grid.Children.Add(marker);
                StackPanel text = new StackPanel();
                text.Children.Add(new TextBlock { Text = entry.Name, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(42, 38, 32)), TextTrimming = TextTrimming.CharacterEllipsis });
                text.Children.Add(new TextBlock { Text = entry.Category + "  ·  " + entry.Path, FontSize = 10, Foreground = new SolidColorBrush(Color.FromRgb(132, 122, 108)), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 3, 0, 0) });
                Grid.SetColumn(text, 1);
                grid.Children.Add(text);
                string badge = entry.Pinned ? "固定" : entry.Favorite ? "收藏" : "";
                if (badge.Length > 0)
                {
                    TextBlock tag = new TextBlock { Text = badge, FontSize = 10, Foreground = new SolidColorBrush(Color.FromRgb(113, 137, 107)), Margin = new Thickness(10, 2, 0, 0), VerticalAlignment = VerticalAlignment.Top };
                    Grid.SetColumn(tag, 2);
                    grid.Children.Add(tag);
                }
                row.Content = grid;
                resultList.Items.Add(row);
                shown++;
                if (shown >= 80) break;
            }
            summary.Text = shown == 0 ? "没有匹配项目" : "显示 " + shown + " 个项目（共 " + entries.Count + " 个）";
            if (resultList.Items.Count > 0) resultList.SelectedIndex = 0;
        }

        private static bool Matches(QuickSearchEntry entry, string query)
        {
            if (query.Length == 0) return true;
            return (entry.Name ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
                || (entry.Path ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
                || (entry.Category ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
                || (entry.Kind ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void OnQueryKeyDown(object sender, KeyEventArgs args)
        {
            if (args.Key == Key.Escape) { Close(); args.Handled = true; }
            else if (args.Key == Key.Enter) { OpenSelected(); args.Handled = true; }
        }

        private void OnResultKeyDown(object sender, KeyEventArgs args)
        {
            if (args.Key == Key.Escape) { Close(); args.Handled = true; }
            else if (args.Key == Key.Enter) { OpenSelected(); args.Handled = true; }
        }

        private void OpenSelected()
        {
            ListBoxItem item = resultList.SelectedItem as ListBoxItem;
            QuickSearchEntry entry = item == null ? null : item.Tag as QuickSearchEntry;
            if (entry == null) return;
            host.OpenQuickSearchPath(entry.Path);
            Close();
        }
    }
}

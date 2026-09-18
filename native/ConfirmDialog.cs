// =====================================================================
// ConfirmDialog.cs —— 「片刻收纳」现代化二次确认弹窗（WPF 原生卡片）
// ---------------------------------------------------------------------
// 对齐 DeskBox 原生设计规范与视觉语言：
//   * 12px 圆角现代卡片 + 柔和深度阴影（DropShadow）。
//   * 醒目的危险警示徽章与 Phosphor 矢量图标。
//   * 精致的「取消」与「删除便签/确定」操作按钮，支持 Hover/按下状态。
//   * 完备的键盘快捷键（Enter 确认，Esc 取消，方向键切换焦点）。
//   * 100% 跟随当前主题（浅色 / 深色 / 纸面材质与主强调色）。
//   * 约定：C# 5 语法（csc /langversion:5），中文注释，UTF-8 无 BOM。
// =====================================================================
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using ShapePath = System.Windows.Shapes.Path;

namespace PivkeyOrganizer
{
    internal sealed class ConfirmDialog : Window
    {
        private readonly bool darkTheme;
        private readonly Color themeAccent;
        private readonly bool isDanger;
        private Button cancelButton;
        private Button confirmButton;

        public static bool ShowDelete(Window owner, string title, string message, string detail, bool darkTheme, Color accentColor)
        {
            ConfirmDialog dialog = new ConfirmDialog(owner, title, message, detail, "删除便签", "取消", true, darkTheme, accentColor);
            bool? result = dialog.ShowDialog();
            return result == true;
        }

        public static bool ShowConfirm(Window owner, string title, string message, string detail, string confirmText, string cancelText, bool isDanger, bool darkTheme, Color accentColor)
        {
            ConfirmDialog dialog = new ConfirmDialog(owner, title, message, detail, confirmText, cancelText, isDanger, darkTheme, accentColor);
            bool? result = dialog.ShowDialog();
            return result == true;
        }

        public ConfirmDialog(Window owner, string title, string message, string detail, string confirmText, string cancelText, bool isDanger, bool darkTheme, Color accentColor)
        {
            this.darkTheme = darkTheme;
            this.themeAccent = accentColor.A > 0 ? accentColor : Color.FromRgb(52, 120, 246);
            this.isDanger = isDanger;

            Title = !String.IsNullOrWhiteSpace(title) ? title : "片刻便签";
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ResizeMode = ResizeMode.NoResize;
            FontFamily = FontResources.SystemUiFont;
            Width = 380;
            SizeToContent = SizeToContent.Height;

            // 文字清晰度三件套
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
            TextOptions.SetTextRenderingMode(this, TextRenderingMode.ClearType);
            RenderOptions.SetClearTypeHint(this, ClearTypeHint.Enabled);
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;

            if (owner != null && owner.IsVisible)
            {
                Owner = owner;
                WindowStartupLocation = WindowStartupLocation.CenterOwner;
            }
            else
            {
                WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }

            BuildUi(title, message, detail, confirmText, cancelText);

            PreviewKeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.Key == Key.Escape)
                {
                    DialogResult = false;
                    Close();
                    e.Handled = true;
                }
            };

            Loaded += delegate
            {
                if (confirmButton != null)
                {
                    confirmButton.Focus();
                }
            };
        }

        private void BuildUi(string title, string message, string detail, string confirmText, string cancelText)
        {
            // 外层边距留给 DropShadow 绘制，避免裁剪
            Grid rootGrid = new Grid { Margin = new Thickness(18) };

            Color cardBg = darkTheme ? Color.FromRgb(34, 33, 30) : Color.FromRgb(255, 255, 255);
            Color cardBorderColor = darkTheme ? Color.FromArgb(40, 255, 255, 255) : Color.FromArgb(24, 0, 0, 0);

            Border cardBorder = new Border
            {
                CornerRadius = new CornerRadius(12),
                Background = new SolidColorBrush(cardBg),
                BorderBrush = new SolidColorBrush(cardBorderColor),
                BorderThickness = new Thickness(1),
                Effect = new DropShadowEffect
                {
                    BlurRadius = 24,
                    ShadowDepth = 5,
                    Direction = 270,
                    Color = Colors.Black,
                    Opacity = darkTheme ? 0.45 : 0.16
                }
            };

            // 卡片任意非按钮区域均可拖拽
            cardBorder.MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e)
            {
                if (e.ButtonState == MouseButtonState.Pressed && !(e.OriginalSource is Button))
                {
                    try { DragMove(); } catch { }
                }
            };

            Grid contentGrid = new Grid();
            contentGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(34) }); // 行 0: 标题栏
            contentGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });     // 行 1: 主内容（图标 + 文字）
            contentGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });     // 行 2: 按钮区

            // ---------- 0. 顶部标题栏（拖拽区域 + 关闭叉） ----------
            Grid headerGrid = new Grid { Margin = new Thickness(16, 6, 8, 0) };
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            Color text2 = darkTheme ? Color.FromRgb(165, 165, 165) : Color.FromRgb(120, 120, 124);
            TextBlock headerTitle = new TextBlock
            {
                Text = !String.IsNullOrWhiteSpace(title) ? title : "片刻便签",
                FontSize = 12,
                FontWeight = FontWeights.Medium,
                Foreground = new SolidColorBrush(text2),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(headerTitle, 0);
            headerGrid.Children.Add(headerTitle);

            Button closeBtn = CreateCloseButton(darkTheme);
            closeBtn.Click += delegate
            {
                DialogResult = false;
                Close();
            };
            Grid.SetColumn(closeBtn, 1);
            headerGrid.Children.Add(closeBtn);

            Grid.SetRow(headerGrid, 0);
            contentGrid.Children.Add(headerGrid);

            // ---------- 1. 主内容区（左侧圆角徽章 + 右侧文案） ----------
            Grid bodyGrid = new Grid { Margin = new Thickness(16, 6, 16, 16) };
            bodyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            bodyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // 图标徽章（38×38，CornerRadius = 10）
            Border badge = new Border
            {
                Width = 38,
                Height = 38,
                CornerRadius = new CornerRadius(10),
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 2, 14, 0)
            };

            ShapePath badgeIcon;
            if (isDanger)
            {
                badge.Background = new SolidColorBrush(darkTheme ? Color.FromArgb(46, 255, 69, 58) : Color.FromArgb(24, 235, 60, 50));
                badgeIcon = CreatePhosphorIcon("trash", 20);
                badgeIcon.Fill = new SolidColorBrush(darkTheme ? Color.FromRgb(255, 99, 90) : Color.FromRgb(224, 49, 49));
            }
            else
            {
                badge.Background = new SolidColorBrush(Color.FromArgb(darkTheme ? (byte)45 : (byte)24, themeAccent.R, themeAccent.G, themeAccent.B));
                badgeIcon = CreatePhosphorIcon("question", 20);
                badgeIcon.Fill = new SolidColorBrush(themeAccent);
            }
            badge.Child = badgeIcon;
            Grid.SetColumn(badge, 0);
            bodyGrid.Children.Add(badge);

            // 文字区域
            StackPanel textPanel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            Color text1 = darkTheme ? Color.FromRgb(245, 245, 245) : Color.FromRgb(26, 26, 26);
            Color text3 = darkTheme ? Color.FromRgb(140, 140, 140) : Color.FromRgb(122, 122, 122);

            TextBlock messageBlock = new TextBlock
            {
                Text = !String.IsNullOrWhiteSpace(message) ? message : "确定要删除此便签吗？",
                FontSize = 13.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(text1),
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 20
            };
            textPanel.Children.Add(messageBlock);

            if (!String.IsNullOrWhiteSpace(detail))
            {
                TextBlock detailBlock = new TextBlock
                {
                    Text = detail,
                    FontSize = 11.5,
                    Foreground = new SolidColorBrush(text3),
                    Margin = new Thickness(0, 5, 0, 0),
                    TextWrapping = TextWrapping.Wrap,
                    LineHeight = 17
                };
                textPanel.Children.Add(detailBlock);
            }

            Grid.SetColumn(textPanel, 1);
            bodyGrid.Children.Add(textPanel);

            Grid.SetRow(bodyGrid, 1);
            contentGrid.Children.Add(bodyGrid);

            // ---------- 2. 底部按钮区（右对齐：取消 + 删除便签） ----------
            StackPanel actionsPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(16, 0, 16, 16)
            };

            cancelButton = CreateActionButton(!String.IsNullOrWhiteSpace(cancelText) ? cancelText : "取消", false, false, darkTheme, themeAccent);
            cancelButton.IsCancel = true;
            cancelButton.Margin = new Thickness(0, 0, 8, 0);
            cancelButton.Click += delegate
            {
                DialogResult = false;
                Close();
            };

            confirmButton = CreateActionButton(!String.IsNullOrWhiteSpace(confirmText) ? confirmText : (isDanger ? "删除便签" : "确定"), true, isDanger, darkTheme, themeAccent);
            confirmButton.IsDefault = true;
            confirmButton.Click += delegate
            {
                DialogResult = true;
                Close();
            };

            // 左右方向键在两个按钮之间切换
            cancelButton.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.Key == Key.Right) { confirmButton.Focus(); e.Handled = true; }
            };
            confirmButton.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.Key == Key.Left) { cancelButton.Focus(); e.Handled = true; }
            };

            actionsPanel.Children.Add(cancelButton);
            actionsPanel.Children.Add(confirmButton);

            Grid.SetRow(actionsPanel, 2);
            contentGrid.Children.Add(actionsPanel);

            cardBorder.Child = contentGrid;
            rootGrid.Children.Add(cardBorder);
            Content = rootGrid;
        }

        private static Button CreateActionButton(string text, bool isPrimary, bool isDanger, bool darkTheme, Color accentColor)
        {
            Button btn = new Button
            {
                Content = text,
                Height = 30,
                MinWidth = 74,
                Padding = new Thickness(14, 0, 14, 0),
                Cursor = Cursors.Hand,
                FontFamily = FontResources.SystemUiFont,
                FontSize = 12.5,
                FontWeight = FontWeights.Medium,
                FocusVisualStyle = null
            };

            Color normalBg;
            Color hoverBg;
            Color pressedBg;
            Color normalBorder;
            Color normalFg;

            if (isPrimary)
            {
                if (isDanger)
                {
                    normalBg = Color.FromRgb(224, 49, 49);     // #E03131
                    hoverBg = Color.FromRgb(201, 42, 42);      // #C92A2A
                    pressedBg = Color.FromRgb(178, 34, 34);
                    normalBorder = Color.FromRgb(224, 49, 49);
                    normalFg = Colors.White;
                }
                else
                {
                    normalBg = accentColor;
                    hoverBg = Color.FromRgb(
                        (byte)Math.Max(0, accentColor.R - 18),
                        (byte)Math.Max(0, accentColor.G - 18),
                        (byte)Math.Max(0, accentColor.B - 18));
                    pressedBg = Color.FromRgb(
                        (byte)Math.Max(0, accentColor.R - 36),
                        (byte)Math.Max(0, accentColor.G - 36),
                        (byte)Math.Max(0, accentColor.B - 36));
                    normalBorder = accentColor;
                    normalFg = Colors.White;
                }
            }
            else
            {
                // Cancel / 次级按钮
                if (darkTheme)
                {
                    normalBg = Color.FromArgb(24, 255, 255, 255);
                    hoverBg = Color.FromArgb(42, 255, 255, 255);
                    pressedBg = Color.FromArgb(60, 255, 255, 255);
                    normalBorder = Color.FromArgb(36, 255, 255, 255);
                    normalFg = Color.FromRgb(230, 230, 230);
                }
                else
                {
                    normalBg = Color.FromRgb(244, 244, 246);
                    hoverBg = Color.FromRgb(234, 234, 238);
                    pressedBg = Color.FromRgb(222, 222, 228);
                    normalBorder = Color.FromArgb(20, 0, 0, 0);
                    normalFg = Color.FromRgb(48, 48, 50);
                }
            }

            btn.Background = new SolidColorBrush(normalBg);
            btn.BorderBrush = new SolidColorBrush(normalBorder);
            btn.Foreground = new SolidColorBrush(normalFg);
            btn.BorderThickness = new Thickness(1);
            btn.Template = CreateButtonTemplate(new CornerRadius(6));

            btn.MouseEnter += delegate { btn.Background = new SolidColorBrush(hoverBg); };
            btn.MouseLeave += delegate { btn.Background = new SolidColorBrush(normalBg); };
            btn.PreviewMouseDown += delegate { btn.Background = new SolidColorBrush(pressedBg); };
            btn.PreviewMouseUp += delegate { btn.Background = new SolidColorBrush(hoverBg); };

            return btn;
        }

        private static Button CreateCloseButton(bool darkTheme)
        {
            Button btn = new Button
            {
                Width = 24,
                Height = 24,
                Padding = new Thickness(0),
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                Focusable = false,
                ToolTip = "关闭"
            };
            btn.Template = CreateButtonTemplate(new CornerRadius(4));

            ShapePath icon = CreatePhosphorIcon("close", 11);
            Color fgColor = darkTheme ? Color.FromRgb(160, 160, 160) : Color.FromRgb(130, 130, 134);
            icon.Fill = new SolidColorBrush(fgColor);
            btn.Content = icon;

            btn.MouseEnter += delegate
            {
                btn.Background = new SolidColorBrush(darkTheme ? Color.FromArgb(28, 255, 255, 255) : Color.FromArgb(18, 0, 0, 0));
                icon.Fill = new SolidColorBrush(darkTheme ? Colors.White : Color.FromRgb(40, 40, 40));
            };
            btn.MouseLeave += delegate
            {
                btn.Background = Brushes.Transparent;
                icon.Fill = new SolidColorBrush(fgColor);
            };
            return btn;
        }

        private static ControlTemplate CreateButtonTemplate(CornerRadius cornerRadius)
        {
            ControlTemplate template = new ControlTemplate(typeof(Button));
            FrameworkElementFactory border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
            border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
            border.SetValue(Border.CornerRadiusProperty, cornerRadius);

            FrameworkElementFactory presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(ContentPresenter.ContentProperty, new TemplateBindingExtension(ContentControl.ContentProperty));
            presenter.SetValue(ContentPresenter.MarginProperty, new TemplateBindingExtension(Control.PaddingProperty));
            presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);

            border.AppendChild(presenter);
            template.VisualTree = border;
            return template;
        }

        private static ShapePath CreatePhosphorIcon(string name, double size)
        {
            StreamGeometry geom = (StreamGeometry)Geometry.Parse(PhosphorGeometry(name));
            geom = geom.Clone();
            geom.FillRule = FillRule.Nonzero;
            return new ShapePath
            {
                Data = geom,
                Stretch = Stretch.Uniform,
                Width = size,
                Height = size,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        private static string PhosphorGeometry(string name)
        {
            if (name == "close") return "M205.66,194.34a12,12,0,0,1-17,17L128,150.97l-60.69,60.37a12,12,0,0,1-16.97-16.97L111.03,134,50.34,73.66a12,12,0,0,1,16.97-17L128,117.03l60.69-60.37a12,12,0,0,1,17,17L144.97,134Z";
            if (name == "trash") return "M216,48H176V40a24,24,0,0,0-24-24H104A24,24,0,0,0,80,40v8H40a8,8,0,0,0,0,16h8V208a16,16,0,0,0,16,16H192a16,16,0,0,0,16-16V64h8a8,8,0,0,0,0-16ZM96,40a8,8,0,0,1,8-8h48a8,8,0,0,1,8,8v8H96Zm96,168H64V64H192ZM112,104v64a8,8,0,0,1-16,0V104a8,8,0,0,1,16,0Zm48,0v64a8,8,0,0,1-16,0V104a8,8,0,0,1,16,0Z";
            if (name == "warning") return "M236.8,188,148.8,36a24,24,0,0,0-41.6,0L19.2,188A23.86,23.86,0,0,0,40,224H216a23.86,23.86,0,0,0,20.8-36ZM120,104a8,8,0,0,1,16,0v40a8,8,0,0,1-16,0Zm8,88a12,12,0,1,1,12-12A12,12,0,0,1,128,192Z";
            if (name == "question") return "M128,24A104,104,0,1,0,232,128,104.11,104.11,0,0,0,128,24Zm0,192a88,88,0,1,1,88-88A88.1,88.1,0,0,1,128,216Zm16-40a12,12,0,1,1-12-12A12,12,0,0,1,144,176Zm-16-40a8,8,0,0,1-8-8,24,24,0,0,1,24-24,8,8,0,0,0,0-16,24,24,0,0,0-24,24,8,8,0,0,1-16,0,40,40,0,1,1,54.65,37.16A8,8,0,0,1,128,136Z";
            return "M128,20l18.9,67.1L214,106l-67.1,18.9L128,192l-18.9-67.1L42,106l67.1-18.9Z";
        }
    }
}

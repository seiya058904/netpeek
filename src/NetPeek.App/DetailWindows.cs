using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace NetPeek
{
    internal static class DetailWindows
    {
        internal const string HelpText =
            "已建立 ≠ 正在传输。TCP 监听只表示等待连接；UDP 系统表只提供本地绑定，远端未知。\n\n" +
            "搜索应用名称或路径会显示该应用中符合筛选条件的端点；搜索完整 PID 定位进程，搜索 IP、端口或协议定位具体端点。按 Enter 可再次定位首个结果。状态和时间范围作用于同一条端点，历史状态是最后一次观察到的状态。\n\n" +
            "“当前快照”表示画面采样时仍出现的端点。“近 60 秒历史”表示当时已不再出现的端点，最后观察时间不是准确断开时间。默认显示全部时间范围。历史最多保留 12,000 条；身份无法核验的端点只显示当前快照。\n\n" +
            "暂停只冻结画面，搜索、筛选和复制都基于这张画面；后台继续采集，继续刷新后显示最新状态。顶部统计始终是全机当前快照的统计。\n\n" +
            "每轮采样完成后等待 1.5 秒，可能遗漏两次采样之间的短连接。代理 / VPN 可能只显示代理或隧道端点，不能还原真实网站。路径受限时显示未知；不推测域名、流量或风险。\n\n" +
            "复制按钮保持快捷复制：TCP 连接复制远端，监听与 UDP 复制本地。箭头菜单、端点右键或 Shift+F10 可复制本地、远端或含观察时间的完整记录。UDP 和监听的远端复制不可用。\n\n" +
            "Ctrl+F 搜索 · Ctrl+P 暂停 · F5 请求采样 · Esc 清空搜索 · Ctrl+C 复制端点（文本框保留普通复制）\n\n" +
            "NetPeek · 独立衍生自 Snitch · AGPL-3.0-or-later · 源码与构建说明随包提供";

        internal static Window Create(Window owner, string title, string text)
        {
            double width = Math.Min(600, owner.ActualWidth > 0 ? owner.ActualWidth : owner.Width);
            double height = Math.Min(460, owner.ActualHeight > 0 ? owner.ActualHeight : owner.Height);
            var dialog = new Window {
                Title = "NetPeek — " + title, Width = width, Height = height,
                MinWidth = Math.Min(300, width), MinHeight = Math.Min(180, height),
                WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
                Background = owner.Background, Foreground = owner.Foreground, FontFamily = owner.FontFamily,
                FontSize = 13, UseLayoutRounding = true
            };
            var panel = new Grid { Margin = new Thickness(16) };
            panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            panel.Children.Add(new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) });
            var content = new TextBox { Name = "DetailText", Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                BorderThickness = new Thickness(0), Background = Brushes.Transparent, Foreground = owner.Foreground, Padding = new Thickness(0, 0, 8, 0) };
            System.Windows.Automation.AutomationProperties.SetName(content, title + "完整内容");
            Grid.SetRow(content, 1); panel.Children.Add(content);
            var close = new Button { Content = "关闭", MinWidth = 80, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0), Style = (Style)owner.FindResource(typeof(Button)) };
            close.Click += delegate { dialog.Close(); }; Grid.SetRow(close, 2); panel.Children.Add(close);
            dialog.PreviewKeyDown += delegate(object sender, KeyEventArgs e) { if (e.Key == Key.Escape) { dialog.Close(); e.Handled = true; } };
            dialog.Content = panel;
            return dialog;
        }
    }
}

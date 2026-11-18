using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using TaskbarDrawer.UI;
using Xunit;

namespace TaskbarDrawer.Tests;

/// <summary>
/// 场景卡片里嵌着「详情 / 关闭窗口」按钮。点内层按钮时不能顺带触发整行的 toggle，
/// 否则用户点一次详情，场景就会收起 / 展开一次——这是 1.13.6 修掉的真实回归。
///
/// 用「点击源」判断而不是 e.Handled：WPF 的 ButtonBase 注册的是 handledEventsToo=true 的
/// 类处理器，内层标高 Handled 照样会触发外层 Click（实机验证过）。
/// xunit 默认跑在 MTA，而构造 WPF 控件要求 STA，所以每个用例都放到 STA 线程上执行。
/// </summary>
public class CardClickTests
{
    [Fact]
    public void 点内层按钮时不触发整行()
        => Assert.True(OnSta(() =>
        {
            var inner = new Button();
            var panel = new StackPanel { Children = { new TextBlock { Text = "场景" }, inner } };
            var card = new Button { Content = panel };
            return Ui.PressedInsideButton(inner, card);
        }));

    [Fact]
    public void 点内层按钮里的文本也算内层()
        => Assert.True(OnSta(() =>
        {
            var label = new TextBlock { Text = "详情" };
            var inner = new Button { Content = label };
            var card = new Button { Content = inner };
            return Ui.PressedInsideButton(label, card);
        }));

    [Fact]
    public void 点卡片空白处不算内层()
        => Assert.False(OnSta(() =>
        {
            var inner = new Button();
            var text = new TextBlock { Text = "场景标题" };
            var panel = new StackPanel { Children = { text, inner } };
            var card = new Button { Content = panel };
            return Ui.PressedInsideButton(text, card) || Ui.PressedInsideButton(panel, card);
        }));

    [Fact]
    public void 点击源就是卡片自己时不算内层()
        => Assert.False(OnSta(() =>
        {
            var card = new Button();
            return Ui.PressedInsideButton(card, card);
        }));

    private static T OnSta<T>(Func<T> action)
    {
        T result = default!;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { result = action(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error != null) throw error;
        return result;
    }
}

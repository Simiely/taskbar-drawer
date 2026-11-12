using System.Drawing;
using TaskbarDrawer.Core;
using TaskbarDrawer.Interop;
using Forms = System.Windows.Forms;

namespace TaskbarDrawer.UI;

/// <summary>
/// 托盘常驻：NotifyIcon + 深色右键菜单。
/// 菜单动作只抛事件，真正的状态留在 AppShell，避免两套状态。
/// 左键单击切换单窗口模式，双击打开主面板。
/// 图标随模式切换：普通模式是应用图标，单窗口模式在右下角加一枚「1」角标。
/// </summary>
public sealed class TrayService : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.Timer _clickTimer;
    private bool _singleClickPending;
    private readonly Forms.ContextMenuStrip _menu;
    private readonly Forms.ToolStripMenuItem _openPanel;
    private readonly Forms.ToolStripMenuItem _stashItem;
    private readonly Forms.ToolStripMenuItem _expandLatest;
    private readonly Forms.ToolStripMenuItem _expandGroupMenu;
    private readonly Forms.ToolStripMenuItem _expandAll;
    private readonly Forms.ToolStripMenuItem _singleWindow;
    private readonly Forms.ToolStripMenuItem _autoStart;
    private readonly Forms.ToolStripMenuItem _restartElevated;
    private readonly Forms.ToolStripMenuItem _version;
    private readonly Icon _normalIcon;
    private readonly Icon _singleWindowIcon;
    private readonly List<IntPtr> _iconHandles = new();
    private bool _disposed;

    public TrayService()
    {
        // 深色渲染器必须同时挂到 ToolStripManager 上，否则「按分组切换」这类子菜单仍是白底。
        var renderer = new DarkMenuRenderer();
        Forms.ToolStripManager.Renderer = renderer;
        _menu = new Forms.ContextMenuStrip
        {
            Renderer = renderer,
            Font = new Font("Microsoft YaHei UI", 9f)
        };

        // 单窗口模式放在最上面并占两行高度：它是最常用的模式开关，做成一条醒目的横条。
        _singleWindow = new TallMenuItem("单窗口模式")
        {
            CheckOnClick = true,
            ToolTipText = "开启后，切换到某个窗口时其余窗口会自动最小化；关闭后恢复系统默认行为"
        };
        _singleWindow.Click += (_, _) => SingleWindowRequested?.Invoke(_singleWindow.Checked);
        _menu.Items.Add(_singleWindow);

        _menu.Items.Add(new Forms.ToolStripSeparator());

        _openPanel = new Forms.ToolStripMenuItem("打开主面板");
        _openPanel.Click += (_, _) => OpenPanelRequested?.Invoke();
        _menu.Items.Add(_openPanel);

        _menu.Items.Add(new Forms.ToolStripSeparator());

        // 实际动作是把「此刻屏幕上所有未分组的窗口」收成一组，不是只收一个窗口。
        _stashItem = new Forms.ToolStripMenuItem("收起屏幕上的窗口（新一组）")
        {
            ToolTipText = "把此刻屏幕上的窗口收成新的一组；可以反复收纳，之前的组不会丢"
        };
        _stashItem.Click += (_, _) => StashRequested?.Invoke();
        _menu.Items.Add(_stashItem);

        _expandLatest = new Forms.ToolStripMenuItem("展开最近一组");
        _expandLatest.Click += (_, _) => ExpandLatestRequested?.Invoke();
        _menu.Items.Add(_expandLatest);

        _expandGroupMenu = new Forms.ToolStripMenuItem("按分组切换");
        _menu.Items.Add(_expandGroupMenu);

        _expandAll = new Forms.ToolStripMenuItem("全部展开");
        _expandAll.Click += (_, _) => ExpandAllRequested?.Invoke();
        _menu.Items.Add(_expandAll);

        _menu.Items.Add(new Forms.ToolStripSeparator());

        _autoStart = new Forms.ToolStripMenuItem("开机自启")
        {
            CheckOnClick = true,
            ToolTipText = "写入当前用户的 Run 项，默认关闭"
        };
        _autoStart.Click += (_, _) => AutoStartRequested?.Invoke(_autoStart.Checked);
        _menu.Items.Add(_autoStart);

        // 管理员程序的窗口（HiBit 之类）只有同级进程才能隐藏，普通权限实例收不进去。
        _restartElevated = new Forms.ToolStripMenuItem("以管理员身份重启")
        {
            ToolTipText = "以管理员身份运行的窗口（如 HiBit）需要同级权限才能收起 / 展开"
        };
        _restartElevated.Click += (_, _) => RestartElevatedRequested?.Invoke();
        _menu.Items.Add(_restartElevated);

        _menu.Items.Add(new Forms.ToolStripSeparator());

        // 版本信息放在退出上面：右键托盘即可确认当前跑的是哪次构建。
        _version = new Forms.ToolStripMenuItem("版本") { Enabled = false };
        _menu.Items.Add(_version);

        var exit = new Forms.ToolStripMenuItem("退出");
        exit.Click += (_, _) => ExitRequested?.Invoke();
        _menu.Items.Add(exit);

        // 托盘图标按系统小图标尺寸（16 / 20 / 24，随 DPI 变）取帧，避免 32 缩到 16 发糊。
        int iconSize = Forms.SystemInformation.SmallIconSize.Width;
        using (Bitmap normal = AppIcon.CreateBitmap(iconSize, singleWindowMode: false))
            _normalIcon = CreateIcon(normal);
        using (Bitmap single = AppIcon.CreateBitmap(iconSize, singleWindowMode: true))
            _singleWindowIcon = CreateIcon(single);

        _icon = new Forms.NotifyIcon
        {
            Icon = _normalIcon,
            Text = "任务栏收纳 · 单击切换单窗口模式 · 双击打开面板",
            Visible = true,
            ContextMenuStrip = _menu
        };
        // 单击要等系统双击时间过去才能确认「不是双击」，所以挂起一小会儿再执行。
        _clickTimer = new Forms.Timer { Interval = Forms.SystemInformation.DoubleClickTime };
        _clickTimer.Tick += (_, _) =>
        {
            _clickTimer.Stop();
            if (!_singleClickPending) return;
            _singleClickPending = false;
            ToggleSingleWindowRequested?.Invoke();
        };

        _icon.MouseClick += (_, e) =>
        {
            if (e.Button != Forms.MouseButtons.Left) return;
            // 单击：切换单窗口模式。先挂起，等过了双击时间没有第二击才执行。
            _singleClickPending = true;
            _clickTimer.Stop();
            _clickTimer.Start();
        };

        // 双击走的是 MouseDoubleClick：真双击时 MouseClick 只发第一下、
        // 第二下被换成 MouseDoubleClick，所以在这里撤掉挂起的单击并打开面板。
        _icon.MouseDoubleClick += (_, e) =>
        {
            if (e.Button != Forms.MouseButtons.Left) return;
            _clickTimer.Stop();
            _singleClickPending = false;
            OpenPanelRequested?.Invoke();
        };
    }

    public event Action OpenPanelRequested;

    /// <summary>托盘单击：切换单窗口模式（双击才是打开主面板）。</summary>
    public event Action ToggleSingleWindowRequested;
    public event Action StashRequested;
    public event Action ExpandLatestRequested;
    public event Action<int> GroupRequested;
    public event Action ExpandAllRequested;
    public event Action<bool> SingleWindowRequested;
    public event Action<bool> AutoStartRequested;
    public event Action ExitRequested;

    /// <summary>用户点了「以管理员身份重启」。</summary>
    public event Action RestartElevatedRequested;

    /// <summary>已经是管理员时隐藏「以管理员身份重启」：同权限下再提权没有意义。</summary>
    public void SetElevated(bool elevated)
    {
        if (_disposed) return;
        _restartElevated.Visible = !elevated;
    }

    /// <summary>在托盘菜单里显示「版本 1.0.0 · 构建时间」。</summary>
    public void SetVersion(string text)
    {
        if (_disposed) return;
        _version.Text = $"版本 {text}";
    }

    /// <summary>菜单项右侧显示当前快捷键，随用户改键实时更新。</summary>
    public void SetHotkeys(string stashGesture)
    {
        if (_disposed) return;
        _stashItem.ShortcutKeyDisplayString = stashGesture;
    }

    /// <summary>每次收纳 / 展开后刷新菜单：组列表与可用状态。</summary>
    public void SetGroups(IReadOnlyList<StashGroupSummary> groups, int total)
    {
        if (_disposed) return;
        bool has = groups.Count > 0;

        _expandLatest.Enabled = has;
        _expandAll.Enabled = has;
        _expandGroupMenu.Enabled = has;

        // 逐项移除并释放：定时 Prune 会反复刷新。
        // 菜单正开着时不能 Dispose（绘制路径还持有这些项），只摘掉引用交给 GC。
        bool menuOpen = _menu.Visible;
        foreach (Forms.ToolStripItem item in _expandGroupMenu.DropDownItems.Cast<Forms.ToolStripItem>().ToArray())
        {
            _expandGroupMenu.DropDownItems.Remove(item);
            if (!menuOpen) item.Dispose();
        }

        foreach (var group in groups)
        {
            var item = new Forms.ToolStripMenuItem($"第 {group.Id} 组 · {group.Count} 个窗口 · {group.State}");
            int id = group.Id;
            item.Click += (_, _) => GroupRequested?.Invoke(id);
            _expandGroupMenu.DropDownItems.Add(item);
        }
    }

    /// <summary>同步单窗口模式：勾选状态 + 托盘图标（普通 / 带「1」角标）。</summary>
    public void SetSingleWindowChecked(bool value)
    {
        if (_disposed) return;
        _singleWindow.Checked = value;
        _icon.Icon = value ? _singleWindowIcon : _normalIcon;
    }

    public void SetAutoStartChecked(bool value)
    {
        if (_disposed) return;
        _autoStart.Checked = value;
    }

    public void SetStatus(string text)
    {
        if (_disposed) return;
        string full = "任务栏收纳" + (string.IsNullOrEmpty(text) ? "" : " · " + text);
        _icon.Text = full.Length > 63 ? full[..63] : full;
    }

    public void ShowBalloon(string text)
    {
        if (_disposed) return;
        _icon.ShowBalloonTip(2500, "任务栏收纳", text, Forms.ToolTipIcon.Info);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _clickTimer.Stop();
        _clickTimer.Dispose();
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
        _normalIcon.Dispose();
        _singleWindowIcon.Dispose();
        foreach (IntPtr handle in _iconHandles)
            NativeMethods.DestroyIcon(handle);
        _iconHandles.Clear();
    }

    /// <summary>位图转成托盘用的 Icon；HICON 句柄由本类统一销毁。</summary>
    private Icon CreateIcon(Bitmap bitmap)
    {
        IntPtr handle = bitmap.GetHicon();
        _iconHandles.Add(handle);
        return Icon.FromHandle(handle);
    }
}

/// <summary>
/// 占两行高度的菜单项：用于「单窗口模式」这种模式开关，点起来更醒目。
/// 原生排版会把标题压在上半格，垂直居中由 <see cref="DarkMenuRenderer"/> 统一处理。
/// </summary>
internal sealed class TallMenuItem : Forms.ToolStripMenuItem
{
    public TallMenuItem(string text) : base(text)
    {
    }

    public override Size GetPreferredSize(Size constrainingSize)
    {
        Size size = base.GetPreferredSize(constrainingSize);
        return new Size(size.Width, size.Height * 2);
    }
}

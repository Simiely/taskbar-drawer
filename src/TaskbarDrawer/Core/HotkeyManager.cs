using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using TaskbarDrawer.Interop;

namespace TaskbarDrawer.Core;

public sealed class HotkeyDefinition
{
    public HotkeyDefinition(int id, string name, uint modifiers, uint virtualKey)
    {
        Id = id;
        Name = name;
        Modifiers = modifiers;
        VirtualKey = virtualKey;
    }

    public int Id { get; }
    public string Name { get; }
    public uint Modifiers { get; }
    public uint VirtualKey { get; }

    public string Gesture => HotkeyManager.Describe(Modifiers, VirtualKey);
}

public sealed class HotkeyRegistration
{
    public HotkeyRegistration(HotkeyDefinition definition, bool success, int win32Error)
    {
        Definition = definition;
        Success = success;
        Win32Error = win32Error;
    }

    public HotkeyDefinition Definition { get; }
    public bool Success { get; }
    public int Win32Error { get; }

    public string ErrorText
    {
        get
        {
            if (Success) return "";
            if (Win32Error == NativeMethods.ERROR_HOTKEY_ALREADY_REGISTERED)
                return "已被其他程序占用 (1409)";
            return Win32Error == 0
                ? "注册失败（Win32 未返回错误码）"
                : new Win32Exception(Win32Error).Message + $" ({Win32Error})";
        }
    }
}

public static class HotkeyDefaults
{
    /// <summary>Ctrl+Alt+Shift+D：收起 / 展开全部窗口。</summary>
    public static readonly HotkeyDefinition StashAll =
        new(1, "收起 / 展开当前窗口",
            NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_SHIFT,
            NativeMethods.VK_D);
}

/// <summary>
/// RegisterHotKey 封装 + WM_HOTKEY 分发 + 冲突错误码上报。
/// 冲突必须读 GetLastWin32Error()，不静默失败。
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    private readonly IntPtr _hwnd;
    private readonly HwndSource _source;
    private readonly Dictionary<int, HotkeyDefinition> _registered = new();
    private bool _disposed;

    public HotkeyManager(IntPtr hwnd, HwndSource source = null)
    {
        _hwnd = hwnd;
        _source = source;
        if (_source != null) _source.AddHook(WndProc);
    }

    public event Action<int> Pressed;

    public HotkeyRegistration Register(HotkeyDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        bool ok = NativeMethods.RegisterHotKey(
            _hwnd, definition.Id, definition.Modifiers | NativeMethods.MOD_NOREPEAT, definition.VirtualKey);
        int error = ok ? 0 : Marshal.GetLastWin32Error();
        if (ok) _registered[definition.Id] = definition;
        return new HotkeyRegistration(definition, ok, error);
    }

    public void Unregister(int id)
    {
        if (_registered.Remove(id)) NativeMethods.UnregisterHotKey(_hwnd, id);
    }

    /// <summary>
    /// 改键：先注销旧组合再注册新组合；新组合失败（例如被别的程序占用）时把旧组合注册回去，
    /// 保证用户不会因为一次改键失败就彻底失去快捷键。
    /// </summary>
    public HotkeyRegistration Change(HotkeyDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        HotkeyDefinition previous = _registered.TryGetValue(definition.Id, out var old) ? old : null;
        if (previous != null) Unregister(definition.Id);

        var registration = Register(definition);
        if (!registration.Success && previous != null) Register(previous);
        return registration;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (int id in _registered.Keys.ToArray())
            NativeMethods.UnregisterHotKey(_hwnd, id);
        _registered.Clear();
        if (_source != null) _source.RemoveHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == (int)NativeMethods.WM_HOTKEY)
        {
            int id = wParam.ToInt32();
            if (_registered.ContainsKey(id))
            {
                Pressed?.Invoke(id);
                handled = true;
            }
        }
        return IntPtr.Zero;
    }

    public static string Describe(uint modifiers, uint virtualKey)
    {
        var parts = new List<string>(4);
        if ((modifiers & NativeMethods.MOD_CONTROL) != 0) parts.Add("Ctrl");
        if ((modifiers & NativeMethods.MOD_ALT) != 0) parts.Add("Alt");
        if ((modifiers & NativeMethods.MOD_SHIFT) != 0) parts.Add("Shift");
        if ((modifiers & NativeMethods.MOD_WIN) != 0) parts.Add("Win");

        string key = virtualKey switch
        {
            0x08 => "Backspace",
            0x09 => "Tab",
            0x0D => "Enter",
            0x1B => "Esc",
            0x20 => "Space",
            0x21 => "PageUp",
            0x22 => "PageDown",
            0x23 => "End",
            0x24 => "Home",
            0x25 => "Left",
            0x26 => "Up",
            0x27 => "Right",
            0x28 => "Down",
            0x2D => "Insert",
            0x2E => "Delete",
            0xBA => ";",
            0xBB => "=",
            0xBC => ",",
            0xBD => "-",
            0xBE => ".",
            0xBF => "/",
            0xC0 => "`",
            0xDB => "[",
            0xDC => "\\",
            0xDD => "]",
            0xDE => "'",
            >= 0x70 and <= 0x87 => $"F{virtualKey - 0x6F}",
            >= 0x30 and <= 0x39 => ((char)virtualKey).ToString(),
            >= 0x41 and <= 0x5A => ((char)virtualKey).ToString(),
            _ => $"VK_0x{virtualKey:X2}"
        };
        parts.Add(key);
        return string.Join("+", parts);
    }
}

using TaskbarDrawer.Interop;
using Xunit;

namespace TaskbarDrawer.Tests;

/// <summary>
/// owner 链行走：窗口簇的「簇主」判定全靠它（收纳时同簇弹窗、关闭只关簇主、
/// 单窗口模式判断前台属于哪个场景）。
///
/// 这里锁死的原因是 <c>GetAncestor(GA_ROOTOWNER)</c> 在实机上对 owned 的 WS_POPUP
/// 直接返回窗口自身（GetParent 对非 WS_CHILD 返回 0，链是空的）——必须自己走 GW_OWNER。
/// </summary>
public class OwnerChainTests
{
    private static readonly IntPtr A = new(0x10);
    private static readonly IntPtr B = new(0x20);
    private static readonly IntPtr C = new(0x30);

    [Fact]
    public void 没有owner时返回窗口自己()
        => Assert.Equal(A, NativeMethods.WalkOwners(A, _ => IntPtr.Zero));

    [Fact]
    public void 沿owner链走到簇主()
    {
        // A 由 B 拥有，B 由 C 拥有：簇主是 C。
        var owner = new Dictionary<IntPtr, IntPtr> { [A] = B, [B] = C, [C] = IntPtr.Zero };
        Assert.Equal(C, NativeMethods.WalkOwners(A, h => owner[h]));
    }

    [Fact]
    public void 自环不会死循环()
    {
        // owner 指回自己（异常数据）：立刻停在当前窗口。
        Assert.Equal(A, NativeMethods.WalkOwners(A, _ => A));
    }

    [Fact]
    public void 环形链受深度上限保护()
    {
        // 理论上 Windows 不允许成环，但纯规则要能兜住，不能把 UI 线程挂死。
        // A→B→C→A，maxDepth=3 走三步后停在 A；重点是「有限步返回」，不是停在哪个节点。
        var owner = new Dictionary<IntPtr, IntPtr> { [A] = B, [B] = C, [C] = A };
        Assert.Contains(NativeMethods.WalkOwners(A, h => owner[h], maxDepth: 3), new[] { A, B, C });
    }
}

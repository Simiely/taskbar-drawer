using System.Reflection;
using Xunit;

namespace TaskbarDrawer.Tests;

/// <summary>
/// 界面上显示的版本号必须等于程序集版本号。
///
/// 这条测试是因为一次真实事故加的：`csproj` 里 <c>InformationalVersion</c> 曾是手写常量
/// （"1.9.0 · 时间"），而 <c>Version</c> 一路升到 1.13，托盘 / 启动提示 / 日志头却一直显示 1.9，
/// 用户打开程序看到的版本是错的。现在它由 <c>$(Version)</c> 派生，这条测试负责别让它再漂。
/// </summary>
public class VersionTests
{
    [Fact]
    public void 界面显示的版本与程序集版本一致()
    {
        Assembly assembly = typeof(AppShell).Assembly;

        string informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
        // 与 AppShell.ShortVersion 同一套取法：截到第一个空格 / '+' 之前。
        string shown = informational.Split(' ', '+')[0];

        var version = assembly.GetName().Version;
        Assert.NotNull(version);
        string expected = $"{version!.Major}.{version.Minor}.{version.Build}";

        Assert.Equal(expected, shown);
    }

    [Fact]
    public void 版本串保持短小_带构建时间但不带长Sha()
    {
        // 托盘那一行要放得下：保持「1.13.3 · 11-18 13:59」这种形状。
        // 曾经是「1.13.2 · 2026-11-18 13:54+40 位 sha」，用户反馈太长。
        Assembly assembly = typeof(AppShell).Assembly;
        string informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";

        Assert.Contains(" · ", informational);
        Assert.DoesNotContain("+", informational);
        Assert.True(informational.Length <= 24, $"版本串太长：{informational}");
    }
}

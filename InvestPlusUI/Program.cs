using System;
using System.Windows.Forms;

namespace InvestPlusUI;

/// <summary>
/// 应用程序入口点。
///
/// InvestPlus 是一款可转债行情监测工具，采用纯 C# / WinForms .NET 8 架构。
/// 数据优先来自 AKTools API，并在异常时自动降级到东方财富和集思录直连源。
/// </summary>
internal static class Program
{
    /// <summary>
    /// 主入口方法（STA 线程：WinForms 要求 UI 在单线程公寓模式下运行）。
    /// </summary>
    [STAThread]
    static void Main()
    {
        // 启用 .NET 6+ 的高 DPI 感知和现代视觉样式
        ApplicationConfiguration.Initialize();
        // 启动主窗体，程序在此处阻塞直到窗体关闭
        Application.Run(new MainForm());
    }
}

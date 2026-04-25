using System.Diagnostics;
using System.IO;

namespace InvestPlusUI.Services;

/// <summary>
/// 管理 Python FastAPI 后端进程的生命周期。
///
/// 职责：
///   1. 自动定位 api.py（在可执行文件目录或其父级目录中查找）
///   2. 自动定位 Python 可执行文件（优先使用项目虚拟环境 .venv）
///   3. 启动 / 停止后端进程
///   4. 将后端标准输出和标准错误异步转发给调用方
/// </summary>
public sealed class BackendService : IDisposable
{
    private Process? _process;
    private bool _ownsProcess;   // 仅当本服务启动了进程时才负责停止它
    private bool _disposed;

    /// <summary>后端进程是否正在运行。</summary>
    public bool IsRunning => _process != null && !_process.HasExited;

    /// <summary>接收后端输出/错误日志的事件。</summary>
    public event Action<string>? OutputReceived;

    // ── 静态辅助 ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 在可执行文件目录及其各级父目录中查找 api.py，最多向上搜索 4 层。
    /// </summary>
    public static string? FindApiScript()
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 5; i++)
        {
            if (string.IsNullOrEmpty(dir)) break;
            var candidate = Path.Combine(dir, "api.py");
            if (File.Exists(candidate))
                return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    /// <summary>
    /// 确定 Python 可执行文件路径：
    ///   1. 优先使用 api.py 同级目录下的 .venv\Scripts\python.exe（Windows 虚拟环境）
    ///   2. 回退到系统 PATH 中的 python
    /// </summary>
    public static string FindPython(string? apiScriptPath)
    {
        if (apiScriptPath != null)
        {
            var root = Path.GetDirectoryName(apiScriptPath);
            if (root != null)
            {
                var venvPy = Path.Combine(root, ".venv", "Scripts", "python.exe");
                if (File.Exists(venvPy))
                    return venvPy;
            }
        }
        return "python";
    }

    // ── 公共操作 ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 启动 Python FastAPI 后端。
    /// </summary>
    /// <param name="pythonExe">Python 可执行文件路径（或 "python"）。</param>
    /// <param name="apiScriptPath">api.py 的绝对路径。</param>
    /// <exception cref="InvalidOperationException">后端已在运行时抛出。</exception>
    public void Start(string pythonExe, string apiScriptPath)
    {
        if (IsRunning)
            throw new InvalidOperationException("后端进程已在运行。");

        var workDir = Path.GetDirectoryName(apiScriptPath)
                      ?? AppContext.BaseDirectory;

        var psi = new ProcessStartInfo
        {
            FileName = pythonExe,
            Arguments = $"\"{apiScriptPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workDir,
        };

        _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _process.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null) OutputReceived?.Invoke(e.Data);
        };
        _process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null) OutputReceived?.Invoke(e.Data);
        };
        _process.Exited += (_, _) => OutputReceived?.Invoke("[后端进程已退出]");

        _process.Start();
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
        _ownsProcess = true;
    }

    /// <summary>
    /// 停止后端进程（若由本服务启动）。
    /// </summary>
    public void Stop()
    {
        if (_process == null || !_ownsProcess) return;
        try
        {
            if (!_process.HasExited)
                _process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"停止后端进程失败: {ex.Message}");
        }
        finally
        {
            _process.Dispose();
            _process = null;
            _ownsProcess = false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}

using UnityEngine;
using System.Diagnostics;
using System.IO;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Threading;

// 明确指定使用Unity的Debug，避免冲突
using Debug = UnityEngine.Debug;

public class ChessEngineManager : MonoBehaviour
{
    // ========== 公共变量，在Inspector中设置 ==========
    [Header("引擎设置")]
    [Tooltip("引擎可执行文件路径")]
    public string EnginePath = @"C:\Path\To\Your\lynx-cli.exe";

    [Header("调试与测试选项")]
    [Tooltip("是否显示详细的通信日志")]
    public bool EnableDebugLogs = true;

    [Tooltip("是否在启动时自动运行测试序列")]
    public bool RunAutoTestOnStart = true;

    [Tooltip("测试序列每一步的等待时间（秒）")]
    public float TestStepDelay = 1.0f;

    // ========== 私有变量 ==========
    private Process _engineProcess;
    private Thread _readThread;
    private bool _isRunning = false;
    private bool _isShuttingDown = false;
    private ConcurrentQueue<string> _outputQueue = new ConcurrentQueue<string>();
    private ConcurrentQueue<Action> _mainThreadQueue = new ConcurrentQueue<Action>();
    private AutoResetEvent _outputReceived = new AutoResetEvent(false);
    private bool _testInProgress = false;
    private bool _testSuccessful = false;
    private string _testResultMessage = "";
    private bool _uciOkReceived = false;
    private bool _readyOkReceived = false;

    // ========== 事件回调 ==========
    public event Action<string> OnEngineMessageReceived;
    public event Action<string> OnBestMoveReceived;
    public event Action<string> OnInfoReceived;
    public event Action<bool> OnEngineReadyChanged;
    public event Action<string> OnEngineError;
    public event Action<bool, string> OnCommunicationTestComplete;

    // ========== 引擎状态 ==========
    public bool IsEngineReady { get; private set; }
    public bool IsInitialized { get; private set; }

    void Start()
    {
        // 初始化引擎
        InitializeEngine();
    }

    void Update()
    {
        // 在主线程中处理队列中的任务
        ProcessMainThreadQueue();
    }

    void OnDestroy()
    {
        ShutdownEngine();
    }

    void OnApplicationQuit()
    {
        ShutdownEngine();
    }

    // ========== 核心功能：引擎初始化 ==========

    private void InitializeEngine()
    {
        if (IsInitialized) return;

        string finalPath = GetEnginePath();

        if (!File.Exists(finalPath))
        {
            string errorMsg = $"引擎文件不存在: {finalPath}";
            Debug.LogError(errorMsg);
            OnEngineError?.Invoke(errorMsg);
            return;
        }

        // 添加的日志开始
        if (EnableDebugLogs)
        {
            Debug.Log("========================================");
            Debug.Log("开始初始化引擎");
            Debug.Log($"引擎路径: {finalPath}");
            Debug.Log($"文件存在: {File.Exists(finalPath)}");
        }

        try
        {
            // 创建进程启动信息
            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = finalPath,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(finalPath)
            };

            // 创建进程
            _engineProcess = new Process { StartInfo = startInfo };

            // 设置输出数据接收事件
            _engineProcess.OutputDataReceived += (sender, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    _outputQueue.Enqueue(e.Data);
                    _outputReceived.Set(); // 通知有新数据
                }
            };

            // 设置错误输出处理
            _engineProcess.ErrorDataReceived += (sender, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    Debug.LogError($"引擎错误: {e.Data}");
                    OnEngineError?.Invoke(e.Data);
                }
            };

            // 设置进程退出事件
            _engineProcess.Exited += (sender, e) =>
            {
                _isRunning = false;
                IsEngineReady = false;
                Debug.LogWarning("引擎进程意外退出");
                OnEngineError?.Invoke("引擎进程意外退出");
            };
            _engineProcess.EnableRaisingEvents = true;

            // 启动引擎
            _engineProcess.Start();

            // 开始异步读取输出
            _engineProcess.BeginOutputReadLine();
            _engineProcess.BeginErrorReadLine();

            _isRunning = true;
            IsInitialized = true;

            // 启动读取线程
            _readThread = new Thread(ReadEngineOutput);
            _readThread.IsBackground = true;
            _readThread.Start();

            if (EnableDebugLogs)
            {
                Debug.Log($"引擎进程已启动！");
                Debug.Log($"进程ID: {_engineProcess.Id}");
                Debug.Log($"启动时间: {DateTime.Now:HH:mm:ss}");
                Debug.Log($"引擎已启动: {Path.GetFileName(finalPath)} (PID: {_engineProcess.Id})");
            }

            // 初始化UCI协议
            StartUCIProtocol();
        }
        catch (Exception e)
        {
            string errorMsg = $"启动引擎失败: {e.Message}";
            Debug.LogError(errorMsg);
            OnEngineError?.Invoke(errorMsg);
        }
    }

    private string GetEnginePath()
    {
        // 方法1: 使用直接指定的路径
        if (!string.IsNullOrEmpty(EnginePath) && File.Exists(EnginePath))
            return EnginePath;

        // 方法2: 从StreamingAssets读取
        string streamingPath = Path.Combine(Application.streamingAssetsPath, "Engines", "lynx-cli.exe");
        if (File.Exists(streamingPath))
            return streamingPath;

        return EnginePath; // 返回用户指定的路径，即使不存在
    }

    // ========== UCI协议初始化 ==========

    private void StartUCIProtocol()
    {
        if (EnableDebugLogs)
        {
            Debug.Log("<color=blue>启动UCI协议...</color>");
        }

        // 发送UCI初始化命令
        SendCommand("uci");

        // 等待引擎就绪
        StartCoroutine(WaitForEngineReady());
    }

    private IEnumerator WaitForEngineReady()
    {
        if (EnableDebugLogs)
        {
            Debug.Log("<color=blue>等待引擎准备就绪...</color>");
        }

        // 等待收到uciok
        float uciTimeout = 10.0f;
        float uciElapsed = 0f;
        while (!_uciOkReceived && uciElapsed < uciTimeout)
        {
            yield return new WaitForSeconds(0.1f);
            uciElapsed += 0.1f;
        }

        if (!_uciOkReceived)
        {
            Debug.LogError("<color=red>等待uciok超时！</color>");
            yield break;
        }

        // 等待1秒让引擎稳定
        yield return new WaitForSeconds(1.0f);

        // 发送isready命令
        SendCommand("isready");

        // 等待收到readyok
        float readyTimeout = 5.0f;
        float readyElapsed = 0f;
        while (!_readyOkReceived && readyElapsed < readyTimeout)
        {
            yield return new WaitForSeconds(0.1f);
            readyElapsed += 0.1f;
        }

        if (!_readyOkReceived)
        {
            Debug.LogError("<color=red>等待readyok超时！</color>");
            yield break;
        }

        // 不在这里发送ucinewgame，留给测试序列
    }

    // ========== 发送命令给引擎 ==========

    public void SendCommand(string command)
    {
        if (!_isRunning || _engineProcess == null || _engineProcess.HasExited)
        {
            Debug.LogWarning("引擎未运行，无法发送命令");
            return;
        }

        // 使用主线程队列确保在主线程中执行
        _mainThreadQueue.Enqueue(() =>
        {
            try
            {
                if (_engineProcess != null && !_engineProcess.HasExited)
                {
                    if (EnableDebugLogs)
                    {
                        Debug.Log($"发送: {command}");
                    }

                    _engineProcess.StandardInput.WriteLine(command);
                    _engineProcess.StandardInput.Flush();
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"发送命令失败: {e.Message}");
                OnEngineError?.Invoke($"发送命令失败: {e.Message}");
            }
        });
    }

    // ========== 读取引擎输出 ==========

    private void ReadEngineOutput()
    {
        while (_isRunning)
        {
            _outputReceived.WaitOne(100); // 等待新数据或超时

            // 处理所有队列中的消息
            while (_outputQueue.TryDequeue(out string message))
            {
                ProcessEngineMessage(message);
            }
        }
    }

    private void ProcessEngineMessage(string message)
    {
        // 记录原始消息（仅当启用详细日志时）
        if (EnableDebugLogs)
        {
            if (message.Contains("bestmove"))
            {
                Debug.Log($"<color=green>收到最佳走法: {message}</color>");
            }
            else if (message.Contains("readyok"))
            {
                Debug.Log($"<color=green>收到: {message}</color>");
            }
            else if (message.Contains("uciok"))
            {
                Debug.Log($"<color=green>收到: {message}</color>");
            }
            else if (message.Contains("error") || message.Contains("Error"))
            {
                Debug.Log($"<color=red>错误: {message}</color>");
            }
        }

        // 更新UCI状态标志
        if (message == "uciok")
        {
            _uciOkReceived = true;
        }
        else if (message == "readyok")
        {
            _readyOkReceived = true;

            // 测试期间的特殊处理
            if (_testInProgress)
            {
                _testSuccessful = true;
            }
        }

        // 将消息处理放入主线程队列
        _mainThreadQueue.Enqueue(() =>
        {
            ProcessMessageOnMainThread(message);
        });
    }

    // 在主线程中处理消息
    private void ProcessMessageOnMainThread(string message)
    {
        try
        {
            // 触发原始消息事件
            OnEngineMessageReceived?.Invoke(message);

            // 解析消息类型
            if (message.StartsWith("bestmove"))
            {
                string[] parts = message.Split(' ');
                if (parts.Length >= 2)
                {
                    string bestMove = parts[1];

                    if (EnableDebugLogs)
                    {
                        Debug.Log($"<color=green>成功解析最佳走法: {bestMove}</color>");
                    }

                    OnBestMoveReceived?.Invoke(bestMove);

                    // 测试期间的特殊处理
                    if (_testInProgress)
                    {
                        _testSuccessful = true;
                        _testResultMessage = "通信测试成功：引擎响应正常";
                        CompleteTest();
                    }
                }
            }
            else if (message.StartsWith("info"))
            {
                // 引擎思考过程中的信息
                OnInfoReceived?.Invoke(message);
            }
            else if (message == "readyok")
            {
                IsEngineReady = true;
                OnEngineReadyChanged?.Invoke(true);

                if (EnableDebugLogs)
                {
                    Debug.Log($"<color=green>引擎准备就绪！</color>");
                }

                // 现在这是在主线程中，可以安全启动协程
                if (RunAutoTestOnStart && !_testInProgress)
                {
                    StartCoroutine(RunAutoTestSequence());
                }
            }
            else if (message == "uciok")
            {
                if (EnableDebugLogs)
                {
                    Debug.Log($"<color=green>UCI协议握手成功！</color>");
                }
            }
            else if (message.Contains("error") || message.Contains("Error"))
            {
                // 处理错误消息
                OnEngineError?.Invoke(message);
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"处理引擎消息时出错: {e.Message}");
        }
    }

    // 处理主线程队列
    private void ProcessMainThreadQueue()
    {
        while (_mainThreadQueue.TryDequeue(out Action action))
        {
            try
            {
                action?.Invoke();
            }
            catch (Exception e)
            {
                Debug.LogError($"执行主线程任务时出错: {e.Message}");
            }
        }
    }

    // 完成测试并显示结果
    private void CompleteTest()
    {
        _testInProgress = false;

        if (_testSuccessful)
        {
            Debug.Log("<color=green>=======================================</color>");
            Debug.Log($"<color=green>引擎通信测试完成</color>");
            Debug.Log($"<color=green>{_testResultMessage}</color>");
            Debug.Log($"<color=green>所有关键通信测试已通过</color>");
            Debug.Log($"<color=green>引擎已准备就绪，可以开始下棋</color>");
            Debug.Log("<color=green>=======================================</color>");
        }
        else
        {
            Debug.Log("<color=red>=======================================</color>");
            Debug.Log($"<color=red>引擎通信测试失败</color>");
            Debug.Log($"<color=red>{_testResultMessage}</color>");
            Debug.Log($"<color=red>请检查引擎路径和配置</color>");
            Debug.Log("<color=red>=======================================</color>");
        }

        // 触发测试完成事件
        OnCommunicationTestComplete?.Invoke(_testSuccessful, _testResultMessage);
    }

    // 简化的自动测试方法
    private IEnumerator RunAutoTestSequence()
    {
        if (_testInProgress) yield break;

        _testInProgress = true;
        _testSuccessful = false;
        _testResultMessage = "通信测试中...";

        Debug.Log("<color=magenta>开始引擎通信测试...</color>");

        // 等待1秒让引擎稳定
        yield return new WaitForSeconds(TestStepDelay);

        // 开始新游戏
        SendCommand("ucinewgame");
        yield return new WaitForSeconds(1.0f);

        // 设置初始棋盘位置
        SendCommand("position startpos");
        yield return new WaitForSeconds(1.0f);

        // 让引擎思考3步深度
        SendCommand("go depth 3");

        // 等待最多10秒以完成测试
        float timeout = 10.0f;
        float elapsed = 0f;

        while (_testInProgress && elapsed < timeout)
        {
            yield return new WaitForSeconds(0.5f);
            elapsed += 0.5f;
        }

        // 检查是否超时
        if (_testInProgress)
        {
            _testResultMessage = $"通信测试失败：在{timeout}秒内未收到期望的响应";
            CompleteTest();
        }
    }

    // ========== 清理和关闭 ==========

    private void ShutdownEngine()
    {
        if (_isShuttingDown) return;
        _isShuttingDown = true;

        _isRunning = false;
        _outputReceived?.Set(); // 唤醒线程以便退出

        // 关闭读取线程
        if (_readThread != null && _readThread.IsAlive)
        {
            _readThread.Join(1000);
            _readThread = null;
        }

        // 关闭引擎进程
        if (_engineProcess != null)
        {
            try
            {
                if (!_engineProcess.HasExited)
                {
                    // 发送退出命令
                    try
                    {
                        _engineProcess.StandardInput.WriteLine("quit");
                        _engineProcess.StandardInput.Flush();
                    }
                    catch { }

                    // 等待正常退出
                    if (!_engineProcess.WaitForExit(2000))
                    {
                        _engineProcess.Kill();
                        Debug.Log("引擎被强制终止");
                    }
                }

                _engineProcess.Close();
                _engineProcess.Dispose();
                _engineProcess = null;

                Debug.Log("引擎已关闭");
            }
            catch (Exception e)
            {
                Debug.LogError($"关闭引擎时出错: {e.Message}");
            }
        }

        // 释放AutoResetEvent
        _outputReceived?.Dispose();
        _outputReceived = null;

        // 重置状态标志
        IsEngineReady = false;
        IsInitialized = false;
        _uciOkReceived = false;
        _readyOkReceived = false;
        _isShuttingDown = false;
    }

    // ========== 公共方法供其他脚本调用 ==========

    public void RequestMove(string fenPosition, int thinkingTimeMs = 3000)
    {
        if (!IsEngineReady)
        {
            Debug.LogWarning("引擎未就绪");
            return;
        }

        SetPosition(fenPosition);
        SendCommand($"go movetime {thinkingTimeMs}");
    }

    public void SetPosition(string fen = "startpos", string moves = "")
    {
        if (string.IsNullOrEmpty(moves))
            SendCommand($"position {fen}");
        else
            SendCommand($"position {fen} moves {moves}");
    }

    public void StartCalculation(int depth = 18, int movetime = 0)
    {
        if (movetime > 0)
            SendCommand($"go movetime {movetime}");
        else
            SendCommand($"go depth {depth}");
    }

    public void StopCalculation()
    {
        SendCommand("stop");
    }

    public void RestartEngine()
    {
        ShutdownEngine();
        InitializeEngine();
    }

    // 手动运行通信测试
    public void RunCommunicationTest()
    {
        if (!IsEngineReady)
        {
            Debug.LogWarning("引擎未就绪，无法运行测试");
            return;
        }

        StartCoroutine(RunAutoTestSequence());
    }
}
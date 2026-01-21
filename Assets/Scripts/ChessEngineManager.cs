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
    public string enginePath = @"C:\Path\To\Your\lynx-cli.exe";

    [Header("调试与测试选项")]
    [Tooltip("是否显示详细的通信日志")]
    public bool enableDebugLogs = true;

    [Tooltip("是否在启动时自动运行测试序列")]
    public bool runAutoTestOnStart = true;

    [Tooltip("测试序列每一步的等待时间（秒）")]
    public float testStepDelay = 1.0f;

    // ========== 私有变量 ==========
    private Process engineProcess;
    private Thread readThread;
    private bool isRunning = false;
    private ConcurrentQueue<string> outputQueue = new ConcurrentQueue<string>();
    private AutoResetEvent outputReceived = new AutoResetEvent(false);

    // ========== 事件回调 ==========
    public event Action<string> OnEngineMessageReceived;
    public event Action<string> OnBestMoveReceived;
    public event Action<string> OnInfoReceived;
    public event Action<bool> OnEngineReadyChanged;

    // ========== 引擎状态 ==========
    public bool IsEngineReady { get; private set; }

    void Start()
    {
        // 初始化引擎
        InitializeEngine();
    }

    void Update()
    {
        // 在主线程中处理引擎输出
        ProcessQueuedOutput();
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
        string finalPath = GetEnginePath();

        if (!File.Exists(finalPath))
        {
            Debug.LogError($"引擎文件不存在: {finalPath}");
            return;
        }

        // 添加的日志开始
        if (enableDebugLogs)
        {
            Debug.Log("========================================");
            Debug.Log("开始初始化国际象棋引擎");
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
            engineProcess = new Process { StartInfo = startInfo };

            // 设置输出数据接收事件
            engineProcess.OutputDataReceived += (sender, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    outputQueue.Enqueue(e.Data);
                    outputReceived.Set(); // 通知有新数据
                }
            };

            // 启动引擎
            engineProcess.Start();

            // 开始异步读取输出
            engineProcess.BeginOutputReadLine();
            engineProcess.BeginErrorReadLine();

            isRunning = true;

            // 启动读取线程
            readThread = new Thread(ReadEngineOutput);
            readThread.IsBackground = true;
            readThread.Start();

            if (enableDebugLogs)
            {
                Debug.Log($"引擎进程已启动！");
                Debug.Log($"进程ID: {engineProcess.Id}");
                Debug.Log($"启动时间: {DateTime.Now:HH:mm:ss}");
                Debug.Log($"引擎已启动: {Path.GetFileName(finalPath)} (PID: {engineProcess.Id})");
            }

            // 初始化UCI协议
            StartUCIProtocol();
        }
        catch (Exception e)
        {
            Debug.LogError($"启动引擎失败: {e.Message}");
        }
    }

    private string GetEnginePath()
    {
        // 方法1: 使用直接指定的路径
        if (!string.IsNullOrEmpty(enginePath) && File.Exists(enginePath))
            return enginePath;

        // 方法2: 从StreamingAssets读取（推荐）
        string streamingPath = Path.Combine(Application.streamingAssetsPath, "Engines", "lynx-cli.exe");
        if (File.Exists(streamingPath))
            return streamingPath;

        // 方法3: 从Resources读取
        TextAsset engineBinary = Resources.Load<TextAsset>("Engines/lynx-cli");
        if (engineBinary != null)
        {
            // 需要将TextAsset写入临时文件
            string tempPath = Path.Combine(Application.persistentDataPath, "lynx-cli.exe");
            File.WriteAllBytes(tempPath, engineBinary.bytes);
            return tempPath;
        }

        return enginePath; // 返回用户指定的路径，即使不存在
    }

    // ========== UCI协议初始化 ==========

    private void StartUCIProtocol()
    {
        if (enableDebugLogs)
        {
            Debug.Log("<color=blue>启动UCI协议...</color>");
        }

        // 发送UCI初始化命令
        SendCommand("uci");

        // 等待引擎就绪
        StartCoroutine(WaitForEngineReady());
    }

    private System.Collections.IEnumerator WaitForEngineReady()
    {
        if (enableDebugLogs)
        {
            Debug.Log("<color=blue>等待引擎准备就绪...</color>");
        }

        yield return new WaitForSeconds(1.0f);
        SendCommand("isready");
        SendCommand("ucinewgame");
    }

    // ========== 发送命令给引擎 ==========

    public void SendCommand(string command)
    {
        if (!isRunning || engineProcess == null || engineProcess.HasExited)
        {
            Debug.LogWarning("引擎未运行，无法发送命令");
            return;
        }

        // 确保在主线程中执行
        UnityMainThreadDispatcher.Instance?.Enqueue(() =>
        {
            try
            {
                engineProcess.StandardInput.WriteLine(command);
                if (enableDebugLogs)
                {
                    Debug.Log($"发送: {command}");
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"发送命令失败: {e.Message}");
            }
        });
    }

    // ========== 读取引擎输出 ==========

    private void ReadEngineOutput()
    {
        while (isRunning)
        {
            outputReceived.WaitOne(100); // 等待新数据或超时

            // 处理所有队列中的消息
            while (outputQueue.TryDequeue(out string message))
            {
                ProcessEngineMessage(message);
            }
        }
    }

    private void ProcessEngineMessage(string message)
    {
        // 第一步：记录原始消息（Debug.Log 是线程安全的，可以保留）
        if (enableDebugLogs)
        {
            // 根据不同消息类型使用不同颜色
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
            else
            {
                Debug.Log($"<color=yellow>收到: {message}</color>");
            }
        }

        // 第二步：将消息处理移到主线程队列中
        UnityMainThreadDispatcher.Instance?.Enqueue(() =>
        {
            ProcessMessageOnMainThread(message);
        });
    }

    // 在主线程中处理消息
    private void ProcessMessageOnMainThread(string message)
    {
        // 触发原始消息事件
        OnEngineMessageReceived?.Invoke(message);

        // 第三步：解析消息类型并添加成功标记
        if (message.StartsWith("bestmove"))
        {
            string[] parts = message.Split(' ');
            if (parts.Length >= 2)
            {
                string bestMove = parts[1];

                // 成功收到最佳走法！
                if (enableDebugLogs)
                {
                    Debug.Log($"<color=green>=======================================</color>");
                    Debug.Log($"<color=green>成功解析最佳走法!</color>");
                    Debug.Log($"<color=green>走法: {bestMove}</color>");
                    Debug.Log($"<color=green>时间: {DateTime.Now:HH:mm:ss.fff}</color>");
                    Debug.Log($"<color=green>=======================================</color>");
                }

                OnBestMoveReceived?.Invoke(bestMove);
            }
        }
        else if (message.StartsWith("info"))
        {
            // 引擎思考过程中的信息
            if (enableDebugLogs)
            {
                Debug.Log($"<color=blue>思考信息: {message}</color>");
            }
            OnInfoReceived?.Invoke(message);
        }
        else if (message == "readyok")
        {
            IsEngineReady = true;
            OnEngineReadyChanged?.Invoke(true);

            if (enableDebugLogs)
            {
                Debug.Log($"<color=green>=======================================</color>");
                Debug.Log($"<color=green>引擎准备就绪！</color>");
                Debug.Log($"<color=green>可以开始下棋了！</color>");
                Debug.Log($"<color=green>=======================================</color>");
            }

            // 现在这是在主线程中，可以安全启动协程
            if (runAutoTestOnStart)
            {
                StartCoroutine(RunAutoTestSequence());
            }
        }
        else if (message == "uciok")
        {
            if (enableDebugLogs)
            {
                Debug.Log($"<color=green>UCI协议握手成功！</color>");
            }
        }
    }

    // 新添加的自动测试方法
    private IEnumerator RunAutoTestSequence()
    {
        if (!enableDebugLogs) yield break;

        Debug.Log("<color=magenta>=======================================</color>");
        Debug.Log("<color=magenta>开始自动测试序列</color>");
        Debug.Log("<color=magenta>=======================================</color>");

        // 等待1秒让引擎稳定
        yield return new WaitForSeconds(testStepDelay);

        // 测试1: 发送uci命令（很多引擎需要这个来初始化）
        Debug.Log("<color=magenta>测试1: 发送 'uci' 命令</color>");
        SendCommand("uci");
        yield return new WaitForSeconds(testStepDelay);

        // 测试2: 检查引擎是否就绪
        Debug.Log("<color=magenta>测试2: 发送 'isready' 命令</color>");
        SendCommand("isready");
        yield return new WaitForSeconds(testStepDelay);

        // 测试3: 开始新游戏
        Debug.Log("<color=magenta>测试3: 发送 'ucinewgame' 命令</color>");
        SendCommand("ucinewgame");
        yield return new WaitForSeconds(testStepDelay);

        // 测试4: 设置初始棋盘位置
        Debug.Log("<color=magenta>测试4: 设置初始棋盘位置</color>");
        SendCommand("position startpos");
        yield return new WaitForSeconds(testStepDelay);

        // 测试5: 让引擎思考3步深度
        Debug.Log("<color=magenta>测试5: 让引擎思考（深度3）</color>");
        SendCommand("go depth 3");

        // 测试6: 等待10秒，然后停止计算（测试停止功能）
        Debug.Log("<color=magenta>测试6: 等待5秒后停止计算</color>");
        yield return new WaitForSeconds(5.0f);
        SendCommand("stop");

        yield return new WaitForSeconds(testStepDelay);

        Debug.Log("<color=magenta>=======================================</color>");
        Debug.Log("<color=magenta>自动测试序列完成</color>");
        Debug.Log("<color=magenta>请查看上面的日志确认通信是否正常</color>");
        Debug.Log("<color=magenta>=======================================</color>");

        // 测试7: 关闭引擎（可选）
        // Debug.Log("测试7: 发送 'quit' 命令");
        // SendCommand("quit");
        // yield return new WaitForSeconds(1.0f);
    }

    private void ProcessQueuedOutput()
    {
        // 如果不想用线程，也可以在这里处理
        // 但为了性能，我们使用后台线程读取，主线程只处理事件
    }

    // ========== 清理和关闭 ==========

    private void ShutdownEngine()
    {
        isRunning = false;
        outputReceived?.Set(); // 唤醒线程以便退出

        if (engineProcess != null && !engineProcess.HasExited)
        {
            try
            {
                SendCommand("quit");

                // 等待正常退出
                if (!engineProcess.WaitForExit(2000))
                {
                    engineProcess.Kill();
                    Debug.Log("引擎被强制终止");
                }

                engineProcess.Close();
                engineProcess.Dispose();
                engineProcess = null;

                Debug.Log("引擎已关闭");
            }
            catch (Exception e)
            {
                Debug.LogError($"关闭引擎时出错: {e.Message}");
            }
        }

        if (readThread != null && readThread.IsAlive)
        {
            readThread.Join(1000);
        }
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
}
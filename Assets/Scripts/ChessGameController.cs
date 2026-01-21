using UnityEngine;
using UnityEngine.UI;

public class ChessGameController : MonoBehaviour
{
    [Header("UI引用")]
    public Button calculateButton;
    public Text engineStatusText;
    public Text bestMoveText;

    private ChessEngineManager engineManager;

    void Start()
    {
        // 获取或添加引擎管理器
        engineManager = FindObjectOfType<ChessEngineManager>();
        if (engineManager == null)
        {
            GameObject go = new GameObject("EngineManager");
            engineManager = go.AddComponent<ChessEngineManager>();
        }

        // 设置UI
        if (calculateButton != null)
        {
            calculateButton.onClick.AddListener(OnCalculateClick);
            calculateButton.interactable = false;
        }

        // 订阅引擎事件
        engineManager.OnEngineReadyChanged += OnEngineReadyChanged;
        engineManager.OnBestMoveReceived += OnBestMoveReceived;
        engineManager.OnEngineMessageReceived += OnEngineMessageReceived;
    }

    void OnEngineReadyChanged(bool isReady)
    {
        UnityMainThreadDispatcher.Instance.Enqueue(() =>
        {
            if (engineStatusText != null)
                engineStatusText.text = isReady ? "引擎状态: 就绪" : "引擎状态: 初始化中";

            if (calculateButton != null)
                calculateButton.interactable = isReady;
        });
    }

    void OnBestMoveReceived(string bestMove)
    {
        UnityMainThreadDispatcher.Instance.Enqueue(() =>
        {
            if (bestMoveText != null)
                bestMoveText.text = $"最佳走法: {bestMove}";

            Debug.Log($"收到最佳走法: {bestMove}");

            // 在这里将走法应用到棋盘
            ApplyMoveToBoard(bestMove);
        });
    }

    void OnEngineMessageReceived(string message)
    {
        // 可选：记录所有消息
        // Debug.Log($"引擎消息: {message}");
    }

    void OnCalculateClick()
    {
        if (engineManager == null || !engineManager.IsEngineReady)
            return;

        // 示例：起始局面，计算下一步
        string fenPosition = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1";

        engineManager.RequestMove(fenPosition, 2000); // 思考2秒

        if (bestMoveText != null)
            bestMoveText.text = "思考中...";
    }

    void ApplyMoveToBoard(string move)
    {
        // 这里实现将走法应用到你的棋盘
        // 例如：move = "e2e4" 表示从e2移动到e4

        Debug.Log($"应用走法到棋盘: {move}");

        // 解析move
        string fromSquare = move.Substring(0, 2); // 前两个字符
        string toSquare = move.Substring(2, 2);   // 后两个字符

        // 调用你的棋盘逻辑
        // chessBoard.MovePiece(fromSquare, toSquare);
    }

    void OnDestroy()
    {
        if (engineManager != null)
        {
            engineManager.OnEngineReadyChanged -= OnEngineReadyChanged;
            engineManager.OnBestMoveReceived -= OnBestMoveReceived;
            engineManager.OnEngineMessageReceived -= OnEngineMessageReceived;
        }
    }
}
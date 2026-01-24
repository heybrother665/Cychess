using UnityEngine;
using System.Collections;

public class SimpleEngineTester : MonoBehaviour
{
    private ChessEngineManager engineManager;

    void Start()
    {
        Debug.Log("=== SimpleEngineTester 开始 ===");

        // 查找引擎管理器
        engineManager = FindObjectOfType<ChessEngineManager>();

        if (engineManager == null)
        {
            Debug.LogError("? 找不到 ChessEngineManager！");
            Debug.Log("请在场景中创建一个 GameObject，并添加 ChessEngineManager 组件");
            return;
        }

        Debug.Log("? 找到 ChessEngineManager");

        // 开始测试协程
        StartCoroutine(RunEngineTest());
    }

    IEnumerator RunEngineTest()
    {
        Debug.Log("? 等待引擎初始化...");

        // 等待引擎就绪
        float timeout = 15.0f; // 最多等待15秒
        float startTime = Time.time;

        while (!engineManager.IsEngineReady && (Time.time - startTime) < timeout)
        {
            Debug.Log("等待引擎就绪...");
            yield return new WaitForSeconds(1.0f);
        }

        if (!engineManager.IsEngineReady)
        {
            Debug.LogError("? 引擎初始化超时！");
            yield break;
        }

        Debug.Log("? 引擎已就绪，开始通信测试");

        // 测试1: 发送 UCI 命令
        Debug.Log("测试1: 发送 'uci' 命令");
        engineManager.SendCommand("uci");
        yield return new WaitForSeconds(2.0f);

        // 测试2: 发送 isready
        Debug.Log("测试2: 发送 'isready' 命令");
        engineManager.SendCommand("isready");
        yield return new WaitForSeconds(2.0f);

        // 测试3: 设置初始位置并计算
        Debug.Log("测试3: 设置棋盘并计算");
        engineManager.SendCommand("position startpos");
        engineManager.SendCommand("go depth 1");

        // 等待计算结果
        yield return new WaitForSeconds(5.0f);

        Debug.Log("=== 通信测试完成 ===");
        Debug.Log("如果看到引擎回复，通信成功！");
    }

    void Update()
    {
        // 按 F1 手动测试
        if (Input.GetKeyDown(KeyCode.F1))
        {
            Debug.Log("?? 按 F1: 手动测试");
            if (engineManager != null && engineManager.IsEngineReady)
            {
                engineManager.SendCommand("position startpos");
                engineManager.SendCommand("go depth 2");
            }
        }
    }
}
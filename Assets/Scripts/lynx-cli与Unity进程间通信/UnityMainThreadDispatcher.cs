using UnityEngine;
using System.Collections.Concurrent;
using System;

public class UnityMainThreadDispatcher : MonoBehaviour
{
    private static UnityMainThreadDispatcher instance;
    private ConcurrentQueue<Action> actionQueue = new ConcurrentQueue<Action>();

    public static UnityMainThreadDispatcher Instance
    {
        get
        {
            if (instance == null)
            {
                instance = FindObjectOfType<UnityMainThreadDispatcher>();
                if (instance == null)
                {
                    GameObject go = new GameObject("MainThreadDispatcher");
                    instance = go.AddComponent<UnityMainThreadDispatcher>();
                    DontDestroyOnLoad(go);
                }
            }
            return instance;
        }
    }

    void Awake()
    {
        if (instance == null)
        {
            instance = this;
            DontDestroyOnLoad(gameObject);
        }
        else if (instance != this)
        {
            Destroy(gameObject);
        }
    }

    void Update()
    {
        // 在主线程中执行所有排队的操作
        while (actionQueue.TryDequeue(out Action action))
        {
            action?.Invoke();
        }
    }

    public void Enqueue(Action action)
    {
        if (action != null)
        {
            actionQueue.Enqueue(action);
        }
    }
}

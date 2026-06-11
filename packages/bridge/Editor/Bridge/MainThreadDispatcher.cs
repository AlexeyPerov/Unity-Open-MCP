using System;
using System.Collections.Concurrent;
using UnityEditor;

namespace UnityAgentBridge
{
    public static class MainThreadDispatcher
    {
        static readonly ConcurrentQueue<Action> _queue = new();

        [InitializeOnLoadMethod]
        static void Initialize()
        {
            EditorApplication.update -= ProcessQueue;
            EditorApplication.update += ProcessQueue;
        }

        static void ProcessQueue()
        {
            while (_queue.TryDequeue(out var action))
            {
                try
                {
                    action();
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                }
            }
        }

        public static void Enqueue(Action action)
        {
            _queue.Enqueue(action);
        }
    }
}

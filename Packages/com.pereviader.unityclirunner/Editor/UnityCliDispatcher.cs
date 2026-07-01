using System;
using System.Collections.Concurrent;
using UnityEditor;
using UnityEngine;

namespace UnityCliRunner
{
    [InitializeOnLoad]
    internal static class UnityCliDispatcher
    {
        private static readonly ConcurrentQueue<Action> s_Queue = new ConcurrentQueue<Action>();

        static UnityCliDispatcher()
        {
            EditorApplication.update += Update;
        }

        public static void Enqueue(Action action)
        {
            s_Queue.Enqueue(action);
        }

        private static void Update()
        {
            while (s_Queue.TryDequeue(out var action))
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
    }
}

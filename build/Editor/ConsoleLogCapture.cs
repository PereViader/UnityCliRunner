using System;
using System.Collections.Generic;
using UnityEngine;

namespace UnityCliRunner
{
    internal class ConsoleLogCapture : IDisposable
    {
        private const int MaxEntries = 2000;
        private readonly object m_Lock = new object();
        private readonly List<ConsoleLogEntry> m_Logs = new List<ConsoleLogEntry>();
        private bool m_Disposed = false;
        private bool m_LimitReached = false;

        public ConsoleLogCapture()
        {
            Application.logMessageReceivedThreaded += OnLogReceived;
        }

        private void OnLogReceived(string condition, string stackTrace, LogType type)
        {
            if (m_Disposed || condition == null)
            {
                return;
            }

            // Filter out internal UnityCliRunner harness messages
            if (condition.StartsWith("UnityCliRunner:", StringComparison.Ordinal))
            {
                return;
            }

            lock (m_Lock)
            {
                if (m_Disposed)
                {
                    return;
                }

                if (m_Logs.Count < MaxEntries)
                {
                    m_Logs.Add(new ConsoleLogEntry
                    {
                        message = condition,
                        logType = type.ToString()
                    });
                }
                else if (!m_LimitReached)
                {
                    m_LimitReached = true;
                    m_Logs.Add(new ConsoleLogEntry
                    {
                        message = "[UnityCliRunner: Console log output truncated (exceeded 2,000 entries)]",
                        logType = LogType.Warning.ToString()
                    });
                }
            }
        }

        public List<ConsoleLogEntry> GetLogs()
        {
            lock (m_Lock)
            {
                return new List<ConsoleLogEntry>(m_Logs);
            }
        }

        public void Dispose()
        {
            lock (m_Lock)
            {
                if (m_Disposed)
                {
                    return;
                }
                m_Disposed = true;
            }

            Application.logMessageReceivedThreaded -= OnLogReceived;
        }
    }
}

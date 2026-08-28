using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace UnityCliRunner
{
    [Serializable]
    internal sealed class UnityCliOperationState
    {
        public string operationId;
        public string kind;
        public string status;
        public string editorSessionId;
        public string startedUtc;
        public string updatedUtc;
    }

    internal enum BeginOperationResult
    {
        Started,
        AlreadyStarted,
        Busy,
        Invalid
    }

    /// <summary>
    /// Durable, project-scoped ownership for commands which may outlive their
    /// socket or managed AppDomain. All mutations happen on Unity's main thread.
    /// </summary>
    [InitializeOnLoad]
    internal static class UnityCliOperationStore
    {
        private const string EditorSessionKey = "UnityCliRunner.EditorSessionId";
        private const string OperationFileName = "unity_cli_operation.json";
        private static readonly object s_CacheLock = new object();
        private static UnityCliOperationState s_CachedState;
        private static string s_EditorSessionId;

        internal static string OperationFilePath => Path.Combine(GetTempDirectory(), OperationFileName);

        internal static string EditorSessionId
        {
            get
            {
                if (string.IsNullOrEmpty(s_EditorSessionId))
                {
                    string value = SessionState.GetString(EditorSessionKey, "");
                    if (string.IsNullOrEmpty(value))
                    {
                        value = Guid.NewGuid().ToString("N");
                        SessionState.SetString(EditorSessionKey, value);
                    }
                    s_EditorSessionId = value;
                }

                return s_EditorSessionId;
            }
        }

        static UnityCliOperationStore()
        {
            EnsureInitialized();
        }

        internal static void EnsureInitialized()
        {
            try
            {
                _ = EditorSessionId;
                Read();
            }
            catch (Exception ex)
            {
                Debug.LogError($"UnityCliRunner: Failed to initialize operation store: {ex}");
            }
        }

        internal static BeginOperationResult TryBegin(string operationId, string kind, string status, out UnityCliOperationState existing)
        {
            existing = Read();
            if (!IsValidToken(operationId) || !IsValidToken(kind))
            {
                return BeginOperationResult.Invalid;
            }

            if (existing != null)
            {
                if (existing.operationId == operationId && existing.kind == kind)
                {
                    return BeginOperationResult.AlreadyStarted;
                }

                return BeginOperationResult.Busy;
            }

            string now = DateTime.UtcNow.ToString("o");
            var state = new UnityCliOperationState
            {
                operationId = operationId,
                kind = kind,
                status = status,
                editorSessionId = EditorSessionId,
                startedUtc = now,
                updatedUtc = now
            };
            Write(state);
            existing = state;
            return BeginOperationResult.Started;
        }

        internal static UnityCliOperationState Read()
        {
            try
            {
                if (!File.Exists(OperationFilePath))
                {
                    SetCachedState(null);
                    return null;
                }

                var state = JsonUtility.FromJson<UnityCliOperationState>(File.ReadAllText(OperationFilePath));
                if (state == null || !IsValidToken(state.operationId) || !IsValidToken(state.kind))
                {
                    QuarantineMalformedRecord();
                    SetCachedState(null);
                    return null;
                }

                SetCachedState(state);
                return state;
            }
            catch (Exception ex)
            {
                SetCachedState(null);
                Debug.LogError($"UnityCliRunner: Failed to read operation journal: {ex}");
                return null;
            }
        }

        internal static UnityCliOperationState ReadThreadSafeSnapshot()
        {
            lock (s_CacheLock)
            {
                return Clone(s_CachedState);
            }
        }

        internal static bool Update(string operationId, string status)
        {
            var state = Read();
            if (state == null || state.operationId != operationId)
            {
                return false;
            }

            state.status = status;
            state.updatedUtc = DateTime.UtcNow.ToString("o");
            Write(state);
            return true;
        }

        internal static bool Complete(string operationId)
        {
            var state = Read();
            if (state == null || state.operationId != operationId)
            {
                return false;
            }

            try
            {
                File.Delete(OperationFilePath);
                SetCachedState(null);
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"UnityCliRunner: Failed to complete operation '{operationId}': {ex}");
                return false;
            }
        }

        internal static bool IsOwnedBy(string operationId, string kind)
        {
            var state = Read();
            return state != null && state.operationId == operationId && state.kind == kind;
        }

        internal static void Write(UnityCliOperationState state)
        {
            Directory.CreateDirectory(GetTempDirectory());
            WriteAtomic(OperationFilePath, JsonUtility.ToJson(state, true), state.operationId);
            SetCachedState(state);
        }

        internal static void WriteAtomic(string path, string content, string operationId)
        {
            string tempPath = path + "." + (operationId ?? Guid.NewGuid().ToString("N")) + ".tmp";
            try
            {
                File.WriteAllText(tempPath, content, new UTF8Encoding(false));
                for (int i = 0; i < 5; i++)
                {
                    try
                    {
                        if (File.Exists(path))
                        {
                            File.Replace(tempPath, path, null);
                        }
                        else
                        {
                            File.Move(tempPath, path);
                        }
                        return;
                    }
                    catch (IOException) when (i < 4)
                    {
                        System.Threading.Thread.Sleep(10);
                    }
                    catch (UnauthorizedAccessException) when (i < 4)
                    {
                        System.Threading.Thread.Sleep(10);
                    }
                }
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    try { File.Delete(tempPath); } catch { }
                }
            }
        }

        private static bool IsValidToken(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > 128)
            {
                return false;
            }

            foreach (char c in value)
            {
                if (!(char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.'))
                {
                    return false;
                }
            }

            return true;
        }

        private static void SetCachedState(UnityCliOperationState state)
        {
            lock (s_CacheLock)
            {
                s_CachedState = Clone(state);
            }
        }

        private static UnityCliOperationState Clone(UnityCliOperationState state)
        {
            if (state == null) return null;
            return new UnityCliOperationState
            {
                operationId = state.operationId,
                kind = state.kind,
                status = state.status,
                editorSessionId = state.editorSessionId,
                startedUtc = state.startedUtc,
                updatedUtc = state.updatedUtc
            };
        }

        private static void QuarantineMalformedRecord()
        {
            try
            {
                string quarantinePath = OperationFilePath + ".invalid." + Guid.NewGuid().ToString("N");
                File.Move(OperationFilePath, quarantinePath);
            }
            catch (Exception ex)
            {
                Debug.LogError($"UnityCliRunner: Failed to quarantine malformed operation journal: {ex}");
            }
        }

        private static string GetTempDirectory()
        {
            return Path.Combine(CommandHelper.ProjectRoot, "Temp");
        }
    }
}

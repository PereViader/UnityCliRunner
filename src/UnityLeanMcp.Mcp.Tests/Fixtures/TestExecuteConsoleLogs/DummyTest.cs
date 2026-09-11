using UnityEngine;

namespace Tests
{
    public class DummyTest
    {
    }

    public static class DummyExecuteClass
    {
        public static int LogAndReturn()
        {
            Debug.Log("Standard log message from execute");
            Debug.LogWarning("Warning log message from execute");
            return 999;
        }
    }
}

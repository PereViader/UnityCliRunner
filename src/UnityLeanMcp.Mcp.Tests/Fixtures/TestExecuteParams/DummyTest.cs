using UnityEngine;

namespace Tests
{
    public class DummyTest
    {
    }

    [System.Serializable]
    public class InputJson
    {
        public int Value;
    }

    [System.Serializable]
    public class ResultJson
    {
        public int intVal;
        public float floatVal;
        public string strVal;
        public int jsonVal;
    }

    public static class DummyExecuteClass
    {
        public static ResultJson ParamsMethod(int a, float b, string c, InputJson json)
        {
            return new ResultJson { intVal = a, floatVal = b, strVal = c, jsonVal = json.Value };
        }
    }
}

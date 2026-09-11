using System.Threading.Tasks;
using UnityEngine;

namespace Tests
{
    public class DummyTest
    {
    }

    public static class DummyExecuteClass
    {
        public static async Task AsyncVoidMethod()
        {
            Debug.Log("AsyncVoidMethod start");
            await Task.Delay(50);
            Debug.Log("AsyncVoidMethod end");
        }
    }
}

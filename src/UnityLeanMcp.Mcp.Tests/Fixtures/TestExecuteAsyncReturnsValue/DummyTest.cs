using System.Threading.Tasks;
using UnityEngine;

namespace Tests
{
    public class DummyTest
    {
    }

    public static class DummyExecuteClass
    {
        public static async Task<string> AsyncMethod()
        {
            await Task.Delay(50);
            return "hello-from-async-method";
        }
    }
}

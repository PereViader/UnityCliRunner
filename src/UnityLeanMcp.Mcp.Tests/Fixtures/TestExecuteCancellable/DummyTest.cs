using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Tests
{
    public class DummyTest
    {
    }

    public static class DummyExecuteClass
    {
        public static async Task<string> CancellableMethod(CancellationToken ct)
        {
            await Task.Delay(10000, ct);
            return "completed";
        }

        public static async Task<string> CancellableWithArgMethod(string text, CancellationToken ct)
        {
            await Task.Delay(10000, ct);
            return text;
        }

        public static async Task<string> NonCancellableMethod()
        {
            await Task.Delay(2000);
            return "completed";
        }

        public static string QuickCancellable(CancellationToken ct)
        {
            return "quick-cancellable";
        }

        public static string OverloadedMethod(string value)
        {
            return "no-ct: " + value;
        }

        public static string OverloadedMethod(string value, CancellationToken ct)
        {
            return "with-ct: " + value;
        }
    }
}

using System.Collections;
using System.Reflection;
using NUnit.Framework;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine.TestTools;

namespace Tests
{
    public class DummyTest
    {
        [UnityTest]
        public IEnumerator StopTest()
        {
            var holderProp = typeof(TestRunnerApi).GetProperty("m_testJobDataHolder", BindingFlags.NonPublic | BindingFlags.Static);
            var holder = holderProp?.GetValue(null);
            var getAllRunnersMethod = holder?.GetType().GetMethod("GetAllRunners", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            var runners = (IEnumerable)getAllRunnersMethod?.Invoke(holder, null);
            if (runners != null)
            {
                foreach (var r in runners)
                {
                    var cancelMethod = r.GetType().GetMethod("CancelRun", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    cancelMethod?.Invoke(r, null);
                }
            }
            yield return null;
        }
    }
}

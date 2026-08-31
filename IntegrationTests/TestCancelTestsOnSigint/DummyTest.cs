using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Tests
{
    public class DummyTest
    {
        [UnityTest]
        public IEnumerator LongRunningTest()
        {
            for (int i = 0; i < 100; i++)
            {
                yield return new WaitForSecondsRealtime(0.1f);
            }
        }
    }
}

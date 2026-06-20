using System;
using NUnit.Framework;

namespace Tests
{
    public class DummyTest
    {
        [Test]
        public void PassTest()
        {
            // Warning CS0219: Variable is assigned but its value is never used
            int unusedVar = 42;
            Assert.Pass();
        }
    }
}

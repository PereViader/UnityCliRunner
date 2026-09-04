using System;
using NUnit.Framework;

namespace Tests
{
    public class DummyTest
    {
        [Test]
        public void TestMethod()
        {
            // Warning CS0219: Variable is assigned but its value is never used
            int unusedVar = 42;
            
            // Error CS1002: Semicolon expected
            int errorVar = 2
        }
    }
}

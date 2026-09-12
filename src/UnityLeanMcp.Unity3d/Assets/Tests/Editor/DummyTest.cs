using System;
using NUnit.Framework;

namespace Tests
{
    public class DummyTest
    {
        [Test]
        public void PassTest()
        {
            Assert.Pass();
        }

        [Test]
        public void SpecificTargetTest()
        {
            Assert.Pass();
        }

        [Test]
        public void OtherTest()
        {
            Assert.Pass();
        }

        [Test]
        public void NormalTest()
        {
            Assert.Pass();
        }

        [Test]
        [Category("LongRunning")]
        public void LongTest()
        {
            Assert.Pass();
        }

        [Test]
        [Category("Failing")]
        [Explicit("Explicit failing test for failure diagnostic testing")]
        public void FailTest()
        {
            Assert.Fail("This test failed intentionally.");
        }

        [Test]
        [Ignore("This test is skipped intentionally.")]
        public void IgnoreTest()
        {
            Assert.Pass();
        }

        [Test]
        public void PassWithWarningTest()
        {
            // CS0219: Variable is assigned but its value is never used
            int unusedVar = 42;
            Assert.Pass();
        }
    }
}

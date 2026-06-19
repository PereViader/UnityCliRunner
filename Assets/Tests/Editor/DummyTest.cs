using System;
using NUnit.Framework;

namespace Tests
{
    public class DummyTest
    {
        [Test]
        public void PassTest()
        {
            Assert.Fail("This is not good!");
        }

        [Test]
        public void AnotherTest()
        {
            throw new Exception("Potato");
            Assert.AreEqual(2, 1 + 1);
        }

        [Test]
        [Ignore("This test is skipped intentionally for testing purposes.")]
        public void IgnoreTest()
        {
            Assert.Pass();
        }
    }
}

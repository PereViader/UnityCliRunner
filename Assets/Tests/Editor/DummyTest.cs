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
        public void AnotherTest()
        {
            Assert.AreEqual(2, 1 + 1);
        }
    }
}

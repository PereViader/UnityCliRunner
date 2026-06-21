using NUnit.Framework;

namespace Tests
{
    public class DummyTest
    {
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
    }
}

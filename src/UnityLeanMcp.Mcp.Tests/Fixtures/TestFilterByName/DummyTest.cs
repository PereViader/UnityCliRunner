using NUnit.Framework;

namespace Tests
{
    public class DummyTest
    {
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
    }
}

using NUnit.Framework;

namespace Tests
{
    public class DummyTest
    {
        [Test]
        public void FailTest()
        {
            Assert.Fail("This test failed intentionally.");
        }
    }
}

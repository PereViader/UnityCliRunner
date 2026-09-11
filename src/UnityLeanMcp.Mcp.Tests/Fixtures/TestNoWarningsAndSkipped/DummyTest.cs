using NUnit.Framework;

namespace Tests
{
    public class DummyTest
    {
        [Test]
        [Ignore("This test is skipped intentionally.")]
        public void IgnoreTest()
        {
            Assert.Pass();
        }
    }
}

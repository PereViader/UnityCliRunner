using System;

namespace Tests
{
    public class DummyTest
    {
    }

    public static class DummyExecuteClass
    {
        public static void FailMethod()
        {
            throw new InvalidOperationException("Intentional execution failure!");
        }
    }
}

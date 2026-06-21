using UnityEngine;

namespace Tests
{
    public class DummyTest
    {
    }

    public class Something
    {
        public int Value = 4;
    }

    public static class DummyExecuteClass
    {
        public static Something Something() => new Something();
    }
}

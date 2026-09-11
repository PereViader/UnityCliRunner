namespace Tests
{
    public class DummyTest
    {
    }

    public static class DummyExecuteClass
    {
        public static string Ambiguous(int a) => "int: " + a;
        public static string Ambiguous(float a) => "float: " + a;
    }
}

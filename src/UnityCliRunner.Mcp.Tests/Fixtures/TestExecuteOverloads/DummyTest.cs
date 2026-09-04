namespace Tests
{
    public class DummyTest
    {
    }

    public static class DummyExecuteClass
    {
        public static string OverloadMethod(int a) => "Overload 1: " + a;
        public static string OverloadMethod(int a, string b) => "Overload 2: " + a + ", " + b;
        public static string OverloadMethod(int a, string b, bool c) => "Overload 3: " + a + ", " + b + ", " + c;
    }
}

namespace Tests
{
    public class DummyTest
    {
    }

    public static class DummyExecuteClass
    {
        public static string EchoMultiLine(string text) => text.Replace("\r", "").Replace("\n", "|");
    }
}

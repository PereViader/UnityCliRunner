using System.IO;

namespace UnityCliRunner
{
    internal class PingHandler : ICommandHandler
    {
        public void Handle(string payload, StreamWriter writer)
        {
            writer.WriteLine("PONG");
        }
    }
}

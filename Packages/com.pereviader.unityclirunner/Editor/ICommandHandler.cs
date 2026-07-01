using System.IO;

namespace UnityCliRunner
{
    internal interface ICommandHandler
    {
        void Handle(string payload, StreamWriter writer);
    }
}

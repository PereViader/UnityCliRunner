using System.Collections.Generic;
using System.Text;

namespace UnityCliRunner
{
    internal static class CommandLineTokenizer
    {
        public static string[] SplitArguments(string commandLine)
        {
            var args = new List<string>();
            if (string.IsNullOrEmpty(commandLine))
            {
                return args.ToArray();
            }

            var current = new StringBuilder();
            bool inQuotes = false;
            bool inArg = false;

            for (int i = 0; i < commandLine.Length; i++)
            {
                char c = commandLine[i];
                if (c == '\\')
                {
                    if (i + 1 < commandLine.Length)
                    {
                        char next = commandLine[i + 1];
                        if (next == '"' || next == '\\')
                        {
                            current.Append('\\');
                            current.Append(next);
                            i++;
                            inArg = true;
                            continue;
                        }
                    }
                    current.Append('\\');
                    inArg = true;
                }
                else if (c == '"')
                {
                    inQuotes = !inQuotes;
                    inArg = true;
                }
                else if (c == ' ' && !inQuotes)
                {
                    if (inArg)
                    {
                        args.Add(ProtocolCodec.UnescapeToken(current.ToString()));
                        current.Clear();
                        inArg = false;
                    }
                }
                else
                {
                    current.Append(c);
                    inArg = true;
                }
            }

            if (inArg)
            {
                args.Add(ProtocolCodec.UnescapeToken(current.ToString()));
            }
            return args.ToArray();
        }
    }
}

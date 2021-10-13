using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace DevProxy
{
    public class ProcessTcpConnection
    {
        private static readonly Regex netstatRegex = new Regex(
            @"^\s+TCP\s+([0-9\.]+):([0-9]+)\s+([0-9\.]+):([0-9]+)\s+ESTABLISHED\s+([0-9]+)\s+$",
            RegexOptions.Compiled | RegexOptions.Multiline
        );

        public readonly IPEndPoint LocalEndpoint;
        public readonly IPEndPoint RemoteEndpoint;
        public readonly uint ProcessId;

        private ProcessTcpConnection(Match m)
        {
            this.LocalEndpoint = new IPEndPoint(
                IPAddress.Parse(m.Groups[1].Value),
                int.Parse(m.Groups[2].Value));
            this.RemoteEndpoint = new IPEndPoint(
                IPAddress.Parse(m.Groups[3].Value),
                int.Parse(m.Groups[4].Value));
            this.ProcessId = uint.Parse(m.Groups[5].Value);
        }

        public static async Task<List<ProcessTcpConnection>> FindConnectionsAsync()
        {
            (_, string connectionsString, _) = await ProcessHelpers.RunAsync("netstat", "-n -o", new[] {0});
            return netstatRegex.Matches(connectionsString)
                .Select(m => new ProcessTcpConnection(m))
                .ToList();
        }
    }
}

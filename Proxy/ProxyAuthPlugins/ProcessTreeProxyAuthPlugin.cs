using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;
using Titanium.Web.Proxy.EventArguments;

namespace DevProxy
{
    public class ProcessTreeProxyAuthPlugin : IProxyAuthPlugin
    {
        private readonly ProcessTracker _processTracker;

        public ProcessTreeProxyAuthPlugin(ProcessTracker processTracker)
        {
            _processTracker = processTracker;
        }

        public async Task<(ProxyAuthPluginResult, string)> BeforeRequestAsync(SessionEventArgsBase args)
        {
            var connections = ProcessTcpConnection.FindAllConnections(AddressFamily.InterNetwork);
            var connection = connections.FirstOrDefault(c =>
                c.LocalEndpoint.Address.Equals(args.ClientRemoteEndPoint.Address) &&
                c.LocalEndpoint.Port == args.ClientRemoteEndPoint.Port);

            var ctxt = args.GetRequestContext();
            if (connection == null)
            {
                return (ProxyAuthPluginResult.NoOpinion,"ConnectionNotFound");
            }
            else
            {
                var (found, authRootProcessId) = await _processTracker.TryGetAuthRootAsync(connection.ProcessId);
                if (found)
                {
                    return (ProxyAuthPluginResult.Authenticated,$"RootProcessId={authRootProcessId}");
                }
                else
                {
                    return (ProxyAuthPluginResult.NoOpinion,$"NoAuthRootForProcessId={connection.ProcessId}");
                }
            }
        }
    }
}

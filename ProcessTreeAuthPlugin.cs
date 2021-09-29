using System.Linq;
using System.Threading.Tasks;
using Titanium.Web.Proxy.EventArguments;

namespace DevProxy
{
    public class ProcessTreeAuthPlugin : IAuthPlugin
    {
        private readonly ProcessTracker _processTracker;

        public ProcessTreeAuthPlugin(ProcessTracker processTracker)
        {
            _processTracker = processTracker;
        }

        public async Task<(AuthPluginResult, string)> BeforeRequestAsync(SessionEventArgsBase args)
        {
            var connections = await ProcessTcpConnection.FindConnectionsAsync();
            var connection = connections.FirstOrDefault(c =>
                c.LocalEndpoint.Address.Equals(args.ClientRemoteEndPoint.Address) &&
                c.LocalEndpoint.Port == args.ClientRemoteEndPoint.Port);

            var ctxt = args.GetRequestContext();
            if (connection == null)
            {
                return (AuthPluginResult.NoOpinion,"ConnectionNotFound");
            }
            else
            {
                var (found, authRootProcessId) = await _processTracker.TryGetAuthRootAsync(connection.ProcessId);
                if (found)
                {
                    return (AuthPluginResult.Authenticated,$"RootProcessId={authRootProcessId}");
                }
                else
                {
                    return (AuthPluginResult.NoOpinion,$"NoAuthRootForProcessId={connection.ProcessId}");
                }
            }
        }
    }
}

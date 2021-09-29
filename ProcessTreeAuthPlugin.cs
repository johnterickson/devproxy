using System.Linq;
using System.Threading.Tasks;

namespace DevProxy
{
    public class ProcessTreeAuthPlugin : Plugin
    {
        private readonly ProcessTracker _processTracker;

        public ProcessTreeAuthPlugin(ProcessTracker processTracker)
        {
            _processTracker = processTracker;
        }

        public override async Task<PluginResult> BeforeRequestAsync(PluginRequest request)
        {
            if (request.RequestContext.IsAuthenticated)
            {
                return PluginResult.Continue;
            }

            var connections = await ProcessTcpConnection.FindConnectionsAsync();
            var connection = connections.FirstOrDefault(c =>
                c.LocalEndpoint.Address.Equals(request.Args.ClientRemoteEndPoint.Address) &&
                c.LocalEndpoint.Port == request.Args.ClientRemoteEndPoint.Port);

            if (connection != null)
            {
                var (found, authRootProcessId) = await _processTracker.TryGetAuthRootAsync(connection.ProcessId);
                if (found)
                {
                    request.RequestContext.IsAuthenticated = true;
                    request.RequestContext.AuthToProxyNotes = "ProcessTreeAuth";
                }
            }

            return PluginResult.Continue;
        }

        public override Task<PluginResult> BeforeResponseAsync(PluginRequest request)
        {
            return Task.FromResult(PluginResult.Continue);
        }
    }
}

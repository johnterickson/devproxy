using System.Threading.Tasks;
using Titanium.Web.Proxy.EventArguments;

namespace DevProxy
{
    public enum ProxyAuthPluginResult
    {
        NoOpinion,
        Authenticated,
        Rejected,
    }
    
    public interface IProxyAuthPlugin
    {
        Task<(ProxyAuthPluginResult, string)> BeforeRequestAsync(SessionEventArgsBase args);
    }
}

using System.Collections.Generic;
using System.Threading.Tasks;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Http;

namespace DevProxy
{
    public enum RequestPluginResult
    {
        Continue,
        Stop
    }

    public interface IRequestPlugin
    {
        bool IsHostRelevant(string host);
        Task<RequestPluginResult> BeforeRequestAsync(SessionEventArgs args);
        Task<RequestPluginResult> BeforeResponseAsync(SessionEventArgs args);
    }

    public abstract class RequestPlugin : RequestPlugin<object> { }

    public abstract class RequestPlugin<T> : IRequestPlugin where T : class
    {
        public abstract Task<RequestPluginResult> BeforeRequestAsync(PluginRequest request);

        public Task<RequestPluginResult> BeforeRequestAsync(SessionEventArgs args)
        {
            return BeforeRequestAsync(new PluginRequest(this, args));
        }

        public abstract Task<RequestPluginResult> BeforeResponseAsync(PluginRequest request);

        public Task<RequestPluginResult> BeforeResponseAsync(SessionEventArgs args)
        {
            return BeforeResponseAsync(new PluginRequest(this, args));
        }

        public abstract bool IsHostRelevant(string host);

        public class PluginRequest
        {
            public PluginRequest(RequestPlugin<T> plugin, SessionEventArgs args)
            {
                Plugin = plugin;
                Args = args;
            }

            public readonly RequestPlugin<T> Plugin;
            public readonly SessionEventArgs Args;

            public Request Request => Args.HttpClient.Request;
            public Response Response => Args.HttpClient.Response;

            public RequestContext RequestContext => Args.GetRequestContext();

            public T Data
            {
                get
                {
                    if (this.AllData.TryGetValue(Plugin, out object v))
                    {
                        return (T)v;
                    }
                    else
                    {
                        return null;
                    }
                }
                set
                {
                    this.AllData[Plugin] = value;
                }
            }

            private Dictionary<IRequestPlugin, object> AllData => (Args.GetRequestContext()).PluginData;
        }
    }
}

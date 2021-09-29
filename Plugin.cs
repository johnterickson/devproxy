using System.Collections.Generic;
using System.Threading.Tasks;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Http;

namespace DevProxy
{
    public enum PluginResult
    {
        Continue,
        Stop
    }

    public interface IPlugin
    {
        Task<PluginResult> BeforeRequestAsync(SessionEventArgs args);
        Task<PluginResult> BeforeResponseAsync(SessionEventArgs args);
    }

    public abstract class Plugin : Plugin<object> { }

    public abstract class Plugin<T> : IPlugin where T: class
    {
        public abstract Task<PluginResult> BeforeRequestAsync(PluginRequest request);

        public Task<PluginResult> BeforeRequestAsync(SessionEventArgs args)
        {
            return BeforeRequestAsync(new PluginRequest(this, args));
        }

        public abstract Task<PluginResult> BeforeResponseAsync(PluginRequest request);

        public Task<PluginResult> BeforeResponseAsync(SessionEventArgs args)
        {
            return BeforeResponseAsync(new PluginRequest(this, args));
        }

        public class PluginRequest
        {
            public PluginRequest(Plugin<T> plugin, SessionEventArgs args)
            {
                Plugin = plugin;
                Args = args;
            }

            public readonly Plugin<T> Plugin;
            public readonly SessionEventArgs Args;

            public Request Request => Args.HttpClient.Request;
            public Response Response => Args.HttpClient.Response;

            public RequestContext RequestContext => Args.GetRequestContext();

            public T Data
            {
                get
                {
                    if(!this.AllData.TryGetValue(Plugin, out object v))
                    {
                        return (T)v;
                    }
                    else {
                        return null;
                    }
                }
                set
                {
                    this.AllData[Plugin] = value;
                }
            }

            private Dictionary<IPlugin, object> AllData => ((RequestContext)Args.UserData).PluginData;
        }
    }
}

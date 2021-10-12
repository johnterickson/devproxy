using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Models;

namespace DevProxy
{
    // Hopefully Kyle will fix this or I can shell out to his tool??
    /*
        Example tested API:
        Invoke-WebRequest -Proxy http://localhost:8888 -Uri https://vsblob.dev.azure.com/mseng/_apis/blob/blobs/1E761B9ED61FA4F3D47258FB4F4E04751FE9D01C4A5360D4F81791AA60BFFD0D00/url
    */
    public class AzureDevOpsAuthRequestPlugin : AADAuthRequestPlugin
    {
        private const string Resource = "499b84ac-1321-427f-aa17-267ca6975798/.default"; //AzDO

        private static readonly string[] HostsSuffixes = { "dev.azure.com", "visualstudio.com" };

        public AzureDevOpsAuthRequestPlugin() : base(HostsSuffixes, new[] { Resource }) { }


        protected override async Task<Token> GetAuthorizationHeaderTokenAsync(PluginRequest r)
        {
            string token = Environment.GetEnvironmentVariable("AZURE_DEVOPS_TOKEN");
            if (token != null)
            {
                return new Token("FromAZURE_DEVOPS_TOKEN", null, token, null);
            }

            return await base.GetAuthorizationHeaderTokenAsync(r);
        }
    }
}

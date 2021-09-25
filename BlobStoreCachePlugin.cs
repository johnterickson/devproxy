using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using BuildXL.Cache.ContentStore.FileSystem;
using BuildXL.Cache.ContentStore.Hashing;
using BuildXL.Cache.ContentStore.Interfaces.FileSystem;
using BuildXL.Cache.ContentStore.Interfaces.Sessions;
using BuildXL.Cache.ContentStore.Interfaces.Stores;
using BuildXL.Cache.ContentStore.Interfaces.Time;
using BuildXL.Cache.ContentStore.Interfaces.Tracing;
using BuildXL.Cache.ContentStore.Interfaces.Utils;
using BuildXL.Cache.ContentStore.Stores;
using Titanium.Web.Proxy.Models;

namespace DevProxy
{
    public class RequestInfo
    {
        public ContentHash Hash;
        public Context Context;
    }

    /*
        Example tested API:
        Invoke-WebRequest -Proxy http://localhost:8888 -Uri "Invoke-WebRequest -Proxy http://localhost:8888 -Uri "https://4fdvsblobprodwcus012.blob.core.windows.net/b-0efb4611d5654cd19a647d6cb6d7d5f0/1E761B9ED61FA4F3D47258FB4F4E04751FE9D01C4A5360D4F81791AA60BFFD0D00.blob?sv=2019-07-07&sr=b&si=1&sig=REDACTED&spr=https&se=2021-09-24T00%3A03%3A33Z&rscl=x-e2eid-36b718fd-c0064fe3-9c4a9a47-fe7890a2-session-36b718fd-c0064fe3-9c4a9a47-fe7890a2"
    */
    public class BlobStoreCachePlugin : Plugin<RequestInfo>
    {
        private static readonly Regex[] _urlRegexes = new[]
        {
            // e.g. https://4fdvsblobprodwcus012.blob.core.windows.net/b-0efb4611d5654cd19a647d6cb6d7d5f0/1E761B9ED61FA4F3D47258FB4F4E04751FE9D01C4A5360D4F81791AA60BFFD0D00.blob
            new Regex(@"https:\/\/[^\.]+\.blob\.core\.windows\.net\/.*\/([0-9a-fA-F]{64}00)\.blob?.*", RegexOptions.Compiled),
        };

        private readonly IContentStore _contentStore;
        private readonly IContentSession _contentSession;

        public BlobStoreCachePlugin()
        {
            _contentStore = new FileSystemContentStore(
                new PassThroughFileSystem(logger: null),
                SystemClock.Instance,
                new AbsolutePath(Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".devproxy",
                    "blobcache"
                )),
                new ConfigurationModel(
                    new ContentStoreConfiguration()
                ),
                distributedStore: null,
                settings: new ContentStoreSettings()
            );

            var context = new Context(null);
            _contentStore.StartupAsync(context).Wait();
            _contentSession = _contentStore.CreateSession(context, "devproxy", ImplicitPin.None).Session;
        }

        public override async Task<PluginResult> BeforeRequestAsync(PluginRequest r)
        {
            var url = new Uri(r.Args.HttpClient.Request.Url);
            Match match = _urlRegexes.Select(u => u.Match(url.AbsoluteUri)).FirstOrDefault(m => m.Success);
            if (match == null || match.Groups.Count != 2)
            {
                return PluginResult.Continue;
            }

            string hashString = match.Groups[1].Value;

            Console.WriteLine($"Found blob request {hashString}.");

            byte[] hashBytes;
            if (!HexUtilities.TryToByteArray(hashString, out hashBytes))
            {
                return PluginResult.Continue;
            }

            HashType hashType;
            switch (hashBytes.Last())
            {
                case 00:
                    hashType = HashType.Vso0;
                    break;
                default:
                    return PluginResult.Continue;
            }

            ContentHash hash = ContentHash.FromFixedBytes(hashType, new ReadOnlyFixedBytes(hashBytes, 33, 0));

            r.Data = new RequestInfo()
            {
                Hash = hash,
                Context = new Context(url.AbsoluteUri.ToString(), null)
            };

            Console.WriteLine($"Found blob request {hash.Serialize()}.");

            // TODO compression
            // TODO ranges

            var streamResult = await _contentSession.OpenStreamAsync(
                r.Data.Context,
                hash,
                CancellationToken.None);
            if (streamResult.Succeeded)
            {
                using (streamResult.Stream)
                {
                    Console.WriteLine($"Found blob in cache {hash.Serialize()}.");
                    var body = new byte[streamResult.StreamWithLength.Value.Length];
                    await streamResult.Stream.ReadAsync(body, 0, body.Length);
                    var headers = new List<HttpHeader>()
                    {
                        new HttpHeader("Content-Length", body.Length.ToString()),
                        new HttpHeader($"X-DevProxy-{this.GetType().Name}-Cache", "HIT"),
                    };
                    r.Args.GenericResponse(body, HttpStatusCode.OK, headers, closeServerConnection: false);
                }
                return PluginResult.Stop;
            }
            else
            {
                Console.WriteLine($"Blob not in cache {hash.Serialize()}.");
                return PluginResult.Continue;
            }
        }

        public override async Task<PluginResult> BeforeResponseAsync(PluginRequest r)
        {
            r.Args.HttpClient.Response.Headers.AddHeader($"X-DevProxy-{this.GetType().Name}-Cache", "MISS");

            if (r.Data != null && r.Response.StatusCode >= 200 && r.Response.StatusCode < 300)
            {
                using (var ms = new MemoryStream(await r.Args.GetResponseBody()))
                {
                    await _contentSession.PutStreamAsync(r.Data.Context, r.Data.Hash, ms, CancellationToken.None);
                }
            }

            return PluginResult.Continue;
        }
    }

}

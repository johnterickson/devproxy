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
        public bool CacheHit;
    }

    /*
        Example tested API:
        Invoke-WebRequest -Proxy http://localhost:8888 -Uri "Invoke-WebRequest -Proxy http://localhost:8888 -Uri "https://4fdvsblobprodwcus012.blob.core.windows.net/b-0efb4611d5654cd19a647d6cb6d7d5f0/1E761B9ED61FA4F3D47258FB4F4E04751FE9D01C4A5360D4F81791AA60BFFD0D00.blob?sv=2019-07-07&sr=b&si=1&sig=REDACTED&spr=https&se=2021-09-24T00%3A03%3A33Z&rscl=x-e2eid-36b718fd-c0064fe3-9c4a9a47-fe7890a2-session-36b718fd-c0064fe3-9c4a9a47-fe7890a2"
    */
    public class BlobStoreCacheRequestPlugin : RequestPlugin<RequestInfo>
    {
        private static readonly string[] HostsSuffixes = { "blob.core.windows.net", "vsblob.vsassets.io" };
        private static readonly Regex[] _urlRegexes = new[]
        {
            // e.g. https://4fdvsblobprodwcus012.blob.core.windows.net/b-0efb4611d5654cd19a647d6cb6d7d5f0/1E761B9ED61FA4F3D47258FB4F4E04751FE9D01C4A5360D4F81791AA60BFFD0D00.blob
            new Regex(@"https:\/\/[^\.]+\.blob\.core\.windows\.net\/.*\/([0-9a-fA-F]{64}00)\.blob?.*", RegexOptions.Compiled),

            // e.g. https://1yovsblobprodeus2184.vsblob.vsassets.io/b-c8701bf2aece44c9baedcf8a12ad5bd3/7661BD23B4554382BA3494CB41B96EB40CCC5B16D7DF955B530FD66544E889FA00.blob
            new Regex(@"https:\/\/[^\.]+\.vsblob\.vsassets\.io\/.*\/([0-9a-fA-F]{64}00)\.blob?.*", RegexOptions.Compiled),
        };
        
        public override bool IsHostRelevant(string host)
        {
            return HostsSuffixes.Any(h => host.EndsWith(h));
        }

        private readonly IContentStore _contentStore;
        private readonly IContentSession _contentSession;

        public BlobStoreCacheRequestPlugin()
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

        public override async Task<RequestPluginResult> BeforeRequestAsync(PluginRequest r)
        {
            var url = new Uri(r.Request.Url);

            string range = r.Request.Headers.GetFirstHeader("Range")?.Value;
            if (range != null)
            {
                // TODO: Support range requests
                return RequestPluginResult.Continue;
            }

            Match match = _urlRegexes.Select(u => u.Match(url.AbsoluteUri)).FirstOrDefault(m => m.Success);
            if (match == null || match.Groups.Count != 2)
            {
                return RequestPluginResult.Continue;
            }

            string hashString = match.Groups[1].Value;

            // Console.WriteLine($"Found blob request {hashString}.");

            byte[] hashBytes;
            if (!HexUtilities.TryToByteArray(hashString, out hashBytes))
            {
                return RequestPluginResult.Continue;
            }

            HashType hashType;
            switch (hashBytes.Last())
            {
                case 00:
                    hashType = HashType.Vso0;
                    break;
                default:
                    return RequestPluginResult.Continue;
            }

            ContentHash hash = ContentHash.FromFixedBytes(hashType, new ReadOnlyFixedBytes(hashBytes, 33, 0));

            
            r.Data = new RequestInfo()
            {
                Hash = hash,
                Context = new Context(url.AbsoluteUri.ToString(), null),
            };
            // Console.WriteLine($"Found blob request {hash.Serialize()}.");

            // TODO compression

            var streamResult = await _contentSession.OpenStreamAsync(
                r.Data.Context,
                hash,
                CancellationToken.None);

            r.Data.CacheHit = streamResult.Succeeded;

            if (streamResult.Succeeded)
            {
                using (streamResult.Stream)
                {
                    // Console.WriteLine($"Found blob in cache {hash.Serialize()}.");
                    var body = new byte[streamResult.StreamWithLength.Value.Length];
                    await streamResult.Stream.ReadAsync(body, 0, body.Length);
                    var headers = new List<HttpHeader>()
                    {
                        new HttpHeader("Content-Length", body.LongLength.ToString()),
                    };
                    r.Args.GenericResponse(body, HttpStatusCode.OK, headers, closeServerConnection: false);
                }
                return RequestPluginResult.Stop;
            }
            else
            {
                // Console.WriteLine($"Blob not in cache {hash.Serialize()}.");
                return RequestPluginResult.Continue;
            }
        }

        public override async Task<RequestPluginResult> BeforeResponseAsync(PluginRequest r)
        {
            if (r.Data == null)
            {
                return RequestPluginResult.Continue;
            }

            r.Response.Headers.AddHeader(
                $"X-DevProxy-{this.GetType().Name}-Cache",
                r.Data.CacheHit ? "HIT" : "MISS");

            if (!r.Data.CacheHit
                && r.Response.StatusCode >= 200 
                && r.Response.StatusCode < 300
                // The maximum size in any single dimension is 2,147,483,591 (0x7FFFFFC7) for byte arrays
                && r.Response.ContentLength < 0x7FFFFFC7) //
            {
                using (var ms = new MemoryStream(await r.Args.GetResponseBody()))
                {
                    await _contentSession.PutStreamAsync(r.Data.Context, r.Data.Hash, ms, CancellationToken.None);
                }
            }

            return RequestPluginResult.Continue;
        }
    }

}

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
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Models;

namespace DevProxy
{
    public class BlobStoreCachePlugin : Plugin
    {
        private static readonly Regex[] _urlRegexes = new[]
        {
            // e.g. https://4fdvsblobprodwcus012.blob.core.windows.net/b-0efb4611d5654cd19a647d6cb6d7d5f0/1E761B9ED61FA4F3D47258FB4F4E04751FE9D01C4A5360D4F81791AA60BFFD0D00.blob
            new Regex(@"https:\/\/[^\.]+\.blob\.core\.windows\.net\/.*\/([0-9a-fA-F]{64}00)\.blob?.*", RegexOptions.Compiled),
        };

        private readonly IContentStore _contentStore;
        private readonly IContentSession _contentSession;
        private object _blobRegex;

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

        public async Task<PluginResult> BeforeRequestAsync(SessionEventArgs e)
        {
            var url = new Uri(e.HttpClient.Request.Url);
            Match match = _urlRegexes.Select(u => u.Match(url.AbsoluteUri)).FirstOrDefault(m => m.Success);
            if (match == null || match.Groups.Count != 2)
            {
                return PluginResult.Continue;
            }

            string hashString = match.Groups[1].Value;

            Console.WriteLine($"Found blob request {hashString}.");

            byte[] hashBytes;
            if(!HexUtilities.TryToByteArray(hashString, out hashBytes))
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


            Console.WriteLine($"Found blob request {hash.Serialize()}.");

            var streamResult = await _contentSession.OpenStreamAsync(
                new Context(url.AbsoluteUri.ToString(), null),
                hash,
                CancellationToken.None);
            if (streamResult.Succeeded)
            {
                using(streamResult.Stream)
                {
                    Console.WriteLine($"Found blob in cache {hash.Serialize()}.");
                    var body = new byte[streamResult.StreamWithLength.Value.Length];
                    await streamResult.Stream.ReadAsync(body, 0, body.Length);
                    var headers = new List<HttpHeader>()
                    {
                        new HttpHeader("Content-Length", body.Length.ToString()),
                    };
                    e.GenericResponse(body, HttpStatusCode.OK, headers, closeServerConnection: false);
                }
                return PluginResult.Stop;
            }
            else
            {
                Console.WriteLine($"Blob not in cache {hash.Serialize()}.");
                return PluginResult.Continue;
            }
        }

        public Task<PluginResult> BeforeResponseAsync(SessionEventArgs e)
        {
            return Task.FromResult(PluginResult.Continue);
        }
    }

}

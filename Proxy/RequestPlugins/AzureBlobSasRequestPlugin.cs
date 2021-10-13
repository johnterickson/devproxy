using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Azure.Storage;
using Azure.Storage.Blobs;
using Azure.Storage.Sas;
using Microsoft.Azure.Storage;

namespace DevProxy
{
    public class AzureBlobSasRequestPlugin : IRequestPluginFactory
    {
        public IRequestPlugin Create(Dictionary<string, object> options)
        {
            string accountName;
            string host;
            string accountKey;

            string connectionString = null;
            if (options.TryGetValue("connection_string", out object connection_stringObject))
            {
                connectionString = (string)connection_stringObject;
            }
            else if (options.TryGetValue("connection_string_env_var", out object connection_string_env_varObject))
            {
                connectionString = Environment.GetEnvironmentVariable((string)connection_string_env_varObject);
            }
            else
            {
                throw new ArgumentException("Must provide connection_string or connection_string_env_var");
            }

            var connection_string = CloudStorageAccount.Parse(connectionString);
            accountName = connection_string.Credentials.AccountName;
            host = connection_string.BlobEndpoint.Host;
            accountKey = connection_string.Credentials.ExportBase64EncodedKey();
            
            return new AzureBlobSasRequestPluginInstance(
                host,
                new StorageSharedKeyCredential(accountName, accountKey)
            );
        }
    }

    public class AzureBlobSasRequestPluginInstance : RequestPlugin<string>
    {
        private readonly string host;
        private readonly StorageSharedKeyCredential key;

        public AzureBlobSasRequestPluginInstance(string host, StorageSharedKeyCredential key)
        {
            this.host = host;
            this.key = key;
        }

        public override Task<RequestPluginResult> BeforeRequestAsync(PluginRequest request)
        {
            if (null == request.Request.Headers.GetFirstHeader("Authorization"))
            {
                var existingUri = new Uri(request.Request.Url);
                var uri = new BlobUriBuilder(existingUri);

                var blobSasBuilder = new BlobSasBuilder()
                {
                    BlobContainerName = uri.BlobContainerName,
                    BlobName = uri.BlobName,
                    ExpiresOn = DateTime.UtcNow.AddMinutes(5),//Let SAS token expire after 5 minutes.
                };
                switch(request.Request.Method)
                {
                    case "HEAD":
                    case "GET":
                    case "OPTIONS":
                        blobSasBuilder.SetPermissions(BlobSasPermissions.Read);
                        break;
                    case "PUT":
                    case "POST":
                    case "PATCH":
                        blobSasBuilder.SetPermissions(BlobSasPermissions.All);
                        break;
                    default:
                        throw new NotImplementedException("don't know what to do with " + request.Request.Method);
                }
                
                var sasToken = blobSasBuilder.ToSasQueryParameters(key).ToString();
                
                if (string.IsNullOrEmpty(existingUri.Query))
                {
                    request.Request.Url += "?" + sasToken;
                }
                else
                {
                    request.Request.Url += "&" + sasToken;
                }

                request.Data = $"{blobSasBuilder.Permissions}_{blobSasBuilder.ExpiresOn.ToString("s")}";
            }

            return Task.FromResult(RequestPluginResult.Continue);
        }

        public override Task<RequestPluginResult> BeforeResponseAsync(PluginRequest request)
        {
            if (request.Data != null)
            {
                request.Response.Headers.AddHeader(
                    $"X-DevProxy-{nameof(AzureBlobSasRequestPlugin)}-SAS",
                    request.Data);
            }
            return Task.FromResult(RequestPluginResult.Continue);
        }

        public override bool IsHostRelevant(string host) => host == this.host;
    }
}

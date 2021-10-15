Instead of putting caching, auth, etc into every single tool, put it in a proxy and route the tools through it.  See also: https://github.com/zjrunner/prism

Since we don't want just anybody to be able to route traffic through this proxy and act with your auth, we need to limit access to this proxy itself.

First, _this proxy will only listen for connections coming from the local machine_.  This means that requests from other computers will be ignored - even if the firewall is opened.

Additionally, the proxy will require an additional level of auth to ensure that not just any random program on a computer can make requests through this proxy.  Example options:
 * The connection to the proxy provides a session token as proxy basic auth.
 * The connection to the proxy can be linked back to a process tree that has a trusted root process.  E.g. you start your dev environment shell and all processes launched from that shell will be able to access the proxy.


Example output of starting proxy:
```
For most apps:
  $env:http_proxy = "$(D:\src\DevProxy\Proxy\bin\Release\net5.0\DevProxy.exe --get_proxy)"
For windows git (via config):
  git config --add http.sslcainfo C:/Users/jerick/.devproxy/certs/root.devproxy.pem
For windows git (via env var):
  GIT_PROXY_SSL_CAINFO=C:/Users/jerick/.devproxy/certs/root.devproxy.pem
For WSL2 (Ubuntu tested):
  1. Once, install root cert
       sudo apt install ca-certificates
       sudo cp /mnt/c/Users/jerick/.devproxy/certs/root.devproxy.pem /etc/ssl/certs/devproxy.pem
       sudo update-ca-certificates --verbose --fresh | grep -i devproxy
  2. Set envvars to enable (add this to .bashrc)
       export http_proxy=$(/mnt/d/src/DevProxy/Proxy/bin/Release/net5.0/DevProxy.exe --get_wsl_proxy)
       export https_proxy=$http_proxy
       export NODE_EXTRA_CA_CERTS=/etc/ssl/certs/devproxy.pem
```

Example using proxy from powershell:
```
PS C:\Users\jerick> $env:HTTPS_PROXY = "http://$(D:\src\DevProxy\bin\Debug\net5.0\win10-x64\publish\DevProxy.exe --get_token)@localhost:8888"
PS C:\Users\jerick> curl.exe --ssl-no-revoke https://dev.azure.com/mseng  -D headers.txt -o out.txt; findstr DevProxy headers.txt
X-DevProxy-AuthToProxy-ProxyPasswordAuthPlugin: Authenticated_UserMatch_SHA512=5034090E
X-DevProxy-AzureDevOpsAuthPlugin-TokenType: WindowsIntegratedAuth
X-DevProxy-AzureDevOpsAuthPlugin-TokenSHA512: 12C88899
```

Example using proxy from WSL2:
```
john@jerick-hpz440:~$ export HTTP_PROXY=http://$(/mnt/d/src/DevProxy/bin/Debug/net5.0/win10-x64/publish/DevProxy.exe --get_token)@172.28.160.1:8888
john@jerick-hpz440:~$ export HTTPS_PROXY=$HTTP_PROXY
john@jerick-hpz440:~$ curl https://dev.azure.com/mseng -D headers.txt -o body.txt && grep DevProxy headers.txt
X-DevProxy-AuthToProxy-ProxyPasswordAuthPlugin: Authenticated_UserMatch_SHA512=5034090E
X-DevProxy-AzureDevOpsAuthPlugin-TokenType: WindowsIntegratedAuth
X-DevProxy-AzureDevOpsAuthPlugin-TokenSHA512: F22C0ACA
```

Example of automatically creating minimal SAS signatures for each request (set `STORAGE_CONNECTION_STRING` before starting proxy):
```
john@jerick-hpz440:~$ curl -v -X PUT -d "From DevProxy: Hello, Azure Storage! $(date)"$'\n' -H "x-ms-blob-type: BlockBlob" https://jerickbuildcache.blob.core.windows.net/devproxytest/hello.txt 2>&1 | grep DevProxy
*  issuer: CN=DevProxy for jerick
< X-DevProxy-AuthToProxy-AuthorizationHeaderProxyAuthPlugin: NoOpinion_NoHeader=Authorization
< X-DevProxy-AuthToProxy-ProxyAuthorizationHeaderProxyAuthPluginInstance: Authenticated_PasswordMatch_SHA512=07A6974FB90B91F566F502033A4B4BA7
< X-DevProxy-AzureBlobSasRequestPlugin-SAS: racwdxltmei_2021-10-13T20:45:44
john@jerick-hpz440:~$ curl -v https://jerickbuildcache.blob.core.windows.net/devproxytest/hello.txt 2>&1 | grep DevProxy
*  issuer: CN=DevProxy for jerick
< X-DevProxy-AuthToProxy-AuthorizationHeaderProxyAuthPlugin: NoOpinion_NoHeader=Authorization
< X-DevProxy-AuthToProxy-ProxyAuthorizationHeaderProxyAuthPluginInstance: Authenticated_PasswordMatch_SHA512=07A6974FB90B91F566F502033A4B4BA7
< X-DevProxy-AzureBlobSasRequestPlugin-SAS: r_2021-10-13T20:46:01
From DevProxy: Hello, Azure Storage! Wed Oct 13 13:40:44 PDT 2021
```
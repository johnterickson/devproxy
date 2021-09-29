Instead of putting caching, auth, etc into every single tool, put it in a proxy and route the tools through it.  See also: https://github.com/zjrunner/prism

Since we don't want just anybody to be able to route traffic through this proxy and act with your auth, we need to limit access to this proxy itself.

First, _this proxy will only listen for connections coming from the local machine_.  This means that requests from other computers will be ignored - even if the firewall is opened.

Additionally, the proxy will require an additional level of auth to ensure that not just any random program on a computer can make requests through this proxy.  Example options:
 * The connection to the proxy provides a session token as proxy basic auth.
 * The connection to the proxy can be linked back to a process tree that has a trusted root process.  E.g. you start your dev environment shell and all processes launched from that shell will be able to access the proxy.


Examples:
Invoke-WebRequest -Proxy http://localhost:8888 -Uri ((ConvertFrom-Json (Invoke-WebRequest -Proxy http://localhost:8888 -Uri https://vsblob.dev.azure.com/mseng/_apis/blob/blobs/1E761B9ED61FA4F3D47258FB4F4E04751FE9D01C4A5360D4F81791AA60BFFD0D00/url).Content).url)
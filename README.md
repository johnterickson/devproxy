Instead of putting caching, auth, etc into every single tool, put it in a proxy and route the tools through it.

See also: https://github.com/zjrunner/prism

Examples:
Invoke-WebRequest -Proxy http://localhost:8888 -Uri ((ConvertFrom-Json (Invoke-WebRequest -Proxy http://localhost:8888 -Uri https://vsblob.dev.azure.com/mseng/_apis/blob/blobs/1E761B9ED61FA4F3D47258FB4F4E04751FE9D01C4A5360D4F81791AA60BFFD0D00/url).Content).url)
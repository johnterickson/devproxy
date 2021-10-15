using System;
using System.Text.RegularExpressions;

namespace DevProxy
{
    public sealed class UrlFilter
    {
        public static readonly UrlFilter All = new UrlFilter(null, null);
        public readonly Regex Host;
        public readonly Regex Path;

        public UrlFilter(Config config) : this (config.host_regex, config.path_regex)
        {
        }

        public UrlFilter(string hostRegex, string pathRegex)
        {
            if (!string.IsNullOrWhiteSpace(hostRegex))
            {
                Host = new Regex(hostRegex, RegexOptions.Compiled);
            }
            if (!string.IsNullOrWhiteSpace(pathRegex) && pathRegex != ".*")
            {
                Path = new Regex(pathRegex, RegexOptions.Compiled);
            }
        }

        public bool IsHostMatch(string host)
        {
            return this.Host == null || this.Host.IsMatch(host);
        }

        public bool IsPathMatch(string path)
        {
            return this.Path == null || this.Path.IsMatch(path);
        }

        public bool IsAnyHostMatch => this.Host == null;
        public bool IsAnyPathMatch => this.Path == null;

        public bool IsUriMatch(Uri u)
        {
            return this.IsHostMatch(u.Host) && this.IsPathMatch(u.PathAndQuery);
        }

        public override string ToString() =>
            $"{this.Host?.ToString() ?? ".*"}{this.Path?.ToString() ?? ".*"}"; 
        
        public sealed class Config
        {
            public string host_regex {get;set;}
            public string path_regex {get;set;}
        }
    }
}

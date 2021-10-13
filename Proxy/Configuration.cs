using System.Collections.Generic;

namespace DevProxy
{
    public class Configuration
    {
        public ProxyConfiguration proxy { get; set; } = new ProxyConfiguration();
        public List<PluginConfiguration> plugins { get; set; } = new List<PluginConfiguration>();
    }

    public class PluginConfiguration
    {
        public string class_name { get; set; }
        public Dictionary<string, object> options { get; set; } = new Dictionary<string, object>();
    }

    public class FixedPasswordConfiguration
    {
        public string value {get;set;}
    }

    public class RotatingPasswordConfiguration
    {
        public string base_secret {get;set;}
        public int? passwords_lifetime_seconds {get;set;}
        public int? generate_new_every_seconds {get;set;}
    }

    public class ProxyConfiguration
    {
        public string ipcPipeName {get;set;}

        public bool? listen_to_wsl2 {get;set;}

        public int? port { get; set; }
        public string upstream_http_proxy { get; set; }
        public string upstream_https_proxy { get; set; }
        public int? max_cached_connections_per_host { get; set; }
        public bool? log_requests { get; set; }

        public string password_type {get; set; }
        public FixedPasswordConfiguration fixed_password {get;set;}
        public RotatingPasswordConfiguration rotating_password {get;set;}
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace DevProxy
{
    public sealed class RotatingPassword : IProxyPassword
    {
        private sealed class Password
        {
            public readonly DateTimeOffset Expiry;
            public readonly string Value;

            public Password(string value, DateTimeOffset expiry)
            {
                Value = value;
                Expiry = expiry;
            }
        }

        private readonly Timer _timer;
        private readonly string _baseSecret;
        private readonly int _rotationRateSeconds;
        private readonly TimeSpan _maxDuration;
        private Queue<Password> _proxyPasswords = new Queue<Password>();

        private long _currentBaseline = DateTimeOffset.MinValue.ToUnixTimeSeconds();

        public RotatingPassword(TimeSpan maxDuration, string baseSecret, TimeSpan rotationRate)
        {
            _maxDuration = maxDuration;
            _baseSecret = baseSecret;
            _rotationRateSeconds = (int)rotationRate.TotalSeconds;
            DateTimeOffset now = DateTimeOffset.UtcNow;
            for(DateTimeOffset start = now - maxDuration; start < now; start += rotationRate)
            {
                Update(start);
            }
            Update(now);
            _timer = new Timer(
                _ => Update(DateTimeOffset.UtcNow),
                null,
                1000*_rotationRateSeconds,
                1000*_rotationRateSeconds);
        }

        public void Dispose()
        {
            _timer.Dispose();
        }

        private void Update(DateTimeOffset when)
        {
            long seconds = when.ToUnixTimeSeconds();
            seconds /= _rotationRateSeconds;
            seconds *= _rotationRateSeconds;
            DateTimeOffset baseTime = DateTimeOffset.FromUnixTimeSeconds(seconds);
            lock (_proxyPasswords)
            {
                var password = new Password(
                    HasherHelper.HashSecret(_baseSecret + baseTime.ToString("O")),
                    when + _maxDuration);
                _proxyPasswords.Enqueue(password);
            }
        }

        public string GetCurrent()
        {
            lock(_proxyPasswords)
            {
                return _proxyPasswords.Last().Value;
            }
        }

        public bool Check(string password)
        {
            lock(_proxyPasswords)
            {
                return _proxyPasswords.Reverse().Any(p => p.Value.Equals(password, StringComparison.Ordinal));
            }
        }
    }
}

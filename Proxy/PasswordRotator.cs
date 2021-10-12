using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace DevProxy
{
    public sealed class PasswordRotator : IDisposable
    {
        private readonly Timer _timer;
        private readonly string _baseSecret;
        private readonly int _rotationRateSeconds;
        private Queue<string> _proxyPasswords = new Queue<string>();

        private long _currentBaseline = DateTimeOffset.MinValue.ToUnixTimeSeconds();

        public PasswordRotator(string baseSecret, TimeSpan rotationRate)
        {
            _baseSecret = baseSecret;
            _rotationRateSeconds = (int)rotationRate.TotalSeconds;
            Update();
            _timer = new Timer(
                _ => Update(),
                null,
                1000*_rotationRateSeconds,
                1000*_rotationRateSeconds);
        }

        public void Dispose()
        {
            _timer.Dispose();
        }

        private void Update()
        {
            long seconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            seconds /= _rotationRateSeconds;
            seconds *= _rotationRateSeconds;
            DateTimeOffset baseTime = DateTimeOffset.FromUnixTimeSeconds(seconds);
            lock (_proxyPasswords)
            {
                _proxyPasswords.Enqueue(HasherHelper.HashSecret(_baseSecret + baseTime.ToString("O")));
                while(_proxyPasswords.Count > 2)
                {
                    _proxyPasswords.Dequeue();
                }
            }
        }

        public string GetCurrent()
        {
            lock(_proxyPasswords)
            {
                return _proxyPasswords.Last();
            }
        }

        public bool Check(string password)
        {
            lock(_proxyPasswords)
            {
                return _proxyPasswords.Any(p => p.Equals(password, StringComparison.Ordinal));
            }
        }
    }
}

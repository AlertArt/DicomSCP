using System.Collections.Concurrent;

namespace DicomSCP.Services;

/// <summary>
/// 登录失败锁定器（进程内、线程安全）。
/// 同时按账号与来源 IP 计数：任一维度超过阈值即临时锁定，抵御口令爆破。
/// 成功登录只清除账号维度；IP 维度不清零，避免攻击者用有效口令复位限流。
/// </summary>
public sealed class LoginAttemptLimiter
{
    private sealed class Entry
    {
        public int Failures;
        public DateTime? LockedUntilUtc;
        public DateTime LastTouchedUtc = DateTime.UtcNow;
    }

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly int _maxAttempts;
    private readonly int _maxAttemptsPerIp;
    private readonly TimeSpan _lockout;
    private int _registrationsSincePrune;

    public LoginAttemptLimiter(IConfiguration configuration)
    {
        _maxAttempts = Math.Max(1, configuration.GetValue("Auth:MaxLoginAttempts", 5));
        _maxAttemptsPerIp = Math.Max(_maxAttempts, configuration.GetValue("Auth:MaxLoginAttemptsPerIp", 20));
        _lockout = TimeSpan.FromMinutes(Math.Max(1, configuration.GetValue("Auth:LockoutMinutes", 15)));
    }

    public static string UserKey(string username) => "u:" + username.Trim().ToLowerInvariant();

    public static string IpKey(string ip) => "ip:" + ip;

    /// <summary>判断键是否处于锁定期；过期锁会被自动清除。</summary>
    public bool IsLocked(string key, out TimeSpan retryAfter)
    {
        retryAfter = TimeSpan.Zero;
        if (!_entries.TryGetValue(key, out var entry))
        {
            return false;
        }

        lock (entry)
        {
            if (entry.LockedUntilUtc is { } until)
            {
                var now = DateTime.UtcNow;
                if (until > now)
                {
                    retryAfter = until - now;
                    return true;
                }

                entry.LockedUntilUtc = null;
                entry.Failures = 0;
            }
        }

        return false;
    }

    /// <summary>记录一次失败：账号与 IP 两个维度分别累计。</summary>
    public void RegisterFailure(string userKey, string ipKey)
    {
        Register(userKey, _maxAttempts);
        Register(ipKey, _maxAttemptsPerIp);
        MaybePrune();
    }

    /// <summary>成功登录后清除账号失败计数。</summary>
    public void Reset(string key) => _entries.TryRemove(key, out _);

    private void Register(string key, int threshold)
    {
        var entry = _entries.GetOrAdd(key, _ => new Entry());
        lock (entry)
        {
            entry.LastTouchedUtc = DateTime.UtcNow;
            entry.Failures++;
            if (entry.Failures >= threshold)
            {
                entry.LockedUntilUtc = DateTime.UtcNow.Add(_lockout);
                entry.Failures = 0;
            }
        }
    }

    private void MaybePrune()
    {
        if (Interlocked.Increment(ref _registrationsSincePrune) < 100)
        {
            return;
        }

        Interlocked.Exchange(ref _registrationsSincePrune, 0);
        var cutoff = DateTime.UtcNow.AddHours(-1);
        foreach (var pair in _entries)
        {
            var entry = pair.Value;
            lock (entry)
            {
                var unlocked = entry.LockedUntilUtc is null || entry.LockedUntilUtc <= DateTime.UtcNow;
                if (unlocked && entry.LastTouchedUtc < cutoff)
                {
                    _entries.TryRemove(pair.Key, out _);
                }
            }
        }
    }
}

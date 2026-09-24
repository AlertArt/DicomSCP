using System.Collections.Concurrent;

namespace DicomSCP.Services;

/// <summary>
/// Weasis 外部客户端一次性访问令牌：绑定研究、短时有效。
/// 外部进程无法携带会话 Cookie，凭令牌访问 manifest 与 /wado。
/// </summary>
public sealed class WeasisTokenService
{
    private sealed record Entry(string StudyUid, DateTime ExpiresAtUtc);

    private readonly ConcurrentDictionary<string, Entry> _tokens = new();
    private readonly TimeSpan _ttl = TimeSpan.FromMinutes(10);

    public TimeSpan Ttl => _ttl;

    /// <summary>为指定研究签发令牌。</summary>
    public string Issue(string studyInstanceUid)
    {
        PurgeExpired();
        var token = Guid.NewGuid().ToString("N");
        _tokens[token] = new Entry(studyInstanceUid, DateTime.UtcNow.Add(_ttl));
        return token;
    }

    /// <summary>校验令牌：未过期，且（若给定研究）研究匹配。</summary>
    public bool Validate(string? token, string? studyInstanceUid)
    {
        if (string.IsNullOrEmpty(token) || !_tokens.TryGetValue(token, out var entry))
        {
            return false;
        }

        if (entry.ExpiresAtUtc <= DateTime.UtcNow)
        {
            _tokens.TryRemove(token, out _);
            return false;
        }

        return string.IsNullOrEmpty(studyInstanceUid)
            || string.Equals(entry.StudyUid, studyInstanceUid, StringComparison.Ordinal);
    }

    private void PurgeExpired()
    {
        var now = DateTime.UtcNow;
        foreach (var kv in _tokens)
        {
            if (kv.Value.ExpiresAtUtc <= now)
            {
                _tokens.TryRemove(kv.Key, out _);
            }
        }
    }
}

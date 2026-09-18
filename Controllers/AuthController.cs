using Microsoft.AspNetCore.Mvc;
using DicomSCP.Repository;
using DicomSCP.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using System.Security.Claims;

namespace DicomSCP.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController(UserRepository repository, LoginAttemptLimiter limiter) : ControllerBase
{
    private readonly UserRepository _repository = repository;
    private readonly LoginAttemptLimiter _limiter = limiter;

    private const int MinPasswordLength = 8;

    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        var username = request.Username?.Trim() ?? string.Empty;
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var userKey = LoginAttemptLimiter.UserKey(username);
        var ipKey = LoginAttemptLimiter.IpKey(ip);

        // 任一维度处于锁定窗口内即拒绝，避免口令爆破
        var lockedByUser = _limiter.IsLocked(userKey, out var userRetry);
        var lockedByIp = _limiter.IsLocked(ipKey, out var ipRetry);
        if (lockedByUser || lockedByIp)
        {
            var retryAfter = userRetry > ipRetry ? userRetry : ipRetry;
            Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString();
            DicomLogger.Warning("Api", "[API] 登录被限流 - 用户名: {Username}, IP: {Ip}", username, ip);
            return StatusCode(StatusCodes.Status429TooManyRequests, "登录尝试过于频繁，请稍后再试");
        }

        var isValid = await _repository.ValidateUserAsync(username, request.Password);
        if (!isValid)
        {
            _limiter.RegisterFailure(userKey, ipKey);
            DicomLogger.Warning("Api", "[API] 登录失败 - 用户名: {Username}, IP: {Ip}", username, ip);
            return Unauthorized("用户名或密码错误");
        }

        _limiter.Reset(userKey);

        var mustChangePassword = await _repository.IsPasswordChangeRequiredAsync(username);

        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, username),
            new("must_change_pwd", mustChangePassword ? "1" : "0")
        };

        await HttpContext.SignInAsync(
            "CustomAuth",
            new ClaimsPrincipal(new ClaimsIdentity(claims, "CustomAuth")),
            new AuthenticationProperties
            {
                IsPersistent = true,
                IssuedUtc = DateTimeOffset.UtcNow,
                AllowRefresh = true
            });

        return Ok(new { mustChangePassword });
    }

    [Authorize]
    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        try
        {
            var username = User.Identity?.Name ?? "unknown";
            await HttpContext.SignOutAsync("CustomAuth");
            
            DicomLogger.Information("Api", "[API] 用户登出 - 用户名: {Username}", username);
            return Ok();
        }
        catch (Exception ex)
        {
            DicomLogger.Error("Api", ex, "[API] 登出异常");
            return StatusCode(500, "登出失败");
        }
    }

    [Authorize]
    [HttpPost("change-password")]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest request)
    {
        try
        {
            var username = User.Identity?.Name;
            if (string.IsNullOrEmpty(username))
            {
                DicomLogger.Warning("Api", "[API] 修改密码失败 - 原因: 未登录");
                return Unauthorized("未登录");
            }

            // 验证旧密码
            var isValid = await _repository.ValidateUserAsync(username, request.OldPassword);
            if (!isValid)
            {
                DicomLogger.Warning("Api", "[API] 修改密码失败 - 用户名: {Username}, 原因: 旧密码错误", username);
                return BadRequest("旧密码错误");
            }

            // 校验新口令强度
            var newPassword = request.NewPassword ?? string.Empty;
            if (newPassword.Length < MinPasswordLength)
            {
                return BadRequest($"新密码长度不能少于{MinPasswordLength}位");
            }

            if (string.Equals(newPassword, request.OldPassword, StringComparison.Ordinal))
            {
                return BadRequest("新密码不能与旧密码相同");
            }

            // 修改密码
            await _repository.ChangePasswordAsync(username, newPassword);

            // 清除登录状态
            await HttpContext.SignOutAsync("CustomAuth");
            
            DicomLogger.Information("Api", "[API] 修改密码成功 - 用户名: {Username}", username);
            return Ok();
        }
        catch (Exception ex)
        {
            var username = User.Identity?.Name ?? "unknown";
            DicomLogger.Error("Api", ex, "[API] 修改密码异常 - 用户名: {Username}", username);
            return StatusCode(500, "修改密码失败");
        }
    }

    [Authorize]
    [HttpGet("check-session")]
    public IActionResult CheckSession()
    {
        // 由于中间件已经处理了认证，这里只需要返回用户信息
        var mustChangePassword = User.FindFirst("must_change_pwd")?.Value == "1";
        return Ok(new { username = User.Identity?.Name, mustChangePassword });
    }
}

public class LoginRequest
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

public class ChangePasswordRequest
{
    public string OldPassword { get; set; } = string.Empty;
    public string NewPassword { get; set; } = string.Empty;
} 
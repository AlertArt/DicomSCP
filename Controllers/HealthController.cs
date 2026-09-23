using DicomSCP.Services;
using Microsoft.AspNetCore.Mvc;

namespace DicomSCP.Controllers;

/// <summary>
/// 健康检查端点（/health，不鉴权、无 PHI），供负载均衡/CI 探活。
/// </summary>
[ApiController]
[Route("health")]
public class HealthController : ControllerBase
{
    [HttpGet]
    public IActionResult Get() => Ok(new
    {
        status = "Healthy",
        uptimeSeconds = (long)DicomMetrics.Uptime.TotalSeconds,
        time = DateTime.UtcNow
    });
}

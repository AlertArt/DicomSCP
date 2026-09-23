using System.Text;
using DicomSCP.Services;
using Microsoft.AspNetCore.Mvc;

namespace DicomSCP.Controllers;

/// <summary>
/// 运行指标导出（Prometheus 文本格式）。路由位于 /api/Metrics，受全局 /api/ 认证保护。
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class MetricsController : ControllerBase
{
    /// <summary>Prometheus 文本格式指标。</summary>
    [HttpGet]
    public IActionResult Get()
    {
        var sb = new StringBuilder();
        foreach (var (name, value) in DicomMetrics.Counters)
        {
            sb.Append("# TYPE ").Append(name).AppendLine(" counter");
            sb.Append(name).Append(' ').AppendLine(value.ToString());
        }
        foreach (var (name, value) in DicomMetrics.Gauges)
        {
            sb.Append("# TYPE ").Append(name).AppendLine(" gauge");
            sb.Append(name).Append(' ').AppendLine(value.ToString());
        }
        return Content(sb.ToString(), "text/plain; version=0.0.4; charset=utf-8");
    }

    /// <summary>指标 JSON 摘要。</summary>
    [HttpGet("summary")]
    public IActionResult Summary() => Ok(new
    {
        counters = DicomMetrics.Counters,
        gauges = DicomMetrics.Gauges,
        uptimeSeconds = (long)DicomMetrics.Uptime.TotalSeconds
    });
}

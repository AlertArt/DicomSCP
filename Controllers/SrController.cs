using DicomSCP.Repository;
using DicomSCP.Services;
using Microsoft.AspNetCore.Mvc;

namespace DicomSCP.Controllers;

/// <summary>
/// 结构化报告(SR)关联查询：报告引用的图像、以及引用某图像的报告。
/// 路由位于 /api/Sr，受全局 /api/ 认证中间件保护。
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class SrController : ControllerBase
{
    private readonly DicomRepository _repository;

    public SrController(DicomRepository repository)
    {
        _repository = repository;
    }

    /// <summary>查询该 SR 报告引用的图像/对象（证据链）。</summary>
    [HttpGet("{sopInstanceUid}/references")]
    public IActionResult GetReferences(string sopInstanceUid)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(sopInstanceUid))
            {
                return BadRequest("sopInstanceUid is required");
            }

            var references = _repository.GetSrReferences(sopInstanceUid, throwOnError: true);
            return Ok(references);
        }
        catch (Exception ex)
        {
            DicomLogger.Error("Sr", ex, "查询 SR 引用失败 - SR: {Sop}", sopInstanceUid);
            return StatusCode(500, "查询 SR 引用失败");
        }
    }

    /// <summary>反查引用了指定实例的所有 SR 报告。</summary>
    [HttpGet("referencing/{sopInstanceUid}")]
    public IActionResult GetReferencing(string sopInstanceUid)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(sopInstanceUid))
            {
                return BadRequest("sopInstanceUid is required");
            }

            var reports = _repository.GetSrsReferencing(sopInstanceUid, throwOnError: true);
            return Ok(reports);
        }
        catch (Exception ex)
        {
            DicomLogger.Error("Sr", ex, "反查 SR 失败 - 实例: {Sop}", sopInstanceUid);
            return StatusCode(500, "反查 SR 失败");
        }
    }
}

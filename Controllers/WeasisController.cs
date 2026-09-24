using DicomSCP.Services;
using Microsoft.AspNetCore.Mvc;

namespace DicomSCP.Controllers;

/// <summary>
/// Weasis 外部客户端接入：签发绑定研究的一次性令牌，供其免会话访问 manifest 与 /wado。
/// 路由位于 /api/Weasis，受全局 /api/ 认证保护（签发需登录）。
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class WeasisController : ControllerBase
{
    private readonly WeasisTokenService _tokens;

    public WeasisController(WeasisTokenService tokens)
    {
        _tokens = tokens;
    }

    [HttpGet("token")]
    public IActionResult IssueToken([FromQuery] string studyInstanceUid)
    {
        if (string.IsNullOrWhiteSpace(studyInstanceUid))
        {
            return BadRequest("studyInstanceUid is required");
        }

        var token = _tokens.Issue(studyInstanceUid);
        var baseUrl = $"{Request.Scheme}://{Request.Host}";
        var manifestUrl = $"{baseUrl}/viewer/weasis/{studyInstanceUid}?token={token}";
        var weasisUrl = $"weasis://?$dicom:get -w \"{manifestUrl}\"";

        return Ok(new
        {
            token,
            expiresInSeconds = (int)_tokens.Ttl.TotalSeconds,
            manifestUrl,
            weasisUrl
        });
    }
}

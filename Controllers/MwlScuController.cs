using Microsoft.AspNetCore.Mvc;
using DicomSCP.Models;
using DicomSCP.Services;
using Microsoft.Extensions.Options;
using DicomSCP.Configuration;

namespace DicomSCP.Controllers;

[ApiController]
[Route("api/[controller]")]
public class MwlScuController(IMwlScu mwlScu, IOptions<QueryRetrieveConfig> config) : ControllerBase
{
    private readonly IMwlScu _mwlScu = mwlScu;
    private readonly QueryRetrieveConfig _config = config.Value;

    [HttpGet("nodes")]
    public ActionResult<IEnumerable<RemoteNode>> GetNodes()
    {
        return Ok(_config.RemoteNodes.Where(n =>
            n.Type.ToLower() is "worklist" or "wl" or "all"));
    }

    [HttpPost("verify/{remoteName}")]
    public async Task<IActionResult> VerifyConnection(string remoteName)
    {
        try
        {
            var result = await _mwlScu.VerifyConnectionAsync(remoteName);
            return Ok(new { success = result });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
        catch (Exception ex)
        {
            DicomLogger.Error("Api", ex, "验证 MWL 连接失败");
            return StatusCode(500, "验证连接失败");
        }
    }

    [HttpPost("query/{remoteName}")]
    public async Task<IActionResult> Query(string remoteName, [FromBody] MwlQueryCriteria? criteria)
    {
        try
        {
            if (criteria == null)
            {
                criteria = new MwlQueryCriteria();
            }

            DicomLogger.Information("Api", "开始 MWL 查询 - 节点: {RemoteName}", remoteName);
            var items = await _mwlScu.QueryAsync(remoteName, criteria);
            return Ok(items);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
        catch (Exception ex)
        {
            DicomLogger.Error("Api", ex, "MWL 查询失败 - 节点: {RemoteName}", remoteName);
            return StatusCode(500, $"MWL 查询失败: {ex.Message}");
        }
    }
}
using DicomSCP.Configuration;
using DicomSCP.Models;
using DicomSCP.Repository;
using DicomSCP.Services;
using FellowOakDicom;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace DicomSCP.Controllers;

/// <summary>
/// 结构化报告(SR)服务：报告生成、报告引用的图像、以及引用某图像的报告。
/// 路由位于 /api/Sr，受全局 /api/ 认证中间件保护。
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class SrController : ControllerBase
{
    private readonly DicomRepository _repository;
    private readonly DicomDatasetPersistence _persistence;
    private readonly DicomSettings _settings;

    public SrController(
        DicomRepository repository,
        DicomDatasetPersistence persistence,
        IOptions<DicomSettings> settings)
    {
        _repository = repository;
        _persistence = persistence;
        _settings = settings.Value;
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

    /// <summary>列出某研究下的关键对象选择文档(KOS)。</summary>
    [HttpGet("key-objects")]
    public IActionResult GetKeyObjects([FromQuery] string studyInstanceUid)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(studyInstanceUid))
            {
                return BadRequest("studyInstanceUid is required");
            }

            var instances = _repository.GetInstancesBySopClass(
                studyInstanceUid, SrSupport.KeyObjectSelectionSopClassUid, throwOnError: true);

            var result = instances.Select(i => new SrKeyObjectInfo
            {
                SopInstanceUid = i.SopInstanceUid,
                StudyInstanceUid = i.StudyInstanceUid,
                SeriesInstanceUid = i.SeriesInstanceUid,
                DocumentTitle = i.DocumentTitle,
                CompletionFlag = i.CompletionFlag,
                VerificationFlag = i.VerificationFlag,
                ConceptCodeValue = i.ConceptCodeValue,
                ConceptCodeMeaning = i.ConceptCodeMeaning
            }).ToList();

            return Ok(result);
        }
        catch (Exception ex)
        {
            DicomLogger.Error("Sr", ex, "查询 KOS 失败 - Study: {Study}", studyInstanceUid);
            return StatusCode(500, "查询 KOS 失败");
        }
    }

    /// <summary>读取该 SR 报告的内容树（报告头 + 内容项），供查看器呈现。</summary>
    [HttpGet("{sopInstanceUid}/content")]
    public async Task<IActionResult> GetContent(string sopInstanceUid)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(sopInstanceUid))
            {
                return BadRequest("sopInstanceUid is required");
            }

            var dbInstance = await _repository.GetInstanceAsync(sopInstanceUid);
            if (dbInstance == null)
            {
                return NotFound("Instance not found");
            }

            if (!SrSupport.IsStructuredReport(dbInstance.SopClassUid))
            {
                return BadRequest("Instance is not a structured report");
            }

            var filePath = Path.Combine(_settings.StoragePath ?? string.Empty, dbInstance.FilePath);
            if (!System.IO.File.Exists(filePath))
            {
                return NotFound("DICOM file not found");
            }

            var dicomFile = await DicomFile.OpenAsync(filePath);
            var content = SrSupport.ExtractDocumentContent(dicomFile.Dataset);
            return Ok(content);
        }
        catch (Exception ex)
        {
            DicomLogger.Error("Sr", ex, "读取 SR 内容失败 - SR: {Sop}", sopInstanceUid);
            return StatusCode(500, "读取 SR 内容失败");
        }
    }

    /// <summary>从 REST 输入生成 Basic Text SR，归档并立即入库，返回生成的 UID。</summary>
    [HttpPost("generate")]
    public async Task<IActionResult> Generate([FromBody] SrGenerationRequest request)
    {
        try
        {
            if (request == null || string.IsNullOrWhiteSpace(request.ContentText))
            {
                return BadRequest("contentText is required");
            }

            if (string.IsNullOrWhiteSpace(_settings.StoragePath))
            {
                return StatusCode(500, "StoragePath is not configured");
            }

            var dataset = SrGenerator.BuildBasicTextSr(request, DateTime.Now);

            var studyUid = dataset.GetSingleValue<string>(DicomTag.StudyInstanceUID);
            var seriesUid = dataset.GetSingleValue<string>(DicomTag.SeriesInstanceUID);
            var sopUid = dataset.GetSingleValue<string>(DicomTag.SOPInstanceUID);
            var studyDate = dataset.GetSingleValueOrDefault<string>(DicomTag.StudyDate, DateTime.Now.ToString("yyyyMMdd"));

            var relativePath = Path.Combine(
                studyDate[..4], studyDate.Substring(4, 2), studyDate.Substring(6, 2),
                studyUid, seriesUid, $"{sopUid}.dcm");
            var fullPath = Path.Combine(_settings.StoragePath, relativePath);
            var directory = Path.GetDirectoryName(fullPath);
            if (string.IsNullOrEmpty(directory))
            {
                return StatusCode(500, "Invalid storage path structure");
            }
            Directory.CreateDirectory(directory);

            await new DicomFile(dataset).SaveAsync(fullPath);

            // 立即入库，保证生成后可被 QIDO/C-FIND 检索到
            await _persistence.SaveDicomDataImmediateAsync(dataset, relativePath);

            DicomLogger.Information("Sr", "SR 生成完成 - SOP: {Sop}, Study: {Study}, 路径: {Path}",
                sopUid, studyUid, relativePath);

            return Ok(new SrGenerationResult
            {
                SopInstanceUid = sopUid,
                StudyInstanceUid = studyUid,
                SeriesInstanceUid = seriesUid,
                FilePath = relativePath
            });
        }
        catch (Exception ex)
        {
            DicomLogger.Error("Sr", ex, "生成 SR 失败");
            return StatusCode(500, "生成 SR 失败");
        }
    }
}

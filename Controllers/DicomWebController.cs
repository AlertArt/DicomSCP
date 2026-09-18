using DicomSCP.Configuration;
using DicomSCP.Models;
using DicomSCP.Repository;
using DicomSCP.Services;
using FellowOakDicom;
using FellowOakDicom.Imaging;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using System.Text;

namespace DicomSCP.Controllers;

/// <summary>
/// DICOMweb RESTful 服务：WADO-RS（检索）、QIDO-RS（查询）、STOW-RS（存储）。
/// 路由统一位于 /dicomweb 下，参照 PS3.18 标准实现。
/// </summary>
[Route("dicomweb")]
[ApiController]
public class DicomWebController(
    DicomRepository dicomRepository,
    DicomDatasetPersistence persistence,
    IOptions<DicomSettings> settings) : ControllerBase
{
    private readonly DicomRepository _repository = dicomRepository;
    private readonly DicomDatasetPersistence _persistence = persistence;
    private readonly DicomSettings _settings = settings.Value;

    // ── QIDO-RS 查询 ──────────────────────────────────────────────────────

    [HttpGet("studies")]
    public async Task<IActionResult> QidoSearchStudies([FromQuery] string encoding = "")
    {
        try
        {
            var (matches, fuzzy, offset, limit) = ParseQidoQuery(Request);

            var studies = await Task.Run(() => _repository.QidoQueryStudies(matches, fuzzy, offset, limit, throwOnError: true));
            var json = "[" + string.Join(",", studies.Select(s => DicomWebHelpers.ToDicomJson(DicomWebHelpers.BuildStudyDataset(s)))) + "]";
            return Content(json, DicomWebHelpers.JsonContentType, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            DicomLogger.Error("DICOMweb", ex, "QIDO-RS 查询研究失败");
            return StatusCode(500, "QIDO-RS query failed");
        }
    }

    [HttpGet("studies/{study}/series")]
    public async Task<IActionResult> QidoSearchSeries(string study, [FromQuery] string encoding = "")
    {
        try
        {
            var (matches, fuzzy, offset, limit) = ParseQidoQuery(Request);
            var seriesList = await Task.Run(() => _repository.QidoQuerySeries(study, matches, fuzzy, offset, limit, throwOnError: true));
            var json = "[" + string.Join(",", seriesList.Select(x => DicomWebHelpers.ToDicomJson(DicomWebHelpers.BuildSeriesDataset(x)))) + "]";
            return Content(json, DicomWebHelpers.JsonContentType, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            DicomLogger.Error("DICOMweb", ex, "QIDO-RS 查询序列失败 - Study: {Study}", study);
            return StatusCode(500, "QIDO-RS series query failed");
        }
    }

    [HttpGet("studies/{study}/series/{series}/instances")]
    public async Task<IActionResult> QidoSearchInstances(string study, string series, [FromQuery] string encoding = "")
    {
        try
        {
            var (matches, fuzzy, offset, limit) = ParseQidoQuery(Request);
            var instances = await Task.Run(() => _repository.QidoQueryInstances(study, series, matches, fuzzy, offset, limit, throwOnError: true));
            var json = "[" + string.Join(",", instances.Select(i => DicomWebHelpers.ToDicomJson(DicomWebHelpers.BuildInstanceDataset(i)))) + "]";
            return Content(json, DicomWebHelpers.JsonContentType, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            DicomLogger.Error("DICOMweb", ex, "QIDO-RS 查询实例失败 - Study: {Study}, Series: {Series}", study, series);
            return StatusCode(500, "QIDO-RS instance query failed");
        }
    }

    // ── WADO-RS 检索 ──────────────────────────────────────────────────────

    [HttpGet("studies/{study}")]
    public async Task<IActionResult> RetrieveStudy(string study)
    {
        return await RetrieveInstances(
            study,
            seriesUid: null,
            instanceUid: null,
            single: false,
            operation: "study");
    }

    [HttpGet("studies/{study}/series/{series}")]
    public async Task<IActionResult> RetrieveSeries(string study, string series)
    {
        return await RetrieveInstances(
            study,
            seriesUid: series,
            instanceUid: null,
            single: false,
            operation: "series");
    }

    [HttpGet("studies/{study}/series/{series}/instances/{instance}")]
    public async Task<IActionResult> RetrieveInstance(string study, string series, string instance)
    {
        var accept = Request.Headers["Accept"].ToString();
        var wantRetrieve = AcceptsJson(accept) && !AcceptsMultipart(accept) && !AcceptsDicom(accept);

        if (wantRetrieve)
        {
            return await RetrieveInstanceMetadata(study, series, instance);
        }

        return await RetrieveInstances(
            study,
            seriesUid: series,
            instanceUid: instance,
            single: true,
            operation: "instance");
    }

    [HttpGet("studies/{study}/series/{series}/instances/{instance}/metadata")]
    public async Task<IActionResult> RetrieveInstanceMetadata(string study, string series, string instance)
    {
        try
        {
            var dbInstance = await _repository.GetInstanceAsync(instance);
            if (dbInstance == null)
            {
                return NotFound("Instance not found");
            }

            var filePath = Path.Combine(_settings.StoragePath, dbInstance.FilePath);
            if (!System.IO.File.Exists(filePath))
            {
                return NotFound("DICOM file not found");
            }

            var dicomFile = await DicomFile.OpenAsync(filePath);
            var json = "[" + DicomWebHelpers.ToDicomJson(dicomFile.Dataset) + "]";
            return Content(json, DicomWebHelpers.JsonContentType, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            DicomLogger.Error("DICOMweb", ex, "WADO-RS 元数据检索失败 - Instance: {Instance}", instance);
            return StatusCode(500, "Metadata retrieval failed");
        }
    }

    [HttpGet("studies/{study}/series/{series}/instances/{instance}/frames/{frames}")]
    public async Task<IActionResult> RetrieveFrames(string study, string series, string instance, string frames, [FromQuery] string transferSyntax = "")
    {
        try
        {
            var dbInstance = await _repository.GetInstanceAsync(instance);
            if (dbInstance == null)
            {
                return NotFound("Instance not found");
            }

            var filePath = Path.Combine(_settings.StoragePath, dbInstance.FilePath);
            if (!System.IO.File.Exists(filePath))
            {
                return NotFound("DICOM file not found");
            }

            var dicomFile = await DicomFile.OpenAsync(filePath);
            var frameNumbers = ParseFrameList(frames);
            var pixelData = DicomPixelData.Create(dicomFile.Dataset);
            var totalFrames = pixelData.NumberOfFrames;
            if (frameNumbers.Count == 0 || frameNumbers.Any(f => f < 1 || f > totalFrames))
            {
                return BadRequest($"Invalid frame numbers, total frames: {totalFrames}");
            }

            var accept = Request.Headers["Accept"].ToString();
            var wantJpeg = accept.Contains("image/jpeg", StringComparison.OrdinalIgnoreCase);

            var parts = new List<(string ContentType, byte[] Data)>();
            foreach (var frame in frameNumbers)
            {
                if (wantJpeg)
                {
                    parts.Add((DicomWebHelpers.JpegContentType, await RenderFrameToJpegAsync(dicomFile.Dataset, frame - 1)));
                }
                else
                {
                    var frameBuffer = pixelData.GetFrame(frame - 1);
                    parts.Add((DicomWebHelpers.OctetStreamContentType, frameBuffer.Data));
                }
            }

            var boundary = $"dicomweb-{Guid.NewGuid():N}";
            var body = BuildMultipartRelated(parts, boundary);
            return File(body, $"{DicomWebHelpers.MultipartRelated}; type={DicomWebHelpers.OctetStreamContentType}; boundary={boundary}");
        }
        catch (Exception ex)
        {
            DicomLogger.Error("DICOMweb", ex, "WADO-RS 帧检索失败 - Instance: {Instance}", instance);
            return StatusCode(500, "Frame retrieval failed");
        }
    }

    // ── STOW-RS 存储 ──────────────────────────────────────────────────────

    [HttpPost("studies")]
    [RequestSizeLimit(524288000)]
    [RequestFormLimits(MultipartBodyLengthLimit = 524288000)]
    public async Task<IActionResult> StoreStudy()
    {
        return await Store();
    }

    [HttpPost("studies/{study}")]
    [RequestSizeLimit(524288000)]
    [RequestFormLimits(MultipartBodyLengthLimit = 524288000)]
    public async Task<IActionResult> StoreStudyInto(string study)
    {
        return await Store(study);
    }

    private async Task<IActionResult> Store(string? forcedStudyUid = null)
    {
        try
        {
            if (!Request.ContentType?.StartsWith("multipart/related", StringComparison.OrdinalIgnoreCase) == true)
            {
                return BadRequest("Content-Type must be multipart/related (DICOMweb STOW-RS)");
            }

            var dicomFiles = await ParseMultipartRelatedAsync();
            if (dicomFiles.Count == 0)
            {
                return BadRequest("No DICOM instances found in request body");
            }

            var storedUids = new List<(string Study, string Series, string Instance)>();
            foreach (var file in dicomFiles)
            {
                var (studyUid, seriesUid, instanceUid, relativePath) = await StoreDicomFileAsync(file, forcedStudyUid);
                storedUids.Add((studyUid, seriesUid, instanceUid));
            }

            DicomLogger.Information("DICOMweb", "STOW-RS 存储完成 - 文件数: {Count}", storedUids.Count);

            var json = BuildStowResponseJson(storedUids);
            return Content(json, DicomWebHelpers.JsonContentType, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            DicomLogger.Error("DICOMweb", ex, "STOW-RS 存储失败");
            return StatusCode(500, "STOW-RS store failed");
        }
    }

    // ── 内部辅助方法 ───────────────────────────────────────────────────────

    private async Task<IActionResult> RetrieveInstances(string studyUid, string? seriesUid, string? instanceUid, bool single, string operation)
    {
        try
        {
            List<Instance> instances;
            if (!string.IsNullOrEmpty(instanceUid))
            {
                var dbInstance = await _repository.GetInstanceAsync(instanceUid);
                instances = dbInstance != null ? new List<Instance> { dbInstance } : new List<Instance>();
            }
            else if (!string.IsNullOrEmpty(seriesUid))
            {
                instances = await Task.Run(() => _repository.GetInstancesBySeriesUid(studyUid, seriesUid!, throwOnError: true));
            }
            else
            {
                instances = await Task.Run(() => _repository.GetInstancesByStudyUid(studyUid, throwOnError: true).ToList());
            }

            if (instances.Count == 0)
            {
                return NotFound($"{operation} not found");
            }

            var parts = new List<(string ContentType, byte[] Data)>();
            foreach (var instance in instances)
            {
                var filePath = Path.Combine(_settings.StoragePath, instance.FilePath);
                if (!System.IO.File.Exists(filePath))
                {
                    continue;
                }
                parts.Add((DicomWebHelpers.DicomContentType, await System.IO.File.ReadAllBytesAsync(filePath)));
            }

            if (parts.Count == 0)
            {
                return NotFound("DICOM files not found");
            }

            if (single)
            {
                return File(parts[0].Data, DicomWebHelpers.DicomContentType);
            }

            var boundary = $"dicomweb-{Guid.NewGuid():N}";
            var body = BuildMultipartRelated(parts, boundary);
            return File(body, $"{DicomWebHelpers.MultipartRelated}; type={DicomWebHelpers.DicomContentType}; boundary={boundary}");
        }
        catch (Exception ex)
        {
            DicomLogger.Error("DICOMweb", ex, "WADO-RS 检索失败 - Operation: {Operation}, Study: {Study}", operation, studyUid);
            return StatusCode(500, "Retrieval failed");
        }
    }

    private async Task<(string StudyUid, string SeriesUid, string InstanceUid, string RelativePath)> StoreDicomFileAsync(DicomFile file, string? forcedStudyUid)
    {
        var ds = file.Dataset;

        var studyUid = !string.IsNullOrEmpty(forcedStudyUid)
            ? forcedStudyUid
            : ds.GetSingleValueOrDefault<string>(DicomTag.StudyInstanceUID, DicomUID.Generate().UID);
        var seriesUid = ds.GetSingleValueOrDefault<string>(DicomTag.SeriesInstanceUID, DicomUID.Generate().UID);
        var instanceUid = ds.GetSingleValueOrDefault<string>(DicomTag.SOPInstanceUID, DicomUID.Generate().UID);

        var studyDate = StandardizeDicomDate(ds.GetSingleValueOrDefault<string>(DicomTag.StudyDate, string.Empty));
        var year = studyDate.Substring(0, 4);
        var month = studyDate.Substring(4, 2);
        var day = studyDate.Substring(6, 2);

        var relativePath = Path.Combine(year, month, day, studyUid, seriesUid, $"{instanceUid}.dcm");
        var fullPath = Path.Combine(_settings.StoragePath, relativePath);
        var dir = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(dir);

        if (!System.IO.File.Exists(fullPath))
        {
            await using var ms = new MemoryStream();
            await file.SaveAsync(ms);
            await System.IO.File.WriteAllBytesAsync(fullPath, ms.ToArray());
        }

        await _persistence.SaveDicomDataImmediateAsync(ds, relativePath);
        return (studyUid, seriesUid, instanceUid, relativePath);
    }

    private static string StandardizeDicomDate(string? dateValue)
    {
        if (string.IsNullOrEmpty(dateValue))
        {
            return DateTime.Now.ToString("yyyyMMdd");
        }

        var clean = new string(dateValue.Where(char.IsDigit).ToArray());
        if (clean.Length == 8 &&
            DateTime.TryParseExact(clean, "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out var dt))
        {
            return dt.ToString("yyyyMMdd");
        }

        return DateTime.Now.ToString("yyyyMMdd");
    }

    private async Task<List<DicomFile>> ParseMultipartRelatedAsync()
    {
        var contentType = Request.ContentType!;
        var boundary = ExtractBoundary(contentType);
        if (string.IsNullOrEmpty(boundary))
        {
            throw new InvalidOperationException("Missing multipart boundary");
        }

        using var ms = new MemoryStream();
        await Request.Body.CopyToAsync(ms);
        ms.Position = 0;

        var result = new List<DicomFile>();
        using var reader = new StreamReader(ms, Encoding.Latin1, true);
        var content = reader.ReadToEnd();

        var tokens = content.Split($"--{boundary}", StringSplitOptions.None);
        foreach (var token in tokens)
        {
            var trimmed = token.TrimStart('\r', '\n');
            var endHeader = trimmed.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (endHeader < 0)
            {
                continue;
            }

            var headers = trimmed[..endHeader];
            var body = trimmed[(endHeader + 4)..];

            var partContentType = ParsePartContentType(headers);
            if (!string.Equals(partContentType ?? "", DicomWebHelpers.DicomContentType, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(partContentType ?? "", DicomWebHelpers.OctetStreamContentType, StringComparison.OrdinalIgnoreCase))
            {
                DicomLogger.Warning("DICOMweb", "STOW-RS 跳过不支持的部分类型: {ContentType}", partContentType ?? "null");
                continue;
            }

            if (body.Trim().Length == 0)
            {
                continue;
            }

            try
            {
                using var partStream = new MemoryStream(Encoding.Latin1.GetBytes(body));
                var dicomFile = await DicomFile.OpenAsync(partStream);
                result.Add(dicomFile);
            }
            catch (Exception ex)
            {
                DicomLogger.Warning("DICOMweb", ex, "STOW-RS 解析 DICOM 部分失败，跳过");
            }
        }

        return result;
    }

    private static string? ExtractBoundary(string contentType)
    {
        var parts = contentType.Split(';');
        foreach (var part in parts)
        {
            var kv = part.Split('=', 2);
            if (kv.Length == 2 && kv[0].Trim().Equals("boundary", StringComparison.OrdinalIgnoreCase))
            {
                return kv[1].Trim().Trim('"');
            }
        }
        return null;
    }

    private static string? ParsePartContentType(string headers)
    {
        foreach (var line in headers.Split('\r', '\n'))
        {
            if (line.StartsWith("Content-Type:", StringComparison.OrdinalIgnoreCase))
            {
                return line[(line.IndexOf(':') + 1)..].Trim().Split(';')[0].Trim();
            }
        }
        return null;
    }

    private static byte[] BuildMultipartRelated(List<(string ContentType, byte[] Data)> parts, string boundary)
    {
        using var ms = new MemoryStream();
        foreach (var (contentType, data) in parts)
        {
            var header = Encoding.ASCII.GetBytes($"--{boundary}\r\nContent-Type: {contentType}\r\n\r\n");
            ms.Write(header, 0, header.Length);
            ms.Write(data, 0, data.Length);
            var nl = Encoding.ASCII.GetBytes("\r\n");
            ms.Write(nl, 0, nl.Length);
        }
        var tail = Encoding.ASCII.GetBytes($"--{boundary}--\r\n");
        ms.Write(tail, 0, tail.Length);
        return ms.ToArray();
    }

    private async Task<byte[]> RenderFrameToJpegAsync(DicomDataset dataset, int frameIndex)
    {
        try
        {
            var dicomImage = new DicomImage(dataset, frameIndex);
            var renderedImage = dicomImage.RenderImage();
            using var ms = new MemoryStream();
            using var image = Image.LoadPixelData<Rgba32>(
                renderedImage.AsBytes(), renderedImage.Width, renderedImage.Height);
            await image.SaveAsJpegAsync(ms, new JpegEncoder { Quality = 90 });
            return ms.ToArray();
        }
        catch
        {
            // 渲染失败时回退到原始帧数据
            return DicomPixelData.Create(dataset).GetFrame(frameIndex).Data;
        }
    }

    private static List<int> ParseFrameList(string frames)
    {
        var list = new List<int>();
        foreach (var item in frames.Split(','))
        {
            if (int.TryParse(item, out var n))
            {
                list.Add(n);
            }
        }
        return list;
    }

    private (Dictionary<DicomTag, IReadOnlyList<string>> Matches, bool Fuzzy, int? Offset, int? Limit) ParseQidoQuery(HttpRequest request)
    {
        var matches = new Dictionary<DicomTag, IReadOnlyList<string>>();
        var fuzzy = false;
        int? offset = null;
        int? limit = null;

        foreach (var kv in request.Query)
        {
            var key = kv.Key;
            switch (key.ToLower())
            {
                case "fuzzymatch":
                    fuzzy = kv.Value.ToString().Equals("true", StringComparison.OrdinalIgnoreCase);
                    continue;
                case "offset":
                    if (int.TryParse(kv.Value.ToString(), out var off)) offset = off;
                    continue;
                case "limit":
                    if (int.TryParse(kv.Value.ToString(), out var lim)) limit = lim;
                    continue;
                case "includefield":
                    continue;
            }

            var tag = ResolveTag(key);
            if (tag == null)
            {
                continue;
            }

            var values = kv.Value.Select(v => v ?? "").ToList();
            matches[tag] = values;
        }

        return (matches, fuzzy, offset, limit);
    }

    private static DicomTag? ResolveTag(string key)
    {
        // 支持十六进制标签（00080020）与关键字（StudyDate）两种写法
        if (key.Length == 8 && key.All(char.IsAsciiHexDigit))
        {
            try
            {
                ushort group = Convert.ToUInt16(key[..4], 16);
                ushort element = Convert.ToUInt16(key[4..], 16);
                return new DicomTag(group, element);
            }
            catch
            {
                return null;
            }
        }

        try
        {
            return FellowOakDicom.DicomDictionary.Default[key];
        }
        catch
        {
            return null;
        }
    }

    private static bool AcceptsJson(string accept)
    {
        return accept.Contains(DicomWebHelpers.JsonContentType, StringComparison.OrdinalIgnoreCase)
            || accept.Contains("*/*", StringComparison.OrdinalIgnoreCase);
    }

    private static bool AcceptsMultipart(string accept)
    {
        return accept.Contains(DicomWebHelpers.MultipartRelated, StringComparison.OrdinalIgnoreCase);
    }

    private static bool AcceptsDicom(string accept)
    {
        return accept.Contains(DicomWebHelpers.DicomContentType, StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildStowResponseJson(List<(string Study, string Series, string Instance)> storedUids)
    {
        // PS3.18 STOW-RS 成功响应：ReferencedSOPSequence
        var sb = new StringBuilder();
        sb.Append("{\"00081199\":{\"vr\":\"SQ\",\"Value\":[");
        sb.Append(string.Join(",", storedUids.Select(u =>
            $"{{\"00081150\":{{\"vr\":\"UI\",\"Value\":[\"\"],\"BulkDataURI\":null}},\"00081155\":{{\"vr\":\"UI\",\"Value\":[\"{u.Instance}\"]}}}}")));
        sb.Append("]}}");
        return sb.ToString();
    }
}
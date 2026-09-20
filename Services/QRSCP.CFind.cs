using System.Collections.Concurrent;
using System.Text;
using FellowOakDicom;
using FellowOakDicom.Imaging.Codec;
using FellowOakDicom.Network;
using FellowOakDicom.Network.Client;
using DicomSCP.Configuration;
using DicomSCP.Models;
using DicomSCP.Repository;

namespace DicomSCP.Services;

public partial class QRSCP
{
    private async Task<List<DicomCFindResponse>> HandleStudyLevelFind(DicomCFindRequest request)
    {
        var responses = new List<DicomCFindResponse>();

        try
        {
            // 从请求中获取查询参数
            var queryParams = ExtractStudyQueryParameters(request);
            
            DicomLogger.Information("QRSCP", "Study级查询参数 - PatientId: {PatientId}, PatientName: {PatientName}, " +
                "AccessionNumber: {AccessionNumber}, 日期范围: {StartDate} - {EndDate}, Modality: {Modality},StudyInstanceUid: {StudyInstanceUid}",
                queryParams.PatientId,
                queryParams.PatientName,
                queryParams.AccessionNumber,
                queryParams.DateRange.StartDate,
                queryParams.DateRange.EndDate,
                queryParams.Modalities,
                queryParams.StudyInstanceUid);

            // 从数据库查询数据
            var studies = await Task.Run(() => _repository.GetStudies(
                queryParams.PatientId,
                queryParams.PatientName,
                queryParams.AccessionNumber,
                queryParams.DateRange,
                queryParams.Modalities,
                queryParams.StudyInstanceUid,
                0,     // offset
                1000   // 限制返回1000条记录
            ));

            DicomLogger.Information("QRSCP", "Study级查询结果 - 记录数: {Count}", studies.Count);

            // 构建响应
            foreach (var study in studies)
            {
                var response = CreateStudyResponse(request, study);
                responses.Add(response);
            }
        }
        catch (Exception ex)
        {
            DicomLogger.Error("QRSCP", ex, "Study级查询失败: {Message}", ex.Message);
            responses.Add(new DicomCFindResponse(request, DicomStatus.ProcessingFailure));
        }

        return responses;
    }


    private record StudyQueryParameters(
        string PatientId,
        string PatientName,
        string AccessionNumber,
        (string StartDate, string EndDate) DateRange,
        string[] Modalities,
        string StudyInstanceUid);


    private StudyQueryParameters ExtractStudyQueryParameters(DicomCFindRequest request)
    {
        // 处理可能为 null 的字符串
        string ProcessValue(string? value) => 
            (value?.Replace("*", "")) ?? string.Empty;

        // 记录原始日期值
        var studyDate = request.Dataset.GetSingleValueOrDefault<string>(DicomTag.StudyDate, string.Empty);
        var studyTime = request.Dataset.GetSingleValueOrDefault<string>(DicomTag.StudyTime, string.Empty);
        if (!string.IsNullOrEmpty(studyDate))
        {
            DicomLogger.Debug("QRSCP", "查询日期: {Date}", studyDate);
        }

        // 处理日期范围
        (string StartDate, string EndDate) ProcessDateRange(string? dateValue)
        {
            if (string.IsNullOrEmpty(dateValue))
                return (string.Empty, string.Empty);  // 返回空，查询所有记录

            // 移除可能的 VR 和标签信息
            var cleanDateValue = dateValue;
            if (dateValue.Contains("DA"))
            {
                var match = System.Text.RegularExpressions.Regex.Match(dateValue, @"\d{8}");
                if (match.Success)
                {
                    cleanDateValue = match.Value;
                }
            }

            // 处理 DICOM 日期范围格式
            if (cleanDateValue.Contains("-"))
            {
                var parts = cleanDateValue.Split('-');
                if (parts.Length == 2)
                {
                    var startDate = parts[0].Trim();
                    var endDate = parts[1].Trim();

                    // 处理开放式范围
                    if (string.IsNullOrEmpty(startDate))
                    {
                        startDate = "19000101";  // 使用最小日期
                    }
                    if (string.IsNullOrEmpty(endDate))
                    {
                        endDate = "99991231";    // 使用最大日期
                    }

                    return (startDate, endDate);
                }
            }
            
            // 如果是个日期，开始和结束日期相同
            return (cleanDateValue, cleanDateValue);
        }

        var dateRange = ProcessDateRange(studyDate);

        // 处理 Modality 列表
        string[] ProcessModalities(DicomDataset dataset)
        {
            var modalities = new List<string>();

            // 尝试取 ModalitiesInStudy
            if (dataset.Contains(DicomTag.ModalitiesInStudy))
            {
                try
                {
                    var modalityValues = dataset.GetValues<string>(DicomTag.ModalitiesInStudy);
                    if (modalityValues != null && modalityValues.Length > 0)
                    {
                        modalities.AddRange(modalityValues.Where(m => !string.IsNullOrEmpty(m)));
                    }
                }
                catch (Exception ex)
                {
                    DicomLogger.Warning("QRSCP", ex, "获取 ModalitiesInStudy 失败");
                }
            }

            // 尝试获取单 Modality
            var singleModality = dataset.GetSingleValueOrDefault<string>(DicomTag.Modality, string.Empty);
            if (!string.IsNullOrEmpty(singleModality) && !modalities.Contains(singleModality))
            {
                modalities.Add(singleModality);
            }

            return modalities.ToArray();
        }

        var parameters = new StudyQueryParameters(
            ProcessValue(request.Dataset.GetSingleValueOrDefault<string>(DicomTag.PatientID, string.Empty)),
            ProcessValue(request.Dataset.GetSingleValueOrDefault<string>(DicomTag.PatientName, string.Empty)),
            ProcessValue(request.Dataset.GetSingleValueOrDefault<string>(DicomTag.AccessionNumber, string.Empty)),
            dateRange,
            ProcessModalities(request.Dataset),
            ProcessValue(request.Dataset.GetSingleValueOrDefault<string>(DicomTag.StudyInstanceUID, string.Empty)));

        return parameters;
    }


    private DicomCFindResponse CreateStudyResponse(DicomCFindRequest request, Study study)
    {
        var response = new DicomCFindResponse(request, DicomStatus.Pending);
        var dataset = new DicomDataset();

        // 获取请求中的字符集，如果没有指定则默认使用 UTF-8
        var requestedCharacterSet = request.Dataset.GetSingleValueOrDefault(DicomTag.SpecificCharacterSet, "ISO_IR 192");

        // 根据请求的字符集设置响应的字符集
        switch (requestedCharacterSet.ToUpperInvariant())
        {
            case "ISO_IR 100":  // Latin1
                dataset.Add(DicomTag.SpecificCharacterSet, "ISO_IR 100");
                break;
            case "GB18030":     // 中文简体
                dataset.Add(DicomTag.SpecificCharacterSet, "GB18030");
                break;
            case "ISO_IR 192":  // UTF-8
            default:
                dataset.Add(DicomTag.SpecificCharacterSet, "ISO_IR 192");
                break;
        }

        AddCommonTags(dataset, request.Dataset);

        dataset.Add(DicomTag.StudyInstanceUID, study.StudyInstanceUid)
              .Add(DicomTag.StudyDate, study.StudyDate ?? string.Empty)
              .Add(DicomTag.StudyTime, study.StudyTime ?? string.Empty)
              .Add(DicomTag.PatientName, study.PatientName ?? string.Empty)
              .Add(DicomTag.PatientID, study.PatientId ?? string.Empty)
              .Add(DicomTag.PatientBirthDate, study.PatientBirthDate ?? string.Empty)
              .Add(DicomTag.StudyDescription, study.StudyDescription ?? string.Empty)
              .Add(DicomTag.ModalitiesInStudy, study.Modality ?? string.Empty)
              .Add(DicomTag.AccessionNumber, study.AccessionNumber ?? string.Empty)
              .Add(DicomTag.NumberOfStudyRelatedSeries, study.NumberOfStudyRelatedSeries.ToString())
              .Add(DicomTag.NumberOfStudyRelatedInstances, study.NumberOfStudyRelatedInstances.ToString());

        response.Dataset = dataset;
        return response;
    }


    private async Task<List<DicomCFindResponse>> HandleSeriesLevelFind(DicomCFindRequest request)
    {
        var responses = new List<DicomCFindResponse>();

        var studyInstanceUid = request.Dataset.GetSingleValueOrDefault<string>(DicomTag.StudyInstanceUID, string.Empty);
        if (string.IsNullOrEmpty(studyInstanceUid))
        {
            DicomLogger.Warning("QRSCP", "Series级别查询缺少StudyInstanceUID");
            return responses;
        }

        // 使用 Task.Run 来异步执行数查询
        var seriesList = await Task.Run(() => _repository.GetSeriesByStudyUid(studyInstanceUid));

        foreach (var series in seriesList)
        {
            var response = new DicomCFindResponse(request, DicomStatus.Pending);
            var dataset = new DicomDataset();

            // 添加字符集和其他用标签
            AddCommonTags(dataset, request.Dataset);

            // 设置必要的字段
            dataset.Add(DicomTag.StudyInstanceUID, series.StudyInstanceUid);
            dataset.Add(DicomTag.SeriesInstanceUID, series.SeriesInstanceUid);
            dataset.Add(DicomTag.Modality, series.Modality ?? string.Empty);
            dataset.Add(DicomTag.SeriesNumber, series.SeriesNumber ?? string.Empty);
            dataset.Add(DicomTag.SeriesDescription, series.SeriesDescription ?? string.Empty);
            dataset.Add(DicomTag.NumberOfSeriesRelatedInstances, series.NumberOfInstances);

            // 复制请求的其他查询字段（如果不存在）
            foreach (var tag in request.Dataset.Select(x => x.Tag))
            {
                if (!dataset.Contains(tag) && request.Dataset.TryGetString(tag, out string value))
                {
                    dataset.Add(tag, value);
                }
            }

            response.Dataset = dataset;
            responses.Add(response);
        }

        DicomLogger.Information("QRSCP", "Series级别查询完成 - 返回记录数: {Count}", responses.Count);
        return responses;
    }


    private async Task<List<DicomCFindResponse>> HandleImageLevelFind(DicomCFindRequest request)
    {
        var responses = new List<DicomCFindResponse>();

        var studyInstanceUid = request.Dataset.GetSingleValueOrDefault<string>(DicomTag.StudyInstanceUID, string.Empty);
        var seriesInstanceUid = request.Dataset.GetSingleValueOrDefault<string>(DicomTag.SeriesInstanceUID, string.Empty);
        
        if (string.IsNullOrEmpty(studyInstanceUid) || string.IsNullOrEmpty(seriesInstanceUid))
        {
            DicomLogger.Warning("QRSCP", "Image级别查询缺少StudyInstanceUID或SeriesInstanceUID");
            return responses;
        }

        // 构建 SR 专属过滤键（SOPClassUID / DocumentTitle / CompletionFlag / VerificationFlag）
        var matches = new Dictionary<DicomTag, IReadOnlyList<string>>();
        void AddMatch(DicomTag tag)
        {
            var value = request.Dataset.GetSingleValueOrDefault<string>(tag, string.Empty);
            if (!string.IsNullOrEmpty(value))
            {
                matches[tag] = new[] { value };
            }
        }
        AddMatch(DicomTag.SOPClassUID);
        AddMatch(DicomTag.DocumentTitle);
        AddMatch(DicomTag.CompletionFlag);
        AddMatch(DicomTag.VerificationFlag);
        AddMatch(DicomTag.CodeValue);
        AddMatch(DicomTag.CodingSchemeDesignator);
        AddMatch(DicomTag.CodeMeaning);

        // 使 Task.Run 来异步执行数据库查询
        var instances = await Task.Run(() => _repository.GetInstancesBySeriesUid(studyInstanceUid, seriesInstanceUid, matches));

        foreach (var instance in instances)
        {
            try
            {
                var response = new DicomCFindResponse(request, DicomStatus.Pending);
                var dataset = new DicomDataset();

                // 添加字符集和其他通用标签
                AddCommonTags(dataset, request.Dataset);

                // 验证 UID 格
                var validStudyUid = ValidateUID(studyInstanceUid);
                var validSeriesUid = ValidateUID(seriesInstanceUid);
                var validSopInstanceUid = ValidateUID(instance.SopInstanceUid);
                var validSopClassUid = ValidateUID(instance.SopClassUid);

                // 置必要的字段
                dataset.Add(DicomTag.StudyInstanceUID, validStudyUid);
                dataset.Add(DicomTag.SeriesInstanceUID, validSeriesUid);
                dataset.Add(DicomTag.SOPInstanceUID, validSopInstanceUid);
                dataset.Add(DicomTag.SOPClassUID, validSopClassUid);
                dataset.Add(DicomTag.InstanceNumber, instance.InstanceNumber ?? string.Empty);

                // 回显 SR 报告级字段（非 SR 实例为空，跳过）
                if (!string.IsNullOrEmpty(instance.DocumentTitle))
                    dataset.Add(DicomTag.DocumentTitle, instance.DocumentTitle);
                if (!string.IsNullOrEmpty(instance.CompletionFlag))
                    dataset.Add(DicomTag.CompletionFlag, instance.CompletionFlag);
                if (!string.IsNullOrEmpty(instance.VerificationFlag))
                    dataset.Add(DicomTag.VerificationFlag, instance.VerificationFlag);
                if (!string.IsNullOrEmpty(instance.ConceptCodeValue))
                    dataset.Add(DicomTag.CodeValue, instance.ConceptCodeValue);
                if (!string.IsNullOrEmpty(instance.ConceptCodingSchemeDesignator))
                    dataset.Add(DicomTag.CodingSchemeDesignator, instance.ConceptCodingSchemeDesignator);
                if (!string.IsNullOrEmpty(instance.ConceptCodeMeaning))
                    dataset.Add(DicomTag.CodeMeaning, instance.ConceptCodeMeaning);
                if (!string.IsNullOrEmpty(instance.VerificationDateTime))
                    dataset.Add(DicomTag.VerificationDateTime, instance.VerificationDateTime);
                if (!string.IsNullOrEmpty(instance.ContentDate))
                    dataset.Add(DicomTag.ContentDate, instance.ContentDate);
                if (!string.IsNullOrEmpty(instance.ContentTime))
                    dataset.Add(DicomTag.ContentTime, instance.ContentTime);

                response.Dataset = dataset;
                responses.Add(response);
            }
            catch (Exception ex)
            {
                DicomLogger.Error("QRSCP", ex, "创建Image响应失败 - SOPInstanceUID: {SopInstanceUid}", 
                    instance.SopInstanceUid);
                continue;
            }
        }

        DicomLogger.Information("QRSCP", "Image级别查询完成 - 返回记录数: {Count}", responses.Count);
        return responses;
    }


    private string ValidateUID(string uid)
    {
        if (string.IsNullOrEmpty(uid)) return string.Empty;
        
        try
        {
            // 分割 UID
            var parts = uid.Split('.');
            var validParts = new List<string>();

            foreach (var part in parts)
            {
                // 跳过空组
                if (string.IsNullOrEmpty(part))
                {
                    continue;
                }

                // 移除前导零并确保至少保留一个数字
                var trimmed = part.TrimStart('0');
                validParts.Add(string.IsNullOrEmpty(trimmed) ? "0" : trimmed);
            }

            // 确保少有两个组件
            if (validParts.Count < 2)
            {
                DicomLogger.Warning("QRSCP", "无效的UID格式 (组件数量不足): {Uid}", 
                    uid ?? string.Empty);
                return "0.0";
            }

            return string.Join(".", validParts);
        }
        catch (Exception ex)
        {
            DicomLogger.Error("QRSCP", ex, "UID验证失败: {Uid}", 
                uid ?? string.Empty);
            return "0.0";
        }
    }


    private void AddCommonTags(DicomDataset dataset, DicomDataset requestDataset)
    {
        // 让 fo-dicom 处理字符集
        dataset.AddOrUpdate(DicomTag.SpecificCharacterSet, 
            requestDataset.GetSingleValueOrDefault(DicomTag.SpecificCharacterSet, "ISO_IR 192"));
    }


    private async Task<List<DicomCFindResponse>> HandlePatientLevelFind(DicomCFindRequest request)
    {
        var responses = new List<DicomCFindResponse>();

        try
        {
            // 从请求中获取查询参数
            var patientId = request.Dataset.GetSingleValueOrDefault<string>(DicomTag.PatientID, string.Empty);
            var patientName = request.Dataset.GetSingleValueOrDefault<string>(DicomTag.PatientName, string.Empty);

            DicomLogger.Information("QRSCP", 
                "Patient级查询 - ID: {PatientId}, Name: {PatientName}", 
                patientId, patientName);

            // 从数据库查询患者数据
            var patients = await Task.Run(() => _repository.GetPatients(patientId, patientName));

            // 构建响应
            foreach (var patient in patients)
            {
                var response = new DicomCFindResponse(request, DicomStatus.Pending);
                var dataset = new DicomDataset();

                AddCommonTags(dataset, request.Dataset);

                dataset.Add(DicomTag.PatientID, patient.PatientId ?? string.Empty)
                      .Add(DicomTag.PatientName, patient.PatientName ?? string.Empty)
                      .Add(DicomTag.PatientBirthDate, patient.PatientBirthDate ?? string.Empty)
                      .Add(DicomTag.PatientSex, patient.PatientSex ?? string.Empty)
                      .Add(DicomTag.NumberOfPatientRelatedStudies, patient.NumberOfStudies.ToString())
                      .Add(DicomTag.NumberOfPatientRelatedSeries, patient.NumberOfSeries.ToString())
                      .Add(DicomTag.NumberOfPatientRelatedInstances, patient.NumberOfInstances.ToString());

                response.Dataset = dataset;
                responses.Add(response);
            }

            DicomLogger.Information("QRSCP", "Patient级查询完成 - 返回记录数: {Count}", responses.Count);
        }
        catch (Exception ex)
        {
            DicomLogger.Error("QRSCP", ex, "Patient级查询失败");
            responses.Add(new DicomCFindResponse(request, DicomStatus.ProcessingFailure));
        }

        return responses;
    }


}

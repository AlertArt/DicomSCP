using DicomSCP.Repository;
using Microsoft.AspNetCore.Mvc;
using System.Xml.Linq;

namespace DicomSCP.Controllers;

/// <summary>
/// 查看器辅助端点。主查看器统一为 OHIF（前端直连标准 /dicomweb，见 wwwroot/dicomviewer），
/// 此处仅保留外部 Weasis 客户端的 manifest 生成。
/// </summary>
[Route("viewer")]
public class ViewerController(DicomRepository repository) : ControllerBase
{
    private readonly DicomRepository _repository = repository;

    [HttpGet("weasis/{studyInstanceUid}")]
    public async Task<IActionResult> GetWeasisManifest(string studyInstanceUid, [FromQuery] string? token = null)
    {
        try
        {
            var study = await _repository.GetStudyAsync(studyInstanceUid);
            if (study == null)
            {
                return NotFound("Study not found");
            }

            var seriesList = await _repository.GetSeriesAsync(studyInstanceUid);

            var baseUrl = $"{Request.Scheme}://{Request.Host}";

            XNamespace ns = "http://www.weasis.org/xsd/2.5";
            XNamespace xsi = "http://www.w3.org/2001/XMLSchema-instance";

            var manifest = new XElement(ns + "manifest");
            manifest.Add(new XAttribute(XNamespace.Xmlns + "xsi", xsi));

            var arcQuery = new XElement(ns + "arcQuery",
                new XAttribute("additionnalParameters", string.IsNullOrEmpty(token) ? "" : $"token={token}"),
                new XAttribute("arcId", "1001"),
                new XAttribute("baseUrl", $"{baseUrl}/wado"),
                new XAttribute("requireOnlySOPInstanceUID", "false")
            );

            var patient = new XElement(ns + "Patient",
                new XAttribute("PatientID", study.PatientId),
                new XAttribute("PatientName", study.PatientName ?? ""),
                new XAttribute("PatientSex", study.PatientSex ?? "")
            );

            var studyElement = new XElement(ns + "Study",
                new XAttribute("AccessionNumber", study.AccessionNumber ?? ""),
                new XAttribute("StudyDate", study.StudyDate ?? ""),
                new XAttribute("StudyDescription", study.StudyDescription ?? ""),
                new XAttribute("StudyInstanceUID", study.StudyInstanceUid),
                new XAttribute("StudyTime", study.StudyTime ?? "")
            );

            foreach (var series in seriesList)
            {
                var instances = await _repository.GetSeriesInstancesAsync(series.SeriesInstanceUid);
                var seriesElement = new XElement(ns + "Series",
                    new XAttribute("Modality", series.Modality ?? ""),
                    new XAttribute("SeriesDescription", series.SeriesDescription ?? ""),
                    new XAttribute("SeriesInstanceUID", series.SeriesInstanceUid),
                    new XAttribute("SeriesNumber", series.SeriesNumber ?? "")
                );

                foreach (var instance in instances)
                {
                    seriesElement.Add(new XElement(ns + "Instance",
                        new XAttribute("InstanceNumber", instance.InstanceNumber ?? ""),
                        new XAttribute("SOPInstanceUID", instance.SopInstanceUid)
                    ));
                }

                studyElement.Add(seriesElement);
            }

            patient.Add(studyElement);
            arcQuery.Add(patient);
            manifest.Add(arcQuery);

            var doc = new XDocument(new XDeclaration("1.0", "UTF-8", null), manifest);

            return Content(doc.ToString(), "application/xml");
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Error generating Weasis manifest: {ex.Message}");
        }
    }
}

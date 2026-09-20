using DicomSCP.Services;
using FellowOakDicom;
using Xunit;

namespace DicomSCP.Tests;

public class SrSupportTests
{
    [Theory]
    [InlineData("1.2.840.10008.5.1.4.1.1.88.11")] // Basic Text SR
    [InlineData("1.2.840.10008.5.1.4.1.1.88.22")] // Enhanced SR
    [InlineData("1.2.840.10008.5.1.4.1.1.88.33")] // Comprehensive SR
    [InlineData("1.2.840.10008.5.1.4.1.1.88.34")] // Comprehensive 3D SR
    [InlineData("1.2.840.10008.5.1.4.1.1.88.40")] // Procedure Log
    [InlineData("1.2.840.10008.5.1.4.1.1.88.59")] // Key Object Selection Document
    public void IsStructuredReport_RecognizesWhitelistedSopClasses(string sopClassUid)
    {
        Assert.True(SrSupport.IsStructuredReport(sopClassUid));
    }

    [Theory]
    [InlineData("1.2.840.10008.5.1.4.1.1.2")]   // CT Image Storage
    [InlineData("1.2.840.10008.5.1.4.1.1.88.1")] // not an SR storage class
    [InlineData("")]
    [InlineData(null)]
    public void IsStructuredReport_RejectsNonSr(string? sopClassUid)
    {
        Assert.False(SrSupport.IsStructuredReport(sopClassUid));
    }

    [Fact]
    public void IsStructuredReport_FromDataset_UsesSopClassUid()
    {
        Assert.True(SrSupport.IsStructuredReport(DicomTestData.MakeStructuredReport()));
        Assert.False(SrSupport.IsStructuredReport(DicomTestData.MakeInstance()));
    }

    [Fact]
    public void Extract_ReadsReportFieldsAndConceptName()
    {
        var dataset = DicomTestData.MakeStructuredReport(
            documentTitle: "My Report",
            completionFlag: "PARTIAL",
            verificationFlag: "VERIFIED",
            conceptCodeValue: "12345",
            conceptScheme: "DCM",
            conceptMeaning: "Some Concept");

        var fields = SrSupport.Extract(dataset);

        Assert.Equal("My Report", fields.DocumentTitle);
        Assert.Equal("PARTIAL", fields.CompletionFlag);
        Assert.Equal("VERIFIED", fields.VerificationFlag);
        Assert.Equal("12345", fields.ConceptCodeValue);
        Assert.Equal("DCM", fields.ConceptCodingSchemeDesignator);
        Assert.Equal("Some Concept", fields.ConceptCodeMeaning);
    }

    [Fact]
    public void Extract_MissingFields_ReturnsEmptyStrings()
    {
        var ds = new DicomDataset();
        ds.AddOrUpdate(DicomTag.SOPClassUID, DicomUID.BasicTextSRStorage.UID);

        var fields = SrSupport.Extract(ds);

        Assert.Equal(string.Empty, fields.DocumentTitle);
        Assert.Equal(string.Empty, fields.CompletionFlag);
        Assert.Equal(string.Empty, fields.VerificationFlag);
        Assert.Equal(string.Empty, fields.ConceptCodeValue);
        Assert.Equal(string.Empty, fields.ConceptCodingSchemeDesignator);
        Assert.Equal(string.Empty, fields.ConceptCodeMeaning);
    }

    [Fact]
    public void ExtractReferences_ReadsEvidenceSequence()
    {
        var sr = DicomTestData.MakeStructuredReport(sopUid: "1.2.3.4.10.1");
        DicomTestData.AddEvidence(sr, "1.2.3.4.20.1", "1.2.3.4.20.2", "1.2.3.4.20.3");

        var references = SrSupport.ExtractReferences(sr);

        var reference = Assert.Single(references);
        Assert.Equal("1.2.3.4.10.1", reference.SrSopInstanceUid);
        Assert.Equal("1.2.3.4.20.3", reference.ReferencedSopInstanceUid);
        Assert.Equal("1.2.840.10008.5.1.4.1.1.2", reference.ReferencedSopClassUid);
        Assert.Equal("1.2.3.4.20.2", reference.SeriesInstanceUid);
        Assert.Equal("1.2.3.4.20.1", reference.StudyInstanceUid);
    }

    [Fact]
    public void ExtractReferences_DeduplicatesReferencedSopInstances()
    {
        var sr = DicomTestData.MakeStructuredReport(sopUid: "1.2.3.4.30.1");

        // 同一被引用 SOP 出现在两个系列条目中，应去重为一条
        var sopItem = new DicomDataset
        {
            { DicomTag.ReferencedSOPClassUID, "1.2.840.10008.5.1.4.1.1.2" },
            { DicomTag.ReferencedSOPInstanceUID, "1.2.3.4.40.3" }
        };
        var sopSeq = new DicomSequence(DicomTag.ReferencedSOPSequence);
        sopSeq.Items.Add(sopItem);

        var seriesItem = new DicomDataset { { DicomTag.SeriesInstanceUID, "1.2.3.4.40.2" } };
        seriesItem.Add(DicomTag.ReferencedSOPSequence, sopSeq);
        var seriesSeq = new DicomSequence(DicomTag.ReferencedSeriesSequence);
        seriesSeq.Items.Add(seriesItem);
        seriesSeq.Items.Add(seriesItem);

        var studyItem = new DicomDataset { { DicomTag.StudyInstanceUID, "1.2.3.4.40.1" } };
        studyItem.Add(DicomTag.ReferencedSeriesSequence, seriesSeq);
        var studySeq = new DicomSequence(DicomTag.CurrentRequestedProcedureEvidenceSequence);
        studySeq.Items.Add(studyItem);
        sr.Add(DicomTag.CurrentRequestedProcedureEvidenceSequence, studySeq);

        Assert.Single(SrSupport.ExtractReferences(sr));
    }

    [Fact]
    public void ExtractReferences_NoEvidence_ReturnsEmpty()
    {
        var sr = DicomTestData.MakeStructuredReport();

        Assert.Empty(SrSupport.ExtractReferences(sr));
    }
}

using DicomSCP.Models;
using DicomSCP.Services;
using FellowOakDicom;
using Xunit;

namespace DicomSCP.Tests;

public class SrGeneratorTests
{
    private static readonly DateTime FixedNow = new(2024, 1, 2, 12, 30, 45);

    [Fact]
    public void BuildBasicTextSr_SetsCoreAttributesAndTextItem()
    {
        var request = new SrGenerationRequest
        {
            PatientId = "PAT1",
            PatientName = "Doe^John",
            DocumentTitle = "Chest CT Report",
            ContentText = "No acute abnormality."
        };

        var ds = SrGenerator.BuildBasicTextSr(request, FixedNow);

        Assert.Equal(DicomUID.BasicTextSRStorage.UID, ds.GetSingleValue<string>(DicomTag.SOPClassUID));
        Assert.Equal("SR", ds.GetSingleValue<string>(DicomTag.Modality));
        Assert.Equal("PAT1", ds.GetSingleValue<string>(DicomTag.PatientID));
        Assert.Equal("Chest CT Report", ds.GetSingleValue<string>(DicomTag.DocumentTitle));
        Assert.Equal("CONTAINER", ds.GetSingleValue<string>(DicomTag.ValueType));
        Assert.Equal("SEPARATE", ds.GetSingleValue<string>(DicomTag.ContinuityOfContent));
        Assert.Equal("20240102", ds.GetSingleValue<string>(DicomTag.StudyDate));

        var textItem = Assert.Single(ds.GetSequence(DicomTag.ContentSequence).Items);
        Assert.Equal("TEXT", textItem.GetSingleValue<string>(DicomTag.ValueType));
        Assert.Equal("CONTAINS", textItem.GetSingleValue<string>(DicomTag.RelationshipType));
        Assert.Equal("No acute abnormality.", textItem.GetSingleValue<string>(DicomTag.TextValue));
        Assert.True(textItem.Contains(DicomTag.ConceptNameCodeSequence));
    }

    [Fact]
    public void BuildBasicTextSr_DefaultsCompletionAndVerificationFlags()
    {
        var ds = SrGenerator.BuildBasicTextSr(new SrGenerationRequest { ContentText = "x" }, FixedNow);

        Assert.Equal("COMPLETE", ds.GetSingleValue<string>(DicomTag.CompletionFlag));
        Assert.Equal("UNVERIFIED", ds.GetSingleValue<string>(DicomTag.VerificationFlag));
        Assert.False(ds.Contains(DicomTag.VerificationDateTime));
    }

    [Fact]
    public void BuildBasicTextSr_VerifiedSetsVerificationDateTime()
    {
        var ds = SrGenerator.BuildBasicTextSr(
            new SrGenerationRequest { ContentText = "x", VerificationFlag = "verified" }, FixedNow);

        Assert.Equal("VERIFIED", ds.GetSingleValue<string>(DicomTag.VerificationFlag));
        Assert.Equal("20240102123045", ds.GetSingleValue<string>(DicomTag.VerificationDateTime));
    }

    [Fact]
    public void BuildBasicTextSr_UsesProvidedIdentifiers()
    {
        var ds = SrGenerator.BuildBasicTextSr(new SrGenerationRequest
        {
            ContentText = "x",
            StudyInstanceUid = "1.2.3.4.5",
            SeriesInstanceUid = "1.2.3.4.6"
        }, FixedNow);

        Assert.Equal("1.2.3.4.5", ds.GetSingleValue<string>(DicomTag.StudyInstanceUID));
        Assert.Equal("1.2.3.4.6", ds.GetSingleValue<string>(DicomTag.SeriesInstanceUID));
        Assert.False(string.IsNullOrEmpty(ds.GetSingleValue<string>(DicomTag.SOPInstanceUID)));
    }

    [Fact]
    public void BuildBasicTextSr_WithReferences_BuildsEvidenceParseableBySrSupport()
    {
        var ds = SrGenerator.BuildBasicTextSr(new SrGenerationRequest
        {
            ContentText = "x",
            ReferencedInstances = new List<SrReferencedInstanceInput>
            {
                new() { StudyInstanceUid = "1.2.3.10", SeriesInstanceUid = "1.2.3.11", SopInstanceUid = "1.2.3.12", SopClassUid = "1.2.840.10008.5.1.4.1.1.2" },
                new() { StudyInstanceUid = "1.2.3.10", SeriesInstanceUid = "1.2.3.11", SopInstanceUid = "1.2.3.13", SopClassUid = "1.2.840.10008.5.1.4.1.1.2" }
            }
        }, FixedNow);

        Assert.True(SrSupport.IsStructuredReport(ds));

        var references = SrSupport.ExtractReferences(ds);
        Assert.Equal(2, references.Count);
        Assert.Contains(references, r => r.ReferencedSopInstanceUid == "1.2.3.12");
        Assert.Contains(references, r => r.ReferencedSopInstanceUid == "1.2.3.13");
        Assert.All(references, r => Assert.Equal("1.2.3.10", r.StudyInstanceUid));
    }

    [Fact]
    public void BuildBasicTextSr_ReportFieldsParseableBySrSupport()
    {
        var ds = SrGenerator.BuildBasicTextSr(new SrGenerationRequest
        {
            ContentText = "Findings text",
            DocumentTitle = "Report Title",
            CompletionFlag = "PARTIAL",
            VerificationFlag = "VERIFIED",
            ConceptCodeValue = "18748-4",
            ConceptCodingSchemeDesignator = "LN",
            ConceptCodeMeaning = "Diagnostic imaging study"
        }, FixedNow);

        var fields = SrSupport.Extract(ds);

        Assert.Equal("Report Title", fields.DocumentTitle);
        Assert.Equal("PARTIAL", fields.CompletionFlag);
        Assert.Equal("VERIFIED", fields.VerificationFlag);
        Assert.Equal("18748-4", fields.ConceptCodeValue);
        Assert.Equal("LN", fields.ConceptCodingSchemeDesignator);
        Assert.Equal("Diagnostic imaging study", fields.ConceptCodeMeaning);
        Assert.Equal("20240102", fields.ContentDate);
    }

    [Fact]
    public void BuildBasicTextSr_MissingContentText_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            SrGenerator.BuildBasicTextSr(new SrGenerationRequest { ContentText = "  " }, FixedNow));
    }

    [Fact]
    public void ExtractDocumentContent_FromGeneratedSr_ReturnsTree()
    {
        var ds = SrGenerator.BuildBasicTextSr(new SrGenerationRequest
        {
            DocumentTitle = "Report",
            ContentText = "Findings body",
            CompletionFlag = "COMPLETE",
            VerificationFlag = "UNVERIFIED"
        }, FixedNow);

        var document = SrSupport.ExtractDocumentContent(ds);

        Assert.Equal("Report", document.DocumentTitle);
        Assert.Equal("COMPLETE", document.CompletionFlag);
        Assert.Equal("UNVERIFIED", document.VerificationFlag);
        Assert.NotNull(document.ConceptName);

        Assert.NotNull(document.Root);
        Assert.Equal("CONTAINER", document.Root!.ValueType);
        Assert.Null(document.Root.RelationshipType);

        var text = Assert.Single(document.Root.Children);
        Assert.Equal("TEXT", text.ValueType);
        Assert.Equal("CONTAINS", text.RelationshipType);
        Assert.Equal("Findings body", text.TextValue);
        Assert.NotNull(text.ConceptName);
        Assert.Empty(text.Children);
    }
}

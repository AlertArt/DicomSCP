using DicomSCP.Services;
using Xunit;

namespace DicomSCP.Tests;

public class RadiotherapySupportTests
{
    [Theory]
    [InlineData("1.2.840.10008.5.1.4.1.1.481.1")] // RT Image
    [InlineData("1.2.840.10008.5.1.4.1.1.481.2")] // RT Dose
    [InlineData("1.2.840.10008.5.1.4.1.1.481.3")] // RT Structure Set
    [InlineData("1.2.840.10008.5.1.4.1.1.481.5")] // RT Plan
    [InlineData("1.2.840.10008.5.1.4.1.1.481.8")] // RT Ion Plan
    [InlineData("1.2.840.10008.5.1.4.1.1.481.9")] // RT Ion Beams Treatment Record
    public void IsRadiotherapy_RecognizesWhitelistedSopClasses(string sopClassUid)
    {
        Assert.True(RadiotherapySupport.IsRadiotherapy(sopClassUid));
    }

    [Theory]
    [InlineData("1.2.840.10008.5.1.4.1.1.2")]    // CT Image
    [InlineData("1.2.840.10008.5.1.4.1.1.88.11")] // Basic Text SR
    [InlineData("")]
    [InlineData(null)]
    public void IsRadiotherapy_RejectsOthers(string? sopClassUid)
    {
        Assert.False(RadiotherapySupport.IsRadiotherapy(sopClassUid));
    }
}

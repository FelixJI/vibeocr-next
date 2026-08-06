using VibeOCR.Platform.Inference;
using Xunit;

namespace VibeOCR.Platform.Tests;

public sealed class RuntimeCapabilityRequirementsTests
{
    [Fact]
    public void ReadsProductCapabilityBaseline()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(
                path,
                """{"required_capabilities":["ocr.recognition.v2","pdf.edit.v2"]}""");

            IReadOnlySet<string> requirements = RuntimeCapabilityRequirements.Read(path);

            Assert.Equal(2, requirements.Count);
            Assert.Contains("ocr.recognition.v2", requirements);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"required_capabilities":[]}""")]
    [InlineData("""{"required_capabilities":[1]}""")]
    [InlineData("""{"required_capabilities":["ocr.recognition.v2","ocr.recognition.v2"]}""")]
    public void RejectsMissingOrInvalidProductCapabilityBaseline(string contents)
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, contents);

            Assert.Throws<InvalidDataException>(() => RuntimeCapabilityRequirements.Read(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}

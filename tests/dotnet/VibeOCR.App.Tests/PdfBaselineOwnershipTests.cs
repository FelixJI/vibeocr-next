using Xunit;

namespace VibeOCR.App.Tests;

public class PdfBaselineOwnershipTests
{
  private static PdfBaselinePublication Publication(
      string session, string document, int? page, long? revision, string url, double at = 10) =>
    new(at, session, document, page, revision, url);

  [Fact]
  public void LowResFirstImageRemainsValidWhenHqPublishedLater()
  {
    var publications = new[]
    {
      Publication("s1", "d1", 0, 1, "https://res/low"),
      Publication("s1", "d1", 0, 1, "https://res/hq", at: 50),
    };
    (bool ok, string? reason) = PdfBaselineOwnership.ValidateWitness(
      publications, "s1", "d1", 0, 1, "https://res/low", frameOpportunityAt: 60);
    Assert.True(ok, reason);
    // 终态 HQ 也在同身份发布历史中：最终一致性可成立。
    (bool finalOk, string? finalReason) = PdfBaselineOwnership.FinalAgreement(
      publications, "s1", "d1", 0, 1, "https://res/hq", "https://res/hq",
      domComplete: true, domConnected: true, domHasLayout: true,
      hasFramedWitnessForUrl: true);
    Assert.True(finalOk, finalReason);
  }

  [Fact]
  public void SameDocumentDifferentSessionIsRejected()
  {
    var publications = new[]
    {
      Publication("s-old", "d1", 0, 1, "https://res/low"),
    };
    (bool ok, string? reason) = PdfBaselineOwnership.ValidateWitness(
      publications, "s-new", "d1", 0, 1, "https://res/low", frameOpportunityAt: 60);
    Assert.False(ok);
    Assert.Contains("not published", reason);
  }

  [Fact]
  public void SameSessionDifferentRevisionIsRejected()
  {
    var publications = new[]
    {
      Publication("s1", "d1", 0, 1, "https://res/low"),
    };
    (bool ok, _) = PdfBaselineOwnership.ValidateWitness(
      publications, "s1", "d1", 0, 2, "https://res/low", frameOpportunityAt: 60);
    Assert.False(ok);
  }

  [Fact]
  public void MissingPageOrRevisionInPublicationIsRejected()
  {
    var publications = new[]
    {
      Publication("s1", "d1", null, 1, "https://res/low"),
      Publication("s1", "d1", 0, null, "https://res/low"),
    };
    Assert.False(PdfBaselineOwnership.ValidateWitness(
      publications, "s1", "d1", 0, 1, "https://res/low", 60).Ok);
    Assert.False(PdfBaselineOwnership.IsPublishedGeneration(
      publications, "s1", "d1", 0, 1, "https://res/low"));
  }

  [Fact]
  public void PublicationAfterWitnessFrameIsNotBoundToIt()
  {
    var publications = new[] { Publication("s1", "d1", 0, 1, "https://res/low", at: 100) };
    (bool ok, string? reason) = PdfBaselineOwnership.ValidateWitness(
      publications, "s1", "d1", 0, 1, "https://res/low", frameOpportunityAt: 60);
    Assert.False(ok);
    Assert.Contains("not bound before witness frame", reason);
  }

  [Fact]
  public void NullFinalUrlOrDomMismatchIsRejectedWithoutPassThrough()
  {
    var publications = new[] { Publication("s1", "d1", 0, 1, "https://res/hq") };
    Assert.False(PdfBaselineOwnership.FinalAgreement(
      publications, "s1", "d1", 0, 1, null, null,
      true, true, true, true).Ok);
    Assert.False(PdfBaselineOwnership.FinalAgreement(
      publications, "s1", "d1", 0, 1, "https://res/hq", "https://res/stale",
      true, true, true, true).Ok);
    Assert.False(PdfBaselineOwnership.FinalAgreement(
      publications, "s1", "d1", 0, 1, "https://res/hq", "https://res/hq",
      domComplete: false, domConnected: true, domHasLayout: true,
      hasFramedWitnessForUrl: true).Ok);
    Assert.False(PdfBaselineOwnership.FinalAgreement(
      publications, "s1", "d1", 0, 1, "https://res/hq", "https://res/hq",
      true, true, true, hasFramedWitnessForUrl: false).Ok);
  }

  [Fact]
  public void OtherPageOrOtherSessionHqIsNotAColdUpgrade()
  {
    var publications = new[]
    {
      Publication("s1", "d1", 0, 1, "https://res/page0-hq"),
      Publication("s1", "d1", 3, 1, "https://res/page3-hq"),
      Publication("s2", "d2", 0, 1, "https://res/other-doc-hq"),
    };
    Assert.True(PdfBaselineOwnership.IsPublishedGeneration(
      publications, "s1", "d1", 0, 1, "https://res/page0-hq"));
    Assert.False(PdfBaselineOwnership.IsPublishedGeneration(
      publications, "s1", "d1", 0, 1, "https://res/page3-hq"),
      "other page HQ must not count as page-0 upgrade");
    Assert.False(PdfBaselineOwnership.IsPublishedGeneration(
      publications, "s1", "d1", 0, 1, "https://res/other-doc-hq"),
      "other document HQ must not count as this document's upgrade");
  }
}

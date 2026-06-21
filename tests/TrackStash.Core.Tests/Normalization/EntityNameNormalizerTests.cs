using TrackStash.Core.Normalization;
using Xunit;

namespace TrackStash.Core.Tests.Normalization;

public sealed class EntityNameNormalizerTests
{
    [Theory]
    [InlineData("Distinctive Records", "Distinct'ive Records")]
    [InlineData("En:Visiqn Recordings", "en:visiqn recordings")]
    [InlineData("One Dot Seven Three", "onedotseventhree")]
    [InlineData("Bozra Bozra", "BozraBozra")]
    public void NormalizeStrict_ReturnsSameKey_ForStylisticVariants(string left, string right)
    {
        var leftKey = EntityNameNormalizer.NormalizeStrict(left);
        var rightKey = EntityNameNormalizer.NormalizeStrict(right);

        Assert.Equal(leftKey, rightKey);
    }

    [Theory]
    [InlineData("Virelith Records", "virelithrecords", "virelith-records")]
    [InlineData("Distinctive Records", "distinctiverecords", "distinctive-records")]
    [InlineData("Distinct'ive Records", "distinctiverecords", "distinctive-records")]
    [InlineData("En:Visiqn Recordings", "envisiqnrecordings", "envisiqn-recordings")]
    public void NormalizeWithSlug_ReturnsStableNormalizedNameAndSlug(string value, string expectedNormalized, string expectedSlug)
    {
        var result = EntityNameNormalizer.NormalizeWithSlug(value);

        Assert.Equal(expectedNormalized, result.NormalizedName);
        Assert.Equal(expectedSlug, result.Slug);
    }

    [Fact]
    public void NormalizeWithSlug_ReturnsEmptyValues_ForBlankInput()
    {
        var result = EntityNameNormalizer.NormalizeWithSlug(null);

        Assert.Equal(string.Empty, result.NormalizedName);
        Assert.Equal(string.Empty, result.Slug);
    }

    [Theory]
    [InlineData("Virelith Records", "virelith")]
    [InlineData("Virelith Recordings", "virelith")]
    [InlineData("Virelith Music", "virelith")]
    [InlineData("Tilthic Music Limited", "tilthic")]
    [InlineData("Noys Music Ltd", "noys")]
    public void NormalizeLoose_RemovesConfiguredFillerTokens(string value, string expected)
    {
        var loose = EntityNameNormalizer.NormalizeLoose(value);

        Assert.Equal(expected, loose);
    }

    [Fact]
    public void SplitCompoundValues_SplitsOnConfiguredDelimiters()
    {
        const string raw = "Virgina\nFreestyle Dusk / Cheeky Orbit;Festival Orbit";

        var parts = EntityNameNormalizer.SplitCompoundValues(raw);

        Assert.Equal(4, parts.Count);
        Assert.Contains("Virgina", parts);
        Assert.Contains("Freestyle Dusk", parts);
        Assert.Contains("Cheeky Orbit", parts);
        Assert.Contains("Festival Orbit", parts);
    }

    [Theory]
    [InlineData(true, 0, 0, DuplicateResolutionAction.ReuseByExternalReference)]
    [InlineData(false, 1, 0, DuplicateResolutionAction.ReuseByStrictNormalization)]
    [InlineData(false, 2, 0, DuplicateResolutionAction.ReviewRequired)]
    [InlineData(false, 0, 1, DuplicateResolutionAction.ReviewRequired)]
    [InlineData(false, 0, 0, DuplicateResolutionAction.CreateNewCanonicalEntity)]
    public void DecideDuplicateResolutionAction_UsesExpectedPriority(
        bool hasExternalReferenceMatch,
        int strictMatchCount,
        int looseMatchCount,
        DuplicateResolutionAction expected)
    {
        var result = EntityNameNormalizer.DecideDuplicateResolutionAction(
            hasExternalReferenceMatch,
            strictMatchCount,
            looseMatchCount);

        Assert.Equal(expected, result);
    }
}

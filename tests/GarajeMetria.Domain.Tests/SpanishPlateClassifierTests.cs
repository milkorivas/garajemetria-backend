using GarajeMetria.Domain.Lpr;

namespace GarajeMetria.Domain.Tests;

public sealed class SpanishPlateClassifierTests
{
    public static TheoryData<string> Provinces => new(
        "A AB AL AV B BA BI BU C CA CC CE CO CR CS CU GC GI GR GU H HU IB J L LE LO LU M MA ML MU NA O OU P PO S SA SE SG SO SS T TE TF TO V VA VI Z ZA PM GE OR".Split(' '));
    public static TheoryData<string> HistoricalPrefixes => new("ALB", "CAC", "CAS", "SEG", "TER");
    public static TheoryData<string> StatePrefixes => new("ET", "FN", "EA", "MF", "MMA", "PME", "CNP", "PGC", "FAE");

    [Theory]
    [MemberData(nameof(Provinces))]
    public void EveryProvincialPrefixAcceptsBothSeriesAndRejectsExcessDigitsOrLetters(string prefix)
    {
        Assert.True(SpanishPlateClassifier.Classify(prefix + "000001").IsValid);
        Assert.True(SpanishPlateClassifier.Classify(prefix + "0123AE").IsValid);
        Assert.False(SpanishPlateClassifier.Classify(prefix + "1234567").IsValid);
        Assert.False(SpanishPlateClassifier.Classify(prefix + "0123ABCD").IsValid);
    }

    [Theory]
    [MemberData(nameof(HistoricalPrefixes))]
    public void ThreeLetterHistoricalPrefixesAreNumericOnly(string prefix)
    {
        Assert.Equal("spanish_provincial_numeric", SpanishPlateClassifier.Classify(prefix + "000001").Format);
        Assert.False(SpanishPlateClassifier.Classify(prefix + "1234567").IsValid);
        Assert.False(SpanishPlateClassifier.Classify(prefix + "0123AB").IsValid);
    }

    [Theory]
    [MemberData(nameof(StatePrefixes))]
    public void EveryStatePrefixAcceptsSpecialVariantAndRejectsMissingOrExcessIdentification(string prefix)
    {
        Assert.Equal("spanish_state", SpanishPlateClassifier.Classify(prefix + "00123AB").Format);
        Assert.Equal("spanish_state", SpanishPlateClassifier.Classify(prefix + "-VE-00123").Format);
        Assert.False(SpanishPlateClassifier.Classify(prefix + "VE").IsValid);
        Assert.False(SpanishPlateClassifier.Classify(prefix + "123456789").IsValid);
        Assert.False(SpanishPlateClassifier.Classify(prefix + "1ABCD").IsValid);
    }

    [Theory]
    [InlineData("0000BBB", "spanish_national")]
    [InlineData("E0000BBB", "spanish_special")]
    [InlineData("R0000BBB", "spanish_trailer")]
    [InlineData("C0000BBB", "spanish_moped")]
    [InlineData("H0000BBB", "spanish_historic")]
    [InlineData("T0000BBB", "spanish_tourist")]
    [InlineData("P0000BBB", "spanish_temporary")]
    [InlineData("S0000BBB", "spanish_temporary")]
    [InlineData("V0000BBB", "spanish_temporary")]
    [InlineData("CD-01-001", "spanish_diplomatic")]
    [InlineData("CC-01-001", "spanish_ambiguous")]
    [InlineData("OI-01-001", "spanish_diplomatic")]
    [InlineData("TA-01-001", "spanish_diplomatic")]
    [InlineData("E00123", "spanish_regional")]
    [InlineData("CGPC00123AB", "spanish_regional")]
    [InlineData("M0123O", "spanish_provincial_alphanumeric")]
    public void CatalogFamiliesAreClassifiedWithoutGuessingAmbiguousFamilies(string plate, string format)
    {
        var result = SpanishPlateClassifier.Classify(plate);
        Assert.True(result.IsValid);
        Assert.Equal(format, result.Format);
        Assert.Equal("es-v1", result.RulesVersion);
    }

    [Theory]
    [InlineData("0000AAA")]
    [InlineData("0000BBQ")]
    [InlineData("0000BBÑ")]
    [InlineData("0000BB0")]
    [InlineData("０000BBB")]
    [InlineData("unknown")]
    [InlineData("E0000AAA")]
    [InlineData("R0000AAA")]
    [InlineData("C0000AAA")]
    [InlineData("H0000AAA")]
    [InlineData("T0000AAA")]
    [InlineData("P0000AAA")]
    [InlineData("S0000AAA")]
    [InlineData("V0000AAA")]
    [InlineData("CD1")]
    [InlineData("CD1234567")]
    [InlineData("CC1234567")]
    [InlineData("OI1")]
    [InlineData("TA1")]
    [InlineData("E123456789")]
    [InlineData("CGPC123456789")]
    [InlineData("CGPC1ABCD")]
    [InlineData("ZZ0123AB")]
    [InlineData("E-0000-BBB-E")]
    public void InvalidFormatsAreRetainedWithOriginalAndNormalizedText(string original)
    {
        var result = SpanishPlateClassifier.Classify(original);
        Assert.False(result.IsValid);
        Assert.Null(result.Format);
        Assert.Equal(original, result.Original);
    }

    [Fact]
    public void NormalizationPreservesZerosAndOriginalWithoutCorrectingOOrRemovingPrefixes()
    {
        const string original = " m\u00a0-0012-o ";
        var result = SpanishPlateClassifier.Classify(original);
        Assert.Equal(original, result.Original);
        Assert.Equal("M0012O", result.Normalized);
        Assert.NotEqual("M00120", result.Normalized);
        Assert.Equal("E0000BBB", SpanishPlateClassifier.Classify("e-0000-bbb").Normalized);
        Assert.DoesNotContain(original, result.ToString());
        Assert.DoesNotContain(result.Normalized, result.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData(" -- ")]
    [InlineData("0000BBB\n")]
    [InlineData("\t0000BBB")]
    [InlineData("123456789012345678901234567890123")]
    public void MalformedTextIsAContractErrorRatherThanAnInvalidPlate(string original) =>
        Assert.Throws<ArgumentException>(() => SpanishPlateClassifier.Classify(original));

    [Fact]
    public void UnpairedSurrogatesAreRejectedBeforeNormalization() =>
        Assert.Throws<ArgumentException>(() => SpanishPlateClassifier.Classify(new string((char)0xd800, 1)));
}

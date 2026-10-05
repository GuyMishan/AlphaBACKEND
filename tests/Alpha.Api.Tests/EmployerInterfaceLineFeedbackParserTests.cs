using Alpha.Api.Services;
using Xunit;

namespace Alpha.Api.Tests;

public sealed class EmployerInterfaceLineFeedbackParserTests
{
    [Fact]
    public void Summary_feedback_parser_extracts_money_and_manufacturer_contribution_values()
    {
        var recordId = Guid.NewGuid().ToString("D").ToUpperInvariant();
        var transferId = Guid.NewGuid().ToString("D").ToUpperInvariant();
        var xml = $"""
            <Root>
              <StatosPirteiHaavaratKsafim>
                <MISPAR-ZIHUI>{transferId}</MISPAR-ZIHUI>
                <MISPAR-MISLAKA>{Guid.NewGuid():D}</MISPAR-MISLAKA>
                <TAARICH-ERECH-HAFKADA>2026-09-10</TAARICH-ERECH-HAFKADA>
                <TAARICH-ERECH-HAFKADA-CHESHBON-NEHEMANUT>2026-09-11</TAARICH-ERECH-HAFKADA-CHESHBON-NEHEMANUT>
                <MISPAR-ASMACHTA-LEAHAVARAT-KSAFIM>REF-1</MISPAR-ASMACHTA-LEAHAVARAT-KSAFIM>
                <SACH-HAFKADA-KUPA-H-P>1000.00</SACH-HAFKADA-KUPA-H-P>
                <SACH-HAFKADA-KLITA-BAPOAL>1000.00</SACH-HAFKADA-KLITA-BAPOAL>
                <SACH-KSAFIM-SHUICHU>900.00</SACH-KSAFIM-SHUICHU>
                <KSAFIM-BEMAHAVAR>100.00</KSAFIM-BEMAHAVAR>
                <HASHAVAT-KSAFIM-YEZUMA>0.00</HASHAVAT-KSAFIM-YEZUMA>
                <HASHAVAT-KSAFIM-CHESHBON-MAASIK>0.00</HASHAVAT-KSAFIM-CHESHBON-MAASIK>
                <STATUS-TIPUL-BEKSAFIM>3</STATUS-TIPUL-BEKSAFIM>
                <PERUT-STATUS>0</PERUT-STATUS>
                <TAARICH-NECHONUT>20260910123000</TAARICH-NECHONUT>
              </StatosPirteiHaavaratKsafim>
              <StatosPirteiKlitatReshuma>
                <MISPAR-MEZAHE-RESHUMA>{recordId}</MISPAR-MEZAHE-RESHUMA>
                <RESHUMA-NIKLETA>1</RESHUMA-NIKLETA>
                <SUG-SHGIHA>1</SUG-SHGIHA>
                <PERUT-SHGIHA-SHUM>0.00</PERUT-SHGIHA-SHUM>
                <OfenRishumZchuiot>
                  <SACHAR-MECHUSHAV>10000.00</SACHAR-MECHUSHAV>
                  <CHODESH-MASKORET>2026-09-01</CHODESH-MASKORET>
                  <MISPAR-POLISA-O-HESHBON>123</MISPAR-POLISA-O-HESHBON>
                  <SUG-HAFRASHA>2</SUG-HAFRASHA>
                  <SHIUR-HAFRASHA>6.00</SHIUR-HAFRASHA>
                  <SCHUM-HAFRASHA>600.00</SCHUM-HAFRASHA>
                </OfenRishumZchuiot>
              </StatosPirteiKlitatReshuma>
            </Root>
            """;

        var parsed = EmployerInterfaceLineFeedbackParser.ParseSummary(xml);

        var transfer = Assert.Single(parsed.Transfers);
        Assert.Equal(1000m, transfer.ActualReceivedAmount);
        Assert.Equal(900m, transfer.AllocatedAmount);
        Assert.Equal(100m, transfer.InTransitAmount);
        Assert.Equal(3, transfer.MoneyTreatmentStatus);

        var record = Assert.Single(parsed.Records);
        Assert.Equal(recordId, record.RecordIdentifier);
        Assert.Equal(1, record.IntakeStatus);
        Assert.Equal(1, record.ErrorCode);
        var rights = Assert.Single(record.Rights!);
        Assert.Equal(2, rights.ContributionTypeCode);
        Assert.Equal(10000m, rights.CalculatedSalary);
        Assert.Equal(6m, rights.ContributionRate);
        Assert.Equal(600m, rights.ContributionAmount);
    }

    [Fact]
    public void Summary_feedback_parser_preserves_multiple_rights_rows_in_order()
    {
        var recordId = Guid.NewGuid().ToString("D").ToUpperInvariant();
        var xml = $"""
            <Root>
              <StatosPirteiKlitatReshuma>
                <MISPAR-MEZAHE-RESHUMA>{recordId}</MISPAR-MEZAHE-RESHUMA>
                <RESHUMA-NIKLETA>1</RESHUMA-NIKLETA>
                <SUG-SHGIHA>1</SUG-SHGIHA>
                <OfenRishumZchuiot>
                  <SUG-HAFRASHA>2</SUG-HAFRASHA>
                  <SHIUR-HAFRASHA>6.00</SHIUR-HAFRASHA>
                  <SCHUM-HAFRASHA>600.00</SCHUM-HAFRASHA>
                </OfenRishumZchuiot>
                <OfenRishumZchuiot>
                  <SUG-HAFRASHA>3</SUG-HAFRASHA>
                  <SHIUR-HAFRASHA>6.50</SHIUR-HAFRASHA>
                  <SCHUM-HAFRASHA>650.00</SCHUM-HAFRASHA>
                </OfenRishumZchuiot>
              </StatosPirteiKlitatReshuma>
            </Root>
            """;

        var record = Assert.Single(EmployerInterfaceLineFeedbackParser.ParseSummary(xml).Records);

        Assert.Collection(record.Rights!,
            first =>
            {
                Assert.Equal(2, first.ContributionTypeCode);
                Assert.Equal(6m, first.ContributionRate);
                Assert.Equal(600m, first.ContributionAmount);
            },
            second =>
            {
                Assert.Equal(3, second.ContributionTypeCode);
                Assert.Equal(6.5m, second.ContributionRate);
                Assert.Equal(650m, second.ContributionAmount);
            });
    }

    [Fact]
    public void Try_parse_summary_rejects_simulated_json_without_throwing()
    {
        var ok = EmployerInterfaceLineFeedbackParser.TryParseSummary(
            """{"scenario":"mixed","feedbackInterface":"EMPFED"}""",
            out var parsed);

        Assert.False(ok);
        Assert.Empty(parsed.Transfers);
        Assert.Empty(parsed.Records);
    }

    [Theory]
    [InlineData(15, EmployerInterfaceLineFeedbackParser.FeedbackErrorScope.Deposit)]
    [InlineData(53, EmployerInterfaceLineFeedbackParser.FeedbackErrorScope.Contribution)]
    [InlineData(4, EmployerInterfaceLineFeedbackParser.FeedbackErrorScope.Employee)]
    [InlineData(45, EmployerInterfaceLineFeedbackParser.FeedbackErrorScope.Money)]
    [InlineData(28, EmployerInterfaceLineFeedbackParser.FeedbackErrorScope.Report)]
    [InlineData(31, EmployerInterfaceLineFeedbackParser.FeedbackErrorScope.Informational)]
    public void Error_scope_classifies_feedback_by_business_level(
        int code,
        EmployerInterfaceLineFeedbackParser.FeedbackErrorScope expected)
    {
        Assert.Equal(expected, EmployerInterfaceLineFeedbackParser.ErrorScope(code));
    }

    [Fact]
    public void Every_official_error_code_has_an_explicit_business_scope()
    {
        Assert.All(
            EmployerInterfaceLineFeedbackParser.OfficialErrorCodes,
            code => Assert.True(
                EmployerInterfaceLineFeedbackParser.HasExplicitErrorScope(code),
                $"Official error code {code} is missing an explicit business scope."));
    }

    [Fact]
    public void Only_success_and_corrective_match_are_informational()
    {
        var informational = EmployerInterfaceLineFeedbackParser.OfficialErrorCodes
            .Where(code => EmployerInterfaceLineFeedbackParser.ErrorScope(code)
                == EmployerInterfaceLineFeedbackParser.FeedbackErrorScope.Informational)
            .OrderBy(code => code)
            .ToArray();

        Assert.Equal(new[] { 1, 31 }, informational);
    }

    [Theory]
    [InlineData(31, "התנועה המקורית תקינה ומותאמת (עבור דיווח מתקן)")]
    [InlineData(53, "אין התאמה בין אחוז הפרשה, סכום הפרשה ושכר")]
    [InlineData(116, "הפקדה לקרן שאינה תואמת את מנגנון החלוקה בהתאם לספרת הביקורת בת.ז. של העמית")]
    public void Official_error_descriptions_cover_version_006_codes(int code, string expected)
    {
        Assert.Equal(expected, EmployerInterfaceLineFeedbackParser.Description(code));
    }
}

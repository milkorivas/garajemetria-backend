using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using GarajeMetria.Application.Authentication;
using GarajeMetria.Application.History;

namespace GarajeMetria.Infrastructure.History;

internal sealed class HistoryWorkbookWriter : IHistoryWorkbookWriter
{
    private readonly TimeZoneInfo zone = TimeZoneInfo.FindSystemTimeZoneById("Atlantic/Canary");
    public Task<Outcome<HistoryExport>> WriteAsync(HistoryPage page, HistoryKind kind, CancellationToken ct)
    {
        if (page.TotalCount > 10000 || page.Items.Count > 10000)
            return Task.FromResult(Outcome<HistoryExport>.Fail(FailureCode.HistoryExportLimitExceeded));
        using var buffer = new MemoryStream();
        using (var document = SpreadsheetDocument.Create(buffer, SpreadsheetDocumentType.Workbook, true))
        {
            var workbook = document.AddWorkbookPart();
            workbook.Workbook = new Workbook();
            var worksheet = workbook.AddNewPart<WorksheetPart>();
            var rows = new SheetData();
            string[] eventHeaders = ["Original plate", "Normalized plate", "Validity", "Captured at (Atlantic/Canary)",
                "Received at (Atlantic/Canary)", "Workshop", "Device", "Location", "Direction", "Confidence", "Stay ID"];
            string[] stayHeaders = ["Plate", "Workshop", "Entry (Atlantic/Canary)", "Exit (Atlantic/Canary)", "Duration (seconds)",
                "State", "Entry source", "Exit source", "Duration estimated"];
            var headers = kind == HistoryKind.Events ? eventHeaders : kind == HistoryKind.Timeline ?
                new[] { "Type" }.Concat(eventHeaders).Concat(stayHeaders).ToArray() : stayHeaders;
            rows.Append(Row(headers.Cast<object?>()));
            foreach (var item in page.Items)
            {
                ct.ThrowIfCancellationRequested();
                var e = item.Event;
                var s = item.Stay;
                object?[] events = [e?.PlateOriginal, e?.PlateNormalized, e?.PlateStatus, Local(e?.CapturedAtUtc), Local(e?.ReceivedAtUtc),
                    e?.Workshop.Name, e?.Device.Name, e?.Location.Description, e?.Direction,
                    e is null ? null : e.Confidence is null ? "No informada" : e.Confidence, e?.StayId?.ToString("D")];
                object?[] stays = [s?.PlateNormalized, s?.Workshop.Name, Local(s?.Entry?.AtUtc), Local(s?.Exit?.AtUtc),
                    s is null ? null : s.DurationSeconds is null ? "No disponible" : s.DurationSeconds,
                    s?.State, s?.Entry?.Source, s?.Exit?.Source, s?.DurationIsEstimated];
                rows.Append(Row(kind == HistoryKind.Events ? events : kind == HistoryKind.Timeline ?
                    new object?[] { item.Kind }.Concat(events).Concat(stays) : stays));
            }
            worksheet.Worksheet = new Worksheet(rows, new AutoFilter { Reference = $"A1:{Column(headers.Length)}{page.Items.Count + 1}" });
            workbook.Workbook.Append(new Sheets(new Sheet { Id = workbook.GetIdOfPart(worksheet), SheetId = 1, Name = "Results" }));
            var metadata = workbook.AddNewPart<WorksheetPart>();
            metadata.Worksheet = new Worksheet(new SheetData(Row(["Generated at", Local(page.UpdatedAtUtc)]),
                Row(["Time zone", "Atlantic/Canary"]), Row(["Rows", page.Items.Count])));
            workbook.Workbook.GetFirstChild<Sheets>()!.Append(new Sheet { Id = workbook.GetIdOfPart(metadata), SheetId = 2, Name = "Metadata" });
            workbook.Workbook.Save();
        }
        ct.ThrowIfCancellationRequested();
        if (buffer.Length > 32 * 1024 * 1024) return Task.FromResult(Outcome<HistoryExport>.Fail(FailureCode.HistoryExportSizeExceeded));
        return Task.FromResult(Outcome<HistoryExport>.Success(new(buffer.ToArray(), page.Items.Count)));
    }
    private string? Local(DateTimeOffset? instant) => instant is null ? null :
        TimeZoneInfo.ConvertTime(instant.Value, zone).ToString("yyyy-MM-dd'T'HH:mm:ss.fffffffzzz", CultureInfo.InvariantCulture);
    private static Row Row(IEnumerable<object?> values) => new(values.Select(value => value switch
    {
        decimal or int or long => new Cell { DataType = CellValues.Number, CellValue = new CellValue(Convert.ToString(value, CultureInfo.InvariantCulture)!) },
        bool boolean => new Cell { DataType = CellValues.Boolean, CellValue = new CellValue(boolean ? "1" : "0") },
        _ => new Cell { DataType = CellValues.InlineString, InlineString = new InlineString(new Text(value?.ToString() ?? "") { Space = SpaceProcessingModeValues.Preserve }) }
    }));
    private static string Column(int index)
    {
        var value = "";
        while (index > 0) { index--; value = (char)('A' + index % 26) + value; index /= 26; }
        return value;
    }
}

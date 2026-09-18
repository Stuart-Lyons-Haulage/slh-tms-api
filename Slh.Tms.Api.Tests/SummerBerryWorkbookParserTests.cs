using System.IO.Compression;
using System.Text;
using Slh.Tms.Api.Services;
using Xunit;
using IntakeParser = Slh.Tms.Api.Controllers.SpecialistMailboxOrderParser;

namespace Slh.Tms.Api.Tests;

public sealed class SummerBerryWorkbookParserTests
{
    [Fact]
    public void Morrisons_booking_keeps_summer_berry_as_customer_and_retailer_as_destination()
    {
        var workbook = Convert.ToBase64String(BuildWorkbook());
        var request = new MailboxEmailIntakeRequest(
            "summer-berry-19-sep",
            "<summer-berry-19-sep@example.test>",
            "info@lyonshaulage.com",
            "Ioana-Andreea.Pascalau@summerberry.co.uk",
            "Ioana-Andreea Pascalau",
            "ALDI & Morrisons -  19.09.2026",
            DateTimeOffset.Parse("2026-09-18T07:25:39Z"),
            "Morrisons - 19.09.2026\nALDI to follow later this afternoon\nPlease invoice with below PO: PORD000676",
            null,
            null,
            [new MailboxAttachmentRequest(
                "Morrisons  Aldi Bookings 19.09.2026.xlsm",
                "application/vnd.ms-excel.sheet.macroenabled.12",
                workbook)]);

        var result = new IntakeParser().TryParse(request);

        var order = Assert.Single(result!.Orders);
        var payload = order.Payload;
        Assert.Equal("SUMMERBERRY", payload.GetProperty("customerCode").GetString());
        Assert.Equal("MORRISONS", payload.GetProperty("retailerCode").GetString());
        Assert.Equal("MORRISONS", payload.GetProperty("marketName").GetString());
        Assert.Equal("MORRISONS SITTINGBOURNE", payload.GetProperty("stallNumber").GetString());
        Assert.Equal("SB-Groves Farm", payload.GetProperty("sellerName").GetString());
        Assert.Equal("PORD000676", payload.GetProperty("customerPo").GetString());
        Assert.Equal("2026-09-19", payload.GetProperty("collectionDate").GetString());
        Assert.Equal(10, payload.GetProperty("pallets").GetInt32());
        Assert.DoesNotContain(result.Orders, item =>
            string.Equals(item.Payload.GetProperty("retailerCode").GetString(), "ALDI", StringComparison.OrdinalIgnoreCase));
    }

    private static byte[] BuildWorkbook()
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(archive, "[Content_Types].xml", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
                  <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
                  <Default Extension="xml" ContentType="application/xml"/>
                  <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
                  <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
                  <Override PartName="/xl/sharedStrings.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sharedStrings+xml"/>
                  <Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/>
                </Types>
                """);
            Add(archive, "_rels/.rels", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
                </Relationships>
                """);
            Add(archive, "xl/workbook.xml", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
                  <sheets><sheet name="Bookings" sheetId="1" r:id="rId1"/></sheets>
                </workbook>
                """);
            Add(archive, "xl/_rels/workbook.xml.rels", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
                  <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/sharedStrings" Target="sharedStrings.xml"/>
                  <Relationship Id="rId3" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/>
                </Relationships>
                """);
            Add(archive, "xl/sharedStrings.xml", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <sst xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" count="11" uniqueCount="11">
                  <si><t>Collection Site</t></si><si><t>Date</t></si><si><t>Depot Description</t></si>
                  <si><t>Pallets</t></si><si><t>Request time</t></si><si><t>Available time</t></si>
                  <si><t>SB-Groves Farm</t></si><si><t>MORRISONS SITTINGBOURNE</t></si>
                  <si><t>SB-Groves Farm</t></si><si><t>ALDI GOLDTHORPE</t></si><si><t>unused</t></si>
                </sst>
                """);
            Add(archive, "xl/styles.xml", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
                  <fonts count="1"><font/></fonts><fills count="1"><fill><patternFill patternType="none"/></fill></fills>
                  <borders count="1"><border/></borders><cellStyleXfs count="1"><xf/></cellStyleXfs>
                  <cellXfs count="1"><xf xfId="0"/></cellXfs>
                </styleSheet>
                """);
            Add(archive, "xl/worksheets/sheet1.xml", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
                  <sheetData>
                    <row r="1">
                      <c r="A1" t="s"><v>0</v></c><c r="B1" t="s"><v>1</v></c><c r="C1" t="s"><v>2</v></c>
                      <c r="D1" t="s"><v>3</v></c><c r="E1" t="s"><v>4</v></c><c r="F1" t="s"><v>5</v></c>
                    </row>
                    <row r="2">
                      <c r="A2" t="s"><v>6</v></c>
                      <c r="B2"><v>46284</v></c>
                      <c r="C2" t="s"><v>7</v></c>
                      <c r="D2"><v>10</v></c>
                      <c r="E2"><v>0.2916666667</v></c>
                    </row>
                    <row r="3">
                      <c r="A3" t="s"><v>8</v></c>
                      <c r="B3"><v>46284</v></c>
                      <c r="C3" t="s"><v>9</v></c>
                    </row>
                  </sheetData>
                </worksheet>
                """);
        }

        return output.ToArray();
    }

    private static void Add(ZipArchive archive, string path, string content)
    {
        var entry = archive.CreateEntry(path);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }
}

using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Slh.Tms.Api.Models.Tracking;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class TachoMasterClientTests
{
    [Fact]
    public async Task Current_vehicle_status_uses_documented_duty_member_and_metric_fields()
    {
        var handler = new TachoMasterHandler();
        var client = new TachoMasterClient(new HttpClient(handler), new TachoMasterOptions
        {
            Enabled = true,
            BaseUrl = "https://api-v1-alpha.roadtech.co.uk",
            ApiKey = "test-key",
            Username = "planner",
            Password = "secret"
        }, NullLogger<TachoMasterClient>.Instance);

        var statuses = await client.GetCurrentDriverStatusesByVehicleAsync(new DateOnly(2026, 8, 14));

        var status = Assert.Single(statuses).Value;
        Assert.Equal("AB12CDE", Assert.Single(statuses).Key);
        Assert.Equal("Jane Driver", status.DriverName);
        Assert.Equal("DB123456789", status.CardNumber);
        Assert.Equal("SLH-42", status.EmployeeNumber);
        Assert.Equal(75, status.DriveMinutes);
        Assert.Equal(30, status.RestMinutes);
        Assert.Equal(1, status.BreakCount);
        Assert.Equal(30, status.BreakMinutes);
        Assert.Equal(285, status.DriveAvailableTodayMinutes);
        Assert.Equal(1560, status.DriveAvailableWeekMinutes);

        Assert.Equal(new[]
        {
            "/api/auth/login",
            "/api/Duty/GetDutyTransactions",
            "/api/Member/GetMembersLong",
            "/api/Member/GetMemberMetrics"
        }, handler.Paths);
        using var dutyRequest = JsonDocument.Parse(handler.BodiesByPath["/api/Duty/GetDutyTransactions"]);
        Assert.True(dutyRequest.RootElement.EnumerateObject().Single(property => string.Equals(property.Name, "WithWtd", StringComparison.OrdinalIgnoreCase)).Value.GetBoolean());
    }

    private sealed class TachoMasterHandler : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];
        public Dictionary<string, string> BodiesByPath { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Paths.Add(path);
            BodiesByPath[path] = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            var payload = request.RequestUri.AbsolutePath switch
            {
                "/api/auth/login" => "{\"token\":\"sid-123\"}",
                "/api/Duty/GetDutyTransactions" => """
                    {"dutyNew":{"moreData":false,"recordOffset":0,"recordCount":1,"data":[{
                      "memCode":42,"vehCode":"AB12 CDE","dutyStart":"2026-08-14T06:00:00Z","dutyEnd":null,
                      "timeWork":45,"timeRest":30,"timeAvailable":15,"timeDrive":75,
                      "wtd":[{"wtdEvent":"wtdBreak","timeStart":"2026-08-14T08:00:00Z","timeEnd":"2026-08-14T08:30:00Z"}]
                    }]}}
                    """,
                "/api/Member/GetMembersLong" => """
                    {"moreData":false,"recordOffset":0,"recordCount":1,"data":[{
                      "memCode":42,"cName":"Jane","sName":"Driver","cardNoShort":"DB123456789","employeeNumber":"SLH-42"
                    }]}
                    """,
                "/api/Member/GetMemberMetrics" => """
                    {"moreData":false,"recordOffset":0,"recordCount":1,"data":[{
                      "memCode":42,"dateTimeWhenValid":"2026-08-14T09:00:00Z","dailyDriverPeriodsAvaiable":1,
                      "driveAvailableToday":285,"driveAvailableTomorrow":600,"driveAvailableWeek":1560,
                      "driveAvailableFortnight":4260,"longDaysWorkedThisWeek":1,"shortDailyRestTakenThisWeek":0,
                      "workAvaiableWeek":2100
                    }]}
                    """,
                _ => throw new InvalidOperationException($"Unexpected test request {request.RequestUri}")
            };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
        }
    }
}

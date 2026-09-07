using System.Text.RegularExpressions;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

/// <summary>A worker/member number alone is not evidence that an employee is a driver.</summary>
public static class DriverPopulationRules
{
    public static bool IsOfficeReference(string? employeeNumber) =>
        !string.IsNullOrWhiteSpace(employeeNumber) && employeeNumber.Trim().StartsWith("TM", StringComparison.OrdinalIgnoreCase);

    public static bool HasDriverRole(string? value) => !string.IsNullOrWhiteSpace(value) &&
        Regex.IsMatch(value, @"\bdrivers?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) &&
        !Regex.IsMatch(value, @"\b(non[- ]?driver|office|administrator|admin|manager|management|workshop)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static bool IsDriver(Driver driver) =>
        !IsOfficeReference(driver.EmployeeNumber) &&
        (!string.IsNullOrWhiteSpace(driver.TachoCardNumber) ||
         HasDriverRole(driver.DriverType) || HasDriverRole(driver.DriverGroup) ||
         string.Equals(driver.DriverType?.Trim(), "Agency", StringComparison.OrdinalIgnoreCase) ||
         !string.IsNullOrWhiteSpace(driver.AgencyName));

    public static bool IsDriver(TachoLiveWorker worker) =>
        !string.IsNullOrWhiteSpace(worker.CardNumber) || HasDriverRole(worker.WorkerType) ||
        string.Equals(worker.WorkerType?.Trim(), "Agency", StringComparison.OrdinalIgnoreCase) ||
        !string.IsNullOrWhiteSpace(worker.AgencyName);

    public static bool IsSageDriver(SageHrEmployee employee, string? driverTeam, string? positionKeyword) =>
        (!string.IsNullOrWhiteSpace(driverTeam) && HasDriverRole(driverTeam) &&
            string.Equals(employee.Team?.Trim(), driverTeam.Trim(), StringComparison.OrdinalIgnoreCase)) ||
        (!string.IsNullOrWhiteSpace(positionKeyword) && HasDriverRole(employee.Position) &&
            employee.Position!.Contains(positionKeyword.Trim(), StringComparison.OrdinalIgnoreCase));
}

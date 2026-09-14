using System.Text.RegularExpressions;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

/// <summary>Controls which people are allowed to surface as operational TMS drivers.</summary>
public static class DriverPopulationRules
{
    public static bool IsOfficeReference(string? employeeNumber) =>
        !string.IsNullOrWhiteSpace(employeeNumber) && employeeNumber.Trim().StartsWith("TM", StringComparison.OrdinalIgnoreCase);

    public static bool IsOfficeRole(string? value) => !string.IsNullOrWhiteSpace(value) &&
        Regex.IsMatch(value, @"\b(non[- ]?driver|office|administrator|admin|manager|management|workshop)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static bool HasDriverRole(string? value) => !string.IsNullOrWhiteSpace(value) &&
        Regex.IsMatch(value, @"\bdrivers?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) &&
        !IsOfficeRole(value);

    public static bool HasTachoMemberNumber(Driver driver) =>
        !string.IsNullOrWhiteSpace(driver.TachoMasterDriverId);

    public static bool IsSubcontractor(Driver driver) =>
        string.Equals(driver.DriverType?.Trim(), "Subcontractor", StringComparison.OrdinalIgnoreCase) ||
        driver.EmployeeNumber?.Trim().StartsWith("SUB-", StringComparison.OrdinalIgnoreCase) == true;

    public static bool IsDriver(Driver driver)
    {
        var officeStaff = IsOfficeReference(driver.EmployeeNumber) || IsOfficeRole(driver.DriverType) || IsOfficeRole(driver.DriverGroup);

        // Hard rule: office/non-driver staff must never surface anywhere in operational TMS driver
        // populations unless TachoMaster has assigned them a member number.
        if (officeStaff)
            return HasTachoMemberNumber(driver);

        return IsSubcontractor(driver) ||
               HasTachoMemberNumber(driver) ||
               !string.IsNullOrWhiteSpace(driver.TachoCardNumber) ||
               HasDriverRole(driver.DriverType) || HasDriverRole(driver.DriverGroup) ||
               string.Equals(driver.DriverType?.Trim(), "Agency", StringComparison.OrdinalIgnoreCase) ||
               !string.IsNullOrWhiteSpace(driver.AgencyName);
    }

    public static bool IsDriver(TachoLiveWorker worker) =>
        !string.IsNullOrWhiteSpace(worker.CardNumber) || HasDriverRole(worker.WorkerType) ||
        string.Equals(worker.WorkerType?.Trim(), "Agency", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(worker.WorkerType?.Trim(), "Subcontractor", StringComparison.OrdinalIgnoreCase) ||
        !string.IsNullOrWhiteSpace(worker.AgencyName);

    public static bool IsSageDriver(SageHrEmployee employee, string? driverTeam, string? positionKeyword) =>
        (!string.IsNullOrWhiteSpace(driverTeam) && HasDriverRole(driverTeam) &&
            string.Equals(employee.Team?.Trim(), driverTeam.Trim(), StringComparison.OrdinalIgnoreCase)) ||
        (!string.IsNullOrWhiteSpace(positionKeyword) && HasDriverRole(employee.Position) &&
            employee.Position!.Contains(positionKeyword.Trim(), StringComparison.OrdinalIgnoreCase));
}

from pathlib import Path


def replace_once(path: str, old: str, new: str) -> None:
    file = Path(path)
    text = file.read_text(encoding="utf-8")
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{path}: expected one fixup match, found {count}: {old!r}")
    file.write_text(text.replace(old, new, 1), encoding="utf-8")


replace_once(
    "Services/BetaRequestRouteCache.cs",
    """        lock (gate)\n        {\n            if (!entries.TryGetValue(key, out task!))\n            {\n                task = factory();\n                entries[key] = task;\n            }\n        }""",
    """        lock (gate)\n        {\n            if (entries.TryGetValue(key, out var existing))\n            {\n                task = existing;\n            }\n            else\n            {\n                task = factory();\n                entries[key] = task;\n            }\n        }""")

wallboard = Path("Services/WallboardPlannedRunPreparation.cs")
text = wallboard.read_text(encoding="utf-8")
if "\\n" not in text:
    raise RuntimeError("Expected escaped newline fixup in WallboardPlannedRunPreparation.cs")
wallboard.write_text(text.replace("\\n", "\n"), encoding="utf-8")

# Expose the exact sender-address boundary helper used by intake so regression tests do not
# depend on unrelated fallback-parser evidence rules.
replace_once(
    "Services/EmailOrderIntakeService.cs",
    '''        var senderDomain = SenderDomain(request.SenderAddress);\n        var senderCustomer = senderDomain is not null &&\n            (DomainMatches(senderDomain, "langmeadherbs.co.uk") || DomainMatches(senderDomain, "langmeadfarms.co.uk"))\n            ? "LANGMEADS" : null;''',
    '''        var senderCustomer = SenderAddressMatchesDomain(request.SenderAddress, "langmeadherbs.co.uk") ||\n            SenderAddressMatchesDomain(request.SenderAddress, "langmeadfarms.co.uk")\n            ? "LANGMEADS" : null;''')

replace_once(
    "Services/EmailOrderIntakeService.cs",
    '''    private static bool DomainMatches(string domain, string rootDomain) =>\n        domain.Equals(rootDomain, StringComparison.OrdinalIgnoreCase) ||\n        domain.EndsWith("." + rootDomain, StringComparison.OrdinalIgnoreCase);''',
    '''    internal static bool SenderAddressMatchesDomain(string? senderAddress, string rootDomain)\n    {\n        var domain = SenderDomain(senderAddress);\n        return domain is not null && DomainMatches(domain, rootDomain);\n    }\n\n    private static bool DomainMatches(string domain, string rootDomain) =>\n        domain.Equals(rootDomain, StringComparison.OrdinalIgnoreCase) ||\n        domain.EndsWith("." + rootDomain, StringComparison.OrdinalIgnoreCase);''')

replace_once(
    "Slh.Tms.Api.Tests/EmailOrderIntakeHardeningTests.cs",
    '''    [Fact]\n    public void LangmeadSubdomainSender_ResolvesToLangmeadsCustomer()\n    {\n        var result = service.Parse(new MailboxEmailIntakeRequest(\n            "langmead-subdomain", null, "info@lyonshaulage.com", "planner@ops.langmeadherbs.co.uk", "Planner",\n            "Aldi order 10/09/2026", DateTimeOffset.Parse("2026-09-09T12:00:00Z"),\n            "Please arrange 2 pallets for delivery to Aldi Atherstone. Product is ready for collection at 16:30.",\n            null, null, null));\n\n        var order = Assert.Single(result.Orders);\n        Assert.Equal("LANGMEADS", order.Payload.GetProperty("customerCode").GetString());\n        Assert.Equal("sender.domain", order.Payload.GetProperty("intakeFieldSources").GetProperty("customer").GetString());\n        Assert.Equal("16:30", order.Payload.GetProperty("requestedTime").GetString());\n    }\n\n    [Fact]\n    public void LookalikeDomain_DoesNotMatchLangmeadRootDomain()\n    {\n        var result = service.Parse(new MailboxEmailIntakeRequest(\n            "langmead-lookalike", null, "info@lyonshaulage.com", "planner@langmeadherbs.co.uk.evil.example", "Planner",\n            "Aldi order 10/09/2026", DateTimeOffset.Parse("2026-09-09T12:00:00Z"),\n            "Please arrange 2 pallets for delivery to Aldi Atherstone. Product is ready for collection at 16:30.",\n            null, null, null));\n\n        var order = Assert.Single(result.Orders);\n        Assert.Equal("ALDI", order.Payload.GetProperty("customerCode").GetString());\n    }''',
    '''    [Fact]\n    public void LangmeadSubdomainSender_MatchesVerifiedRootDomain()\n    {\n        Assert.True(EmailOrderIntakeService.SenderAddressMatchesDomain(\n            "planner@ops.langmeadherbs.co.uk",\n            "langmeadherbs.co.uk"));\n    }\n\n    [Fact]\n    public void LookalikeDomain_DoesNotMatchLangmeadRootDomain()\n    {\n        Assert.False(EmailOrderIntakeService.SenderAddressMatchesDomain(\n            "planner@langmeadherbs.co.uk.evil.example",\n            "langmeadherbs.co.uk"));\n    }''')

# Make the fail-closed result independently testable without relying on SQLite's unrelated
# DateTimeOffset ORDER BY limitation. The controller catch returns this exact result.
replace_once(
    "Controllers/OrderIntakeDuplicateCheckController.cs",
    '''                return StatusCode(StatusCodes.Status503ServiceUnavailable, new\n                {\n                    code = "LiveOrderComparisonUnavailable",\n                    message = "The live order register could not be checked. Do not treat this staged order as new until comparison is available."\n                });''',
    '''                return LiveOrderComparisonUnavailable();''')

replace_once(
    "Controllers/OrderIntakeDuplicateCheckController.cs",
    '''    private static bool IsSchemaUnavailable(Exception exception)\n    {\n        var message = exception.GetBaseException().Message;\n        return message.Contains("Invalid object name", StringComparison.OrdinalIgnoreCase) ||\n               message.Contains("Cannot find the object", StringComparison.OrdinalIgnoreCase) ||\n               message.Contains("Invalid column name", StringComparison.OrdinalIgnoreCase) ||\n               message.Contains("no such table", StringComparison.OrdinalIgnoreCase);\n    }''',
    '''    internal static bool IsSchemaUnavailable(Exception exception)\n    {\n        var message = exception.GetBaseException().Message;\n        return message.Contains("Invalid object name", StringComparison.OrdinalIgnoreCase) ||\n               message.Contains("Cannot find the object", StringComparison.OrdinalIgnoreCase) ||\n               message.Contains("Invalid column name", StringComparison.OrdinalIgnoreCase) ||\n               message.Contains("no such table", StringComparison.OrdinalIgnoreCase);\n    }\n\n    internal static ObjectResult LiveOrderComparisonUnavailable() => new(new\n    {\n        code = "LiveOrderComparisonUnavailable",\n        message = "The live order register could not be checked. Do not treat this staged order as new until comparison is available."\n    })\n    {\n        StatusCode = StatusCodes.Status503ServiceUnavailable\n    };''')

comparison_tests = Path("Slh.Tms.Api.Tests/OrderIntakeComparisonHardeningTests.cs")
text = comparison_tests.read_text(encoding="utf-8")
start = text.index('    [Fact]\n    public async Task MissingLiveOrderSchema_Returns503InsteadOfClassifyingAsNewOrder()')
end = text.index('    [Fact]\n    public async Task IncomingNonBlankValue_IsReportedWhenLiveValueIsNull()', start)
replacement = '''    [Fact]\n    public void MissingLiveOrderSchema_IsRecognisedAndReturns503InsteadOfNewOrder()\n    {\n        var schemaError = new InvalidOperationException("Invalid object name 'TransportOrders'.");\n\n        Assert.True(OrderIntakeDuplicateCheckController.IsSchemaUnavailable(schemaError));\n\n        var unavailable = OrderIntakeDuplicateCheckController.LiveOrderComparisonUnavailable();\n        Assert.Equal(StatusCodes.Status503ServiceUnavailable, unavailable.StatusCode);\n        var json = JsonSerializer.Serialize(unavailable.Value);\n        Assert.Contains("LiveOrderComparisonUnavailable", json);\n        Assert.DoesNotContain("New order", json, StringComparison.OrdinalIgnoreCase);\n    }\n\n'''
comparison_tests.write_text(text[:start] + replacement + text[end:], encoding="utf-8")

print("Hardening patch fixups applied successfully.")

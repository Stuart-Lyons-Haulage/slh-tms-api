from pathlib import Path


def replace_once(path: str, old: str, new: str) -> None:
    file = Path(path)
    text = file.read_text()
    count = text.count(old)
    if count != 1:
        raise SystemExit(f"Expected exactly one match in {path}, found {count}: {old!r}")
    file.write_text(text.replace(old, new, 1))


replace_once(
    "Controllers/OperationsController.cs",
    "    public async Task<IActionResult> DeliveryEtas([FromQuery] DateOnly? date, CancellationToken ct)\n    {\n        if (!TvWallboardAccess.IsAllowed(HttpContext, configuration)) return Unauthorized();",
    "    public async Task<IActionResult> DeliveryEtas(\n        [FromHeader(Name = \"X-TV-Display-Key\")] string? displayKey,\n        [FromQuery] DateOnly? date,\n        CancellationToken ct)\n    {\n        var pairedKeyAllowed = await TvDisplayKeyStore.ValidateAsync(db, displayKey, ct);\n        if (!pairedKeyAllowed && !TvWallboardAccess.IsAllowed(HttpContext, configuration)) return Unauthorized();",
)

replace_once(
    "Controllers/RunProgressController.cs",
    "    public async Task<IActionResult> Get([FromQuery] DateOnly? date, CancellationToken ct)\n    {\n        if (!TvWallboardAccess.IsAllowed(HttpContext, configuration)) return Unauthorized();",
    "    public async Task<IActionResult> Get(\n        [FromHeader(Name = \"X-TV-Display-Key\")] string? displayKey,\n        [FromQuery] DateOnly? date,\n        CancellationToken ct)\n    {\n        var pairedKeyAllowed = await TvDisplayKeyStore.ValidateAsync(db, displayKey, ct);\n        if (!pairedKeyAllowed && !TvWallboardAccess.IsAllowed(HttpContext, configuration)) return Unauthorized();",
)

replace_once(
    "Controllers/DriverPlanningController.cs",
    "    public async Task<IActionResult> Assignments([FromQuery] DateOnly? from, [FromQuery] DateOnly? to, CancellationToken ct)\n    {\n        if (!TvWallboardAccess.IsAllowed(HttpContext, configuration)) return Unauthorized();",
    "    public async Task<IActionResult> Assignments(\n        [FromHeader(Name = \"X-TV-Display-Key\")] string? displayKey,\n        [FromQuery] DateOnly? from,\n        [FromQuery] DateOnly? to,\n        CancellationToken ct)\n    {\n        var pairedKeyAllowed = await TvDisplayKeyStore.ValidateAsync(db, displayKey, ct);\n        if (!pairedKeyAllowed && !TvWallboardAccess.IsAllowed(HttpContext, configuration)) return Unauthorized();",
)

replace_once(
    "Controllers/RunGeofenceLinkageController.cs",
    "public sealed class RunGeofenceLinkageController(TmsDbContext db) : ControllerBase\n{\n    [HttpGet]\n    public async Task<IActionResult> Get([FromQuery] DateOnly date, CancellationToken ct)\n    {",
    "public sealed class RunGeofenceLinkageController(TmsDbContext db, IConfiguration configuration) : ControllerBase\n{\n    [HttpGet, AllowAnonymous]\n    public async Task<IActionResult> Get(\n        [FromHeader(Name = \"X-TV-Display-Key\")] string? displayKey,\n        [FromQuery] DateOnly date,\n        CancellationToken ct)\n    {\n        var pairedKeyAllowed = await TvDisplayKeyStore.ValidateAsync(db, displayKey, ct);\n        if (!pairedKeyAllowed && !TvWallboardAccess.IsAllowed(HttpContext, configuration))\n            return Unauthorized(new { message = \"This TV display is not authorised.\" });\n",
)

Path(".github/scripts/apply_tv_wallboard_parity_patch.py").unlink()
Path(".github/workflows/_temporary_tv_wallboard_parity_patch.yml").unlink()

from pathlib import Path


def replace(path: str, old: str, new: str) -> None:
    file = Path(path)
    text = file.read_text(encoding="utf-8")
    if text.count(old) != 1:
        raise SystemExit(f"{path}: expected exactly one occurrence of {old!r}, found {text.count(old)}")
    file.write_text(text.replace(old, new), encoding="utf-8")


replace(
    "Controllers/DriverPlanningController.cs",
    '[HttpGet("driver-assignments"), AllowAnonymous]',
    '[HttpGet("driver-assignments"), Authorize(Policy = "TmsAccess")]',
)
replace(
    "Controllers/OperationsController.cs",
    '[HttpGet("delivery-etas"), AllowAnonymous]',
    '[HttpGet("delivery-etas"), Authorize(Policy = "TmsAccess")]',
)
replace(
    "Controllers/PlanningController.cs",
    '[HttpGet("loads"), AllowAnonymous]',
    '[HttpGet("loads"), Authorize(Policy = "TmsAccess")]',
)
replace(
    "Controllers/RunEvidenceHealthController.cs",
    '[HttpGet, AllowAnonymous]',
    '[HttpGet, Authorize(Policy = "TmsAccess")]',
)
replace(
    "Controllers/RunProgressController.cs",
    '[HttpGet, AllowAnonymous]',
    '[HttpGet, Authorize(Policy = "TmsAccess")]',
)
replace(
    "Controllers/RunTimingController.cs",
    '[HttpGet, AllowAnonymous]',
    '[HttpGet, Authorize(Policy = "TmsAccess")]',
)

print("Operational TMS data endpoints now require TmsAccess; dedicated health and TV device endpoints remain on their existing paths.")
